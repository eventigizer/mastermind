using System.IO;
using System.Net.NetworkInformation;
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
/// a PREVIOUS instance. The lock's FILENAME is the extension's IDE server port, so "is that port
/// listening" is the per-window test — that is the primary filter here, with the pid check kept as the
/// second condition. Both fail OPEN (an unparseable name, or an enumeration that fails) so a platform
/// change degrades to the old behaviour rather than blinding the panel.
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

    /// <summary>Every TCP port currently accepting connections — one syscall for the whole set, so the 8s
    /// poll pays no per-lock cost. Empty on failure, which the caller reads as "can't tell" (fail open).</summary>
    private static HashSet<int> ListeningPorts()
    {
        var set = new HashSet<int>();
        try
        {
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()) set.Add(ep.Port);
        }
        catch (Exception ex) { Diag.Warn("listeningPorts", ex); /* fail open on the pid check alone */ }
        return set;
    }

    /// <summary>The lock's own port, from its filename (<c>52818.lock</c> → 52818). 0 = not a port-named
    /// file, which the caller treats as untestable rather than dead.</summary>
    private static int LockPort(string lockFile)
        => int.TryParse(Path.GetFileNameWithoutExtension(lockFile), out var port) ? port : 0;

    /// <summary>Every lock whose port still listens — or every lock, when the listener set can't be read (fail open) —
    /// as (pid, the lock's JSON), across both lock folders. Pid 0: the lock could not be read or names no pid. The one
    /// lock walk: <see cref="OpenWorkspaceFolders"/> and <see cref="LiveLock"/> each apply their own pid test. Throws
    /// when a folder cannot be listed, so each caller keeps its own failure direction.</summary>
    private static List<(int Pid, JsonElement Doc)> ListeningLocks()
    {
        var locks = new List<(int Pid, JsonElement Doc)>();
        var ports = ListeningPorts();
        foreach (var dir in IdeDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var lockFile in Directory.GetFiles(dir, "*.lock"))
            {
                var port = LockPort(lockFile);
                if (port > 0 && ports.Count > 0 && !ports.Contains(port)) continue;   // nobody listening -> that window is gone
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(lockFile));
                    var root = doc.RootElement.Clone();
                    locks.Add((root.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pid) ? pid : 0, root));
                }
                catch { locks.Add((0, default)); }   // unreadable / mid-write lock, or not a JSON object
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
            foreach (var (pid, doc) in ListeningLocks())
            {
                if (!codePids.Contains(pid)) continue;     // stale lock from a PREVIOUS VS Code instance, or unreadable
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
    /// whatever its process is called: its port listens (when the listener set can be read) AND its pid is a live
    /// process; the configured name is never consulted, so a wrong processName cannot disarm it. A leaked lock fails one
    /// of the two — a leftover port re-used by an unrelated listener still names a dead pid. Fails toward a lock on an
    /// unreadable folder or lock: it guards a destructive write. Named for the refusal, since a JetBrains IDE or Zed
    /// running Claude writes the same lock and leaves no VS Code window to close.</summary>
    public static string LiveLock()
    {
        try
        {
            foreach (var (pid, doc) in ListeningLocks())
            {
                if (pid == 0) return "an unreadable IDE lock";
                if (Platform.Processes.IsAlive(pid))   // else a dead editor's leftover
                    return (doc.TryGetProperty("ideName", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() : "an IDE") + $" (pid {pid})";
            }
            return null;
        }
        catch { return "an unreadable IDE lock folder"; }
    }
}
