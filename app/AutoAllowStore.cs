using System.IO;
using System.Text;
using System.Text.Json;

namespace Mastermind;

/// <summary>
/// The per-session auto-allow policy: a <c>{session_id: pid}</c> map at <see cref="ClaudePaths.AutoAllowJson"/> — the
/// pid of the claude process the toggle was turned on for, 0 while none is known yet (a dispatch or session restore
/// arms a session before its process starts). When the asking process is the entry's, its PermissionRequest is
/// answered <c>allow</c> instead of prompting — by Mastermind's listener (<see cref="EventHub"/>) while it runs, by its
/// --hook run (<see cref="HookRun"/>) while it is closed — so the card's toggle is a per-session "stop asking me"
/// switch a resumed session never inherits.
///
/// One writer, Mastermind — its listener binds an entry known for no process and drops one set for a process that is
/// gone, off the UI thread under the same lock; the --hook run only reads. Only on entries are stored — toggling off
/// removes the key — so a missing key is off. Mirrors <see cref="StatusCache"/>: cached map, atomic save, fail-safe
/// (unreadable file == nobody auto-allowed).
/// </summary>
internal static class AutoAllowStore
{
    private static readonly object _lock = new();
    private static Dictionary<string, int> _map;   // session id → the claude pid it was turned on for; 0 = not known yet

    private static Dictionary<string, int> Map() => _map ??= Load();

    /// <summary>The map off disk: a pid per session — a <c>true</c> from an older Mastermind reads as not known
    /// yet.</summary>
    private static Dictionary<string, int> Load()
    {
        var map = new Dictionary<string, int>();
        foreach (var (sid, v) in Json.Load<Dictionary<string, JsonElement>>(ClaudePaths.AutoAllowJson) ?? new())
            if (v.ValueKind == JsonValueKind.True) map[sid] = 0;
            else if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var pid)) map[sid] = pid;
        return map;
    }

    /// <summary>True if this session is toggled to auto-allow — the card's switch; whether its process is answered is
    /// <see cref="Allows"/>. Missing file / key == off (fail-safe).</summary>
    public static bool IsOn(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        lock (_lock) return Map().ContainsKey(sessionId);
    }

    /// <summary>The allow answer — an HTTP hook's 2xx JSON body and a command hook's stdout alike, both read with the
    /// command hooks' output schema.</summary>
    public static readonly byte[] AllowBody = Encoding.UTF8.GetBytes(
        "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}");

    /// <summary>The one allow decision, the listener's and the --hook run's: the session is toggled on, for
    /// <paramref name="askingPid"/> or for no process yet. <paramref name="tidy"/> — the listener, Mastermind being the
    /// map's one writer — binds an entry known for no process to the asker, and drops one set for another process (a
    /// session resumed after its process died with no SessionEnd); the --hook run only reads.</summary>
    public static bool Allows(string sessionId, int askingPid, bool tidy)
    {
        if (string.IsNullOrEmpty(sessionId) || askingPid <= 0) return false;
        lock (_lock)
        {
            var map = Map();
            if (!map.TryGetValue(sessionId, out var stamp)) return false;
            bool allow = stamp == askingPid || stamp == 0;
            if (tidy && stamp != askingPid)
            {
                if (allow) map[sessionId] = askingPid; else map.Remove(sessionId);
                Json.SaveAtomic(ClaudePaths.AutoAllowJson, map);
            }
            return allow;
        }
    }

    /// <summary>A change a person or a dispatch made — the card's toggle, a new session's auto-allow, session restore's
    /// re-arm — raised by <see cref="Set"/> alone, never by <see cref="Remove"/> or <see cref="Sweep"/>, so session
    /// restore's record keeps the auto-allow a session had. Raised on the writer's thread, outside the lock.</summary>
    public static event Action<string, bool> Toggled;

    /// <summary>Turn auto-allow on/off for a session and persist. Off removes the key (no falsey clutter).</summary>
    public static void Set(string sessionId, bool on)
    {
        if (Write(sessionId, on)) Toggled?.Invoke(sessionId, on);
    }

    /// <summary>Auto-allow ON for a session started to run unattended — a dispatch under newDispatchAutoAllow, a session
    /// restore brings back — never in a hidden project, whose card never shows, so nothing could turn it off again.
    /// True when it was set.</summary>
    public static bool Arm(string sessionId, string cwd)
    {
        if (ProjectList.IsHidden(cwd)) return false;
        Set(sessionId, true);
        return true;
    }

    /// <summary>Clear a session's entry (on SessionEnd) so auto-allow never lingers onto a resumed id — session
    /// restore sets it again on a session it brings back, from its own record.</summary>
    public static void Remove(string sessionId) => Write(sessionId, false);

    /// <summary>The map write Set and Remove share; true when it changed. On stamps the session's live claude process —
    /// 0 while it has none yet.</summary>
    private static bool Write(string sessionId, bool on)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        int pid = on ? LiveSessions.Newest(sessionId)?.Pid ?? 0 : 0;
        lock (_lock)
        {
            var map = Map();
            bool had = map.ContainsKey(sessionId);
            if (had == on) return false;                 // no-op: don't rewrite the file for an unchanged toggle
            if (on) map[sessionId] = pid; else map.Remove(sessionId);
            Json.SaveAtomic(ClaudePaths.AutoAllowJson, map);
            return true;
        }
    }

    /// <summary>Drop every entry whose session no longer runs, or runs in another process than the one it was turned
    /// on for — a SessionEnd sent while Mastermind was down (a redeploy, a crash, a login before it started) is lost, and
    /// session ids survive a resume — and stamp an entry known for no process with the one running it. Run at startup.
    /// Returns how many entries went.</summary>
    public static int Sweep()
    {
        var live = new Dictionary<string, int>();
        foreach (var r in LiveSessions.Running())
            if (!live.ContainsKey(r.SessionId)) live[r.SessionId] = LiveSessions.Newest(r.SessionId)?.Pid ?? r.Pid;
        lock (_lock)
        {
            var map = Map();
            var gone = map.Where(kv => !live.TryGetValue(kv.Key, out var pid) || (kv.Value != 0 && kv.Value != pid))
                .Select(kv => kv.Key).ToList();
            foreach (var k in gone) map.Remove(k);
            var unbound = map.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();
            foreach (var k in unbound) map[k] = live[k];
            if (gone.Count + unbound.Count > 0) Json.SaveAtomic(ClaudePaths.AutoAllowJson, map);
            return gone.Count;
        }
    }
}
