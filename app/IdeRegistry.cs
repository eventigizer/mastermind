using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Mastermind;

/// <summary>
/// Reads the Claude Code IDE lock files (<c>~/.claude/ide/*.lock</c>) — one per VS Code window running
/// the extension — to learn which workspace folders are open. Authoritative and multi-root aware, unlike
/// scraping window titles (which can't see a multi-root workspace's folders at all).
///
/// Lock files leak — a closed or killed window leaves its lock behind — and the lock's own <c>pid</c> is
/// NOT enough to spot that: it is the SHARED VS Code main process, which every window of one instance
/// reports, so every lock written during that instance's lifetime keeps passing a pid check long after its
/// window is gone. It only catches locks left by
/// a PREVIOUS instance. The lock's FILENAME is the extension's IDE server port, so "does that port accept a
/// connection" is the per-window test, run after the pid check so a dead instance's lock costs no connect. Never the
/// OS listener table, which can omit every ordinary listener. An unparseable name or an unexpected probe failure
/// fails OPEN, so a platform change degrades to the pid check rather than blinding the panel.
/// </summary>
internal static class IdeRegistry
{
    // Both lock folders: Claude Code's IDE integration was reported writing its locks to ~/.claude/ide whatever
    // CLAUDE_CONFIG_DIR says, so a custom Claude folder (config's claudeDir) is read beside the default, never instead.
    private static IEnumerable<string> IdeDirs => new[]
    {
        ClaudePaths.Ide,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ide"),
    }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);

    // Windows refuses a closed loopback port only after about 2 s of SYN retries, so the UI-thread poll caps the wait
    // and holds the answer. The cap cannot tell that port from a stalled live one, so the connect runs on past it and
    // clears the hold if it lands.
    private const int ConnectCapMs = 100;
    private const int SureCapMs = 3000;
    private static readonly TimeSpan RefusalHeld = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<int, DateTime> Refused = new();

    private static Task Connect(int port)
    {
        var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, port);
        connect.ContinueWith(t =>
        {
            client.Dispose();
            if (t.IsCompletedSuccessfully) Refused.TryRemove(port, out _);
            else _ = t.Exception;   // a refusal past the cap, observed
        }, TaskContinuationOptions.ExecuteSynchronously);
        return connect;
    }

    private static bool IsRefusal(AggregateException ex) =>
        ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused };

    /// <summary>The poll's test: true when something accepts a loopback connection on the port, or the probe itself
    /// failed (fail open). A refusal or a wait past the cap is remembered, so a leaked lock costs one capped wait per
    /// hold.</summary>
    private static bool Answers(int port)
    {
        if (Refused.TryGetValue(port, out var until) && DateTime.UtcNow < until) return false;
        Task connect = null;
        try
        {
            connect = Connect(port);
            if (connect.Wait(ConnectCapMs)) return true;
        }
        catch (AggregateException ex) when (IsRefusal(ex)) { }
        catch (Exception ex) { Diag.Warn("ideLockPort", ex); return true; }
        Refused[port] = DateTime.UtcNow + RefusalHeld;
        if (connect == null || !connect.IsCompletedSuccessfully) return false;
        Refused.TryRemove(port, out _);   // it landed before the hold went in
        return true;
    }

    /// <summary>The destructive guard's test: only a real refusal counts as closed, past the poll's cap and hold, so a
    /// stalled port stays a lock.</summary>
    private static bool AnswersSurely(int port)
    {
        try { Connect(port).Wait(SureCapMs); return true; }
        catch (AggregateException ex) when (IsRefusal(ex)) { return false; }
        catch (Exception ex) { Diag.Warn("ideLockPort", ex); return true; }
    }

    /// <summary>The lock's own port, from its filename (<c>52818.lock</c> → 52818). 0 = not a port-named
    /// file, which the caller treats as untestable rather than dead.</summary>
    private static int LockPort(string lockFile)
        => int.TryParse(Path.GetFileNameWithoutExtension(lockFile), out var port) ? port : 0;

    /// <summary>Every lock whose pid passes <paramref name="pidOwned"/> and whose port passes <paramref name="portAnswers"/>,
    /// as (pid, the lock's JSON), across both lock folders. Pid 0: the lock could not be read or names no pid — it skips
    /// the pid test. The one lock walk: <see cref="OpenWorkspaceFolders"/> and <see cref="LiveLock"/> each pass their own
    /// tests. Throws when a folder cannot be listed, so each caller keeps its own failure direction.</summary>
    private static List<(int Pid, JsonElement Doc)> LiveLocks(Func<int, bool> pidOwned, Func<int, bool> portAnswers)
    {
        var locks = new List<(int Pid, JsonElement Doc)>();
        foreach (var dir in IdeDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var lockFile in Directory.GetFiles(dir, "*.lock"))
            {
                (int Pid, JsonElement Doc) entry = (0, default);   // unreadable / mid-write lock, or not a JSON object
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(lockFile));
                    var root = doc.RootElement.Clone();
                    entry = (root.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pid) ? pid : 0, root);
                }
                catch { }
                if (entry.Pid != 0 && !pidOwned(entry.Pid)) continue;
                var port = LockPort(lockFile);
                if (port > 0 && !portAnswers(port)) continue;   // nobody answers -> that window is gone
                locks.Add(entry);
            }
        }
        return locks;
    }

    /// <summary>Full paths of every workspace folder open in a live VS Code window.</summary>
    public static HashSet<string> OpenWorkspaceFolders()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codePids = EditorIdentities.ProcessNames().SelectMany(n => Platform.Processes.LivePids(n)).ToHashSet();
        if (codePids.Count == 0) return folders;          // VS Code not running -> nothing open

        try
        {
            foreach (var (pid, doc) in LiveLocks(codePids.Contains, Answers))   // a stale lock from a PREVIOUS VS Code instance fails the pid test
            {
                if (pid == 0) continue;                    // unreadable
                if (doc.TryGetProperty("workspaceFolders", out var wf) && wf.ValueKind == JsonValueKind.Array)
                    foreach (var f in wf.EnumerateArray())
                        if (f.ValueKind == JsonValueKind.String)
                        {
                            var s = f.GetString();
                            if (!string.IsNullOrEmpty(s)) folders.Add(s);
                        }
            }
        }
        catch (Exception ex) { Diag.Warn("ideLocks", ex); /* best effort */ }
        return folders;
    }

    /// <summary>The first lock that still belongs to a running editor, as "&lt;ideName&gt; (pid N)", or null when none does —
    /// whatever its process is called: its port answers AND its pid is a live
    /// process; the configured name is never consulted, so a wrong processName cannot disarm it. A leaked lock fails one
    /// of the two — a leftover port re-used by an unrelated listener still names a dead pid. Fails toward a lock on an
    /// unreadable folder or lock: it guards a destructive write. Named for the refusal, since a JetBrains IDE or Zed
    /// running Claude writes the same lock and leaves no VS Code window to close.</summary>
    public static string LiveLock()
    {
        try
        {
            foreach (var (pid, doc) in LiveLocks(Platform.Processes.IsAlive, AnswersSurely))   // a dead editor's leftover fails the pid test
            {
                if (pid == 0) return "an unreadable IDE lock";
                return (doc.TryGetProperty("ideName", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() : "an IDE") + $" (pid {pid})";
            }
            return null;
        }
        catch { return "an unreadable IDE lock folder"; }
    }
}
