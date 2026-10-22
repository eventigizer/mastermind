using System.IO;
using System.Text.Json;

namespace Mastermind;

/// <summary>One running Claude Code agent, read from its <c>~/.claude/sessions/&lt;pid&gt;.json</c> file.</summary>
internal readonly record struct LiveSession(int Pid, string SessionId, string Cwd);

/// <summary>A live agent's own busy / idle state off its registry file: <c>status</c> is <c>busy</c>, <c>idle</c> or
/// <c>waiting</c> (blocked on the user), <c>At</c> its <c>statusUpdatedAt</c> (unix ms). Empty status / 0 = an engine
/// older than the field.</summary>
internal readonly record struct RunState(string Status, long At);

/// <summary>
/// Reads <c>~/.claude/sessions/*.json</c> — one file per RUNNING Claude CLI process (named
/// <c>&lt;pid&gt;.json</c>, carrying sessionId + cwd + pid). This is the live-agent registry: an agent has
/// to be a live process to ask permission or finish, so it's the exact set Mastermind should show — unlike
/// the older "every non-hidden session in every open window" working-set view, which listed historical
/// sessions that can never produce a hook event.
///
/// The CLI prunes a file on a clean exit, but a hard-killed claude (taskkill / crash) leaves its file
/// behind, so we pid-filter against live <c>claude</c> processes. The process-name check (not just "is the
/// pid alive") also defends against pid reuse — a dead claude's pid handed to some other process.
/// </summary>
internal static class LiveSessions
{
    private static string SessionsDir => ClaudePaths.Sessions;

    /// <summary>One registry file, pid-filtered: the raw row every view below narrows.</summary>
    private readonly record struct Entry(int Pid, string SessionId, string Cwd, string FilePath, RunState Run);

    private static volatile IReadOnlyDictionary<string, (string Cwd, RunState Run)> _snapshot =
        new Dictionary<string, (string Cwd, RunState Run)>(StringComparer.Ordinal);

    /// <summary>Every live agent at the last registry read, by session id — its launch cwd and busy / idle state. The
    /// reconciler's 8 s tick keeps it fresh, so the hook path and the symptom check never list processes per event.</summary>
    public static IReadOnlyDictionary<string, (string Cwd, RunState Run)> Snapshot => _snapshot;

    /// <summary>A live agent's launch cwd at the last registry read, or null — the project identity a hook event takes,
    /// since the payload's cwd follows the agent's shell <c>cd</c>.</summary>
    public static string CwdOf(string sessionId) =>
        !string.IsNullOrEmpty(sessionId) && _snapshot.TryGetValue(sessionId, out var s) && !string.IsNullOrEmpty(s.Cwd)
            ? s.Cwd : null;

    /// <summary>The registry read every view shares, publishing <see cref="Snapshot"/> on the way.</summary>
    private static List<Entry> Read()
    {
        var result = ReadRegistry();
        var map = new Dictionary<string, (string Cwd, RunState Run)>(StringComparer.Ordinal);
        foreach (var e in result) map[e.SessionId] = (e.Cwd, e.Run);
        _snapshot = map;
        return result;
    }

    /// <summary>Every registry file whose pid is a live <c>claude</c> — the ONE reader the views share. No
    /// hidden / ghost filter here: each view applies the ones it needs. A mid-write or unreadable file is
    /// skipped; any directory-level failure yields what was read so far (callers treat short as "fewer agents").</summary>
    private static List<Entry> ReadRegistry()
    {
        var result = new List<Entry>();
        var livePids = Platform.Processes.LivePids("claude");
        if (livePids.Count == 0) return result;          // no claude running -> nothing live
        try
        {
            if (!Directory.Exists(SessionsDir)) return result;
            foreach (var file in Directory.GetFiles(SessionsDir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var pid)) continue;
                    if (!livePids.Contains(pid)) continue;        // stale file from a dead/killed claude
                    var sid = Json.Str(r, "sessionId");
                    if (string.IsNullOrEmpty(sid)) continue;
                    result.Add(new Entry(pid, sid, Json.Str(r, "cwd"), file,
                        new RunState(Json.Str(r, "status"), Json.Long(r, "statusUpdatedAt"))));
                }
                catch { /* skip unreadable / mid-write file */ }
            }
        }
        catch (Exception ex) { Diag.Warn("liveSessions", ex); /* best effort */ }
        return result;
    }

    /// <summary>Every currently-running Claude agent the panel shows (pid + session id + cwd).</summary>
    public static List<LiveSession> Active()
    {
        var result = new List<LiveSession>();
        var entries = Read();
        if (entries.Count == 0) return result;
        var hidden = VsCodeState.HiddenSessionIdsCached();   // sessions the user hid in the extension list
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Cwd)) continue;
            if (hidden.Contains(e.SessionId)) continue;           // hidden in the extension list -> don't show
            if (!Sessions.HasTranscript(e.SessionId, e.Cwd)) continue;  // ghost: live pid but transcript deleted -> nothing to show
            result.Add(new LiveSession(e.Pid, e.SessionId, e.Cwd));
        }
        return result;
    }

    /// <summary>Session ids of every live agent — the reconciler's prune/diff key.</summary>
    public static HashSet<string> ActiveIds()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in Active()) set.Add(s.SessionId);
        return set;
    }

    /// <summary>Each live agent's busy / idle state, by session id — the reconciler's correction input. Unfiltered:
    /// the store only touches sessions that already have a card.</summary>
    public static Dictionary<string, RunState> RunStates()
    {
        var map = new Dictionary<string, RunState>(StringComparer.Ordinal);
        foreach (var e in Read()) map[e.SessionId] = e.Run;
        return map;
    }

    /// <summary>
    /// Session ids of every RUNNING claude — pid-filtered ONLY, no hidden/ghost filters. This is the
    /// "is this agent alive at all" set the prompt-inbox orphan check needs: <see cref="ActiveIds"/> excludes
    /// ARCHIVED sessions (archive = hide, the process keeps running and keeps reading), so keying owner-alive
    /// on it falsely orphaned an in-flight prompt — and offered `delete` on a file a live agent owns — the
    /// moment its owner was archived. Same for the transcript/ghost filter: no transcript ≠ dead process.
    /// </summary>
    public static HashSet<string> RawActiveIds()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Read()) set.Add(e.SessionId);
        return set;
    }

    /// <summary>Every running claude launched in <paramref name="root"/> — its pid and session id, pid-filtered only. A
    /// port-off `new` dispatch's session is the one this list gains.</summary>
    public static List<(int Pid, string SessionId)> RunningIn(string root)
    {
        var key = Paths.Key(root);
        var list = new List<(int Pid, string SessionId)>();
        foreach (var e in Read())
            if (!string.IsNullOrEmpty(e.Cwd) && Paths.Key(e.Cwd) == key) list.Add((e.Pid, e.SessionId));
        return list;
    }

    /// <summary>Every running claude's pid, session id and busy / idle state — pid-filtered only, no archive or transcript
    /// filter: an archived or transcript-less session still runs, and VS Code health's busy gate must see it.</summary>
    public static List<(int Pid, string SessionId, RunState Run)> Running()
    {
        var result = new List<(int Pid, string SessionId, RunState Run)>();
        foreach (var e in Read()) result.Add((e.Pid, e.SessionId, e.Run));
        return result;
    }

    /// <summary>
    /// Every RUNNING claude agent whose session the user TRASHED (hidden) — the reap candidate set. The
    /// inverse of <see cref="Active"/>'s hidden filter: keep an entry ONLY when its session id is in the
    /// FRESH hidden set (not the 15s cache — a just-trashed session must count). The transcript/ghost filter
    /// is dropped on purpose. Carries the exact registry file path so the caller sweeps what it read.
    /// Best-effort: an unreadable hidden set yields an empty list (reap nothing on a bad read).
    /// </summary>
    public static List<(int Pid, string SessionId, string Cwd, string FilePath)> HiddenLive()
    {
        var result = new List<(int Pid, string SessionId, string Cwd, string FilePath)>();
        var entries = Read();
        if (entries.Count == 0) return result;
        HashSet<string> hidden;
        try { hidden = VsCodeState.HiddenSessionIds(); } catch { return result; }   // fresh, uncached
        foreach (var e in entries)
            if (hidden.Contains(e.SessionId)) result.Add((e.Pid, e.SessionId, e.Cwd, e.FilePath));
        return result;
    }

    /// <summary>The running claude holding one session — its pid, the registry file read and its busy / idle state —
    /// or null when no live process holds it. The card archive's click-time check and kill target.</summary>
    public static (int Pid, string FilePath, RunState Run)? Find(string sessionId)
    {
        foreach (var e in Read())
            if (e.SessionId == sessionId) return (e.Pid, e.FilePath, e.Run);
        return null;
    }

    /// <summary>The newest running claude holding one session — an extension restart can leave the old process running
    /// for minutes beside the one that resumed it (extension 2.1.307) — or null when none does. The auto-allow stamp's
    /// process.</summary>
    public static (int Pid, string FilePath, RunState Run)? Newest(string sessionId)
    {
        (int Pid, string FilePath, RunState Run)? newest = null;
        DateTime newestAt = DateTime.MinValue;
        foreach (var e in Read())
            if (e.SessionId == sessionId)
            {
                var at = Platform.Processes.StartedAt(e.Pid) ?? DateTime.MinValue;
                if (newest == null || at > newestAt) { newest = (e.Pid, e.FilePath, e.Run); newestAt = at; }
            }
        return newest;
    }

    /// <summary>
    /// Kill-time re-verify (closes the pid-reuse window that opens while the confirm dialog is up): the
    /// registry file must STILL exist, still carry this pid, and name a session STILL in the fresh hidden
    /// set. If a different claude took the pid — its startup overwrote &lt;pid&gt;.json with a non-hidden sid
    /// — this returns false, so a reused pid on a live, untrashed session is never killed. Fail → false.
    /// </summary>
    public static bool StillTrashed(int pid, string filePath, HashSet<string> hidden)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            var r = doc.RootElement;
            if (!r.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var filePid) || filePid != pid)
                return false;
            var sid = Json.Str(r, "sessionId");
            return !string.IsNullOrEmpty(sid) && hidden.Contains(sid);
        }
        catch { return false; }
    }

}
