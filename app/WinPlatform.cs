using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Mastermind;

/// <summary>Run a platform call on the UI thread through the context it was handed — inline when already there, or
/// when none was handed (a probe harness has no UI thread). A throw inside comes back to the caller: the dispatcher
/// would otherwise hand it to App's unhandled-exception handler, which swallows it, and the caller would read success.</summary>
internal static class UiCall
{
    public static T Run<T>(SynchronizationContext ui, Func<T> f)
    {
        if (ui == null) return f();
        T result = default;
        Exception error = null;
        ui.Send(_ => { try { result = f(); } catch (Exception ex) { error = ex; } }, null);
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return result;
    }
}

/// <summary>
/// Editor windows on Windows: the editor's visible top-level windows (config's editor.processName), raised from OUR
/// process (<see cref="Native.ForceForeground"/>). Every raise runs on the UI thread through the context this was
/// handed — ForceForeground's AttachThreadInput attaches the CALLING thread's input queue, and from a queue-less worker
/// that is the intermittent-Win32 corner — so no caller marshals for it. A null context (a probe harness) runs inline.
/// </summary>
internal sealed class WinEditorWindows : IEditorWindows
{
    private readonly SynchronizationContext _ui;
    public WinEditorWindows(SynchronizationContext ui) { _ui = ui; }

    private T OnUi<T>(Func<T> f) => UiCall.Run(_ui, f);

    public void GrantForeground() => Native.AllowAnyForeground();

    public RaiseResult RaiseProject(string cwd) => OnUi(() =>
    {
        var hwnd = FindWindowForCwd(cwd);
        if (hwnd == IntPtr.Zero) return RaiseResult.NotFound;
        return Native.ForceForeground(hwnd) ? RaiseResult.Raised : RaiseResult.Refused;
    });

    /// <summary>Exact-only on purpose: the caller (the CDP switch) took the title from the matched sidebar's host page
    /// target, verified live to byte-match the OS window title, so this raises the precise window with none of the
    /// AllowSetForegroundWindow-grant fragility of a Code.exe-side <c>Page.bringToFront</c>. Fuzzy scoring stays in
    /// <see cref="ScoreTitle"/> for the no-CDP fallback. NotFound = title drift, Refused = Windows kept it back — the
    /// caller falls back to the CDP raise on either.</summary>
    public RaiseResult RaiseByTitle(string title) => OnUi(() =>
    {
        if (string.IsNullOrEmpty(title)) return RaiseResult.NotFound;
        IntPtr hit = IntPtr.Zero;
        var names = EditorIdentities.ProcessNames();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (!IsEditor(pid, names)) return true;
            if (!string.Equals(TitleOf(h), title, StringComparison.Ordinal)) return true;
            hit = h;
            return false;   // found it — stop enumerating
        }, IntPtr.Zero);
        if (hit == IntPtr.Zero) return RaiseResult.NotFound;
        return Native.ForceForeground(hit) ? RaiseResult.Raised : RaiseResult.Refused;
    });

    public bool AnyOpen()
    {
        bool any = false;
        var names = EditorIdentities.ProcessNames();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (!IsEditor(pid, names) || Native.GetWindowTextLength(h) == 0) return true;
            any = true;
            return false;   // found one — stop enumerating
        }, IntPtr.Zero);
        return any;
    }

    private static bool IsEditor(uint pid, IReadOnlyCollection<string> names)
    {
        try
        {
            using var pr = Process.GetProcessById((int)pid);
            return names.Contains(pr.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string TitleOf(IntPtr h)
    {
        int len = Native.GetWindowTextLength(h);
        if (len == 0) return "";
        var sb = new StringBuilder(len + 1);
        Native.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>The visible editor window whose title best matches the project folder. A title looks like
    /// "● file.ts - my-app (Workspace) - Visual Studio Code"; we score on the cwd leaf.</summary>
    private static IntPtr FindWindowForCwd(string cwd)
    {
        var leaf = Paths.Leaf(cwd);
        if (string.IsNullOrEmpty(leaf)) return IntPtr.Zero;
        // A card's raise looks among its own window's editor: VS Code and a look-alike open on one project would
        // otherwise score alike, and the first enumerated would win.
        var only = EditorIdentities.ProcessNameFor(cwd);
        IReadOnlyCollection<string> names = only != null ? new[] { only } : EditorIdentities.ProcessNames();
        IntPtr best = IntPtr.Zero;
        int bestScore = 0;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (!IsEditor(pid, names)) return true;
            int score = ScoreTitle(TitleOf(h), leaf);
            if (score > bestScore) { bestScore = score; best = h; }
            return true;
        }, IntPtr.Zero);
        // A bare substring (30) isn't confident enough to raise — report no match (the caller just notes it
        // rather than opening the session as an editor tab). 70+ = an exact/suffix title match we trust;
        // below that we'd risk raising the wrong window (multi-root titles, same-leaf siblings).
        return bestScore >= 70 ? best : IntPtr.Zero;
    }

    private static int ScoreTitle(string title, string leaf)
    {
        if (string.IsNullOrEmpty(title)) return 0;
        var l = leaf.ToLowerInvariant();

        // Score EVERY " - " segment, best wins. A default VS Code title is "● file.ts - proj - Visual Studio
        // Code" — the workspace name is the MIDDLE segment, so the old head-only scoring saw the FILENAME and
        // returned 0 (the silent focus no-op with a file open). Segment-scoped scoring keeps the original
        // safety (an open FILE named like the leaf scores 70 at most only when a whole segment ends with the
        // leaf — the exact-100 requires the segment to BE the workspace name) while making the fallback work
        // on default titles.
        int best = 0;
        foreach (var seg in TitleSegments(title))
        {
            var s = seg.ToLowerInvariant();
            int score = s == l ? 100 : s.EndsWith(l) ? 70 : s.Contains(l) ? 30 : 0;
            if (score > best) best = score;
        }
        return best;
    }

    /// <summary>The candidate workspace-name segments of a VS Code title: each " - " part, with a trailing
    /// " (Workspace)" marker stripped and the app-name tail ("Visual Studio Code") ignored implicitly (it
    /// simply never matches a leaf). "● file.ts - my-app - Visual Studio Code" yields
    /// ["● file.ts", "my-app", "Visual Studio Code"].</summary>
    private static IEnumerable<string> TitleSegments(string title)
    {
        if (string.IsNullOrEmpty(title)) yield break;
        foreach (var raw in title.Split(" - ", StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw;
            int cut = seg.IndexOf(" (Workspace)", StringComparison.OrdinalIgnoreCase);
            if (cut >= 0) seg = seg[..cut];
            seg = seg.Trim();
            if (seg.Length > 0) yield return seg;
        }
    }
}

/// <summary>
/// Login startup on Windows: a <c>Mastermind.lnk</c> in the per-user Startup folder pointing at the running exe —
/// added or repointed when config's startAtLogin is on, removed when it is off after a clean config read. A dev/debug
/// build (run.bat / IDE: an exe under <c>bin\Debug\</c> or <c>bin\Release\</c>) never touches it either way — never
/// any folder named <c>bin</c>, which a shipped install may sit in. Per-user, no admin.
/// </summary>
internal sealed class WinLoginStartup : ILoginStartup
{
    private const string LinkName = "Mastermind.lnk";

    public void Apply(bool enabled, bool cleanRead)
    {
        try
        {
            // Environment.ProcessPath is the running exe's own full path (e.g. C:\Tools\Mastermind\Mastermind.exe) — the shortcut's
            // target. AppContext.BaseDirectory would give only its folder (a single-file publish included).
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            if (Paths.IsDevBuild(exe)) return;
            string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrEmpty(startupDir)) return;
            string link = Path.Combine(startupDir, LinkName);

            if (enabled)
            {
                EnsureShortcut(link, exe, Path.GetDirectoryName(exe) ?? startupDir);
                if (DisabledBySystem())
                    Warnings.Add("start at login · Windows has Mastermind turned off in Settings › Apps › Startup — it "
                                 + "will not start at login until it is turned back on there");
            }
            else if (cleanRead && File.Exists(link)) File.Delete(link);
        }
        catch { /* login startup is best-effort; never block launch */ }
    }

    /// <summary>Windows keeps its own on/off switch beside a Startup-folder entry: a binary value named after the .lnk
    /// under StartupApproved\StartupFolder, whose first byte is odd while the entry is turned off. The shortcut stays
    /// on disk either way, so its presence says nothing.</summary>
    private static bool DisabledBySystem()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");
        return key?.GetValue(LinkName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
    }

    /// <summary>
    /// Create the .lnk if missing, or fix its target if it points somewhere else
    /// (e.g. a stale shortcut from the old dist\ location). Uses WScript.Shell COM.
    /// </summary>
    private static void EnsureShortcut(string linkPath, string target, string workingDir)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;

        dynamic shell = Activator.CreateInstance(shellType);
        try
        {
            // CreateShortcut loads the existing .lnk when one is already on disk.
            dynamic sc = shell.CreateShortcut(linkPath);
            string current = sc.TargetPath as string;
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                return; // already pinned to this exe — nothing to do

            sc.TargetPath = target;
            sc.WorkingDirectory = workingDir;
            sc.Description = "Mastermind — Claude Code session monitor";
            sc.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}

/// <summary>Recycle-bin deletion where Windows can; a directory recycles its whole tree. Both calls carry the
/// no-confirmation flag, so what Windows cannot recycle — a network or removable drive, a bin turned off or too small —
/// is deleted for good. Its error dialogs are shell UI, so the call runs on the UI thread through the context it was
/// handed.</summary>
internal sealed class WinRecycleBin : IRecycleBin
{
    private readonly SynchronizationContext _ui;
    public WinRecycleBin(SynchronizationContext ui) { _ui = ui; }

    public void Delete(string path, bool isDirectory) => UiCall.Run(_ui, () =>
    {
        if (isDirectory) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        return true;
    });

    /// <summary>The same move with no shell UI at all — no confirmation, no error dialog, no progress — for a sweep
    /// nobody clicked (background events stay log-only). Throws on any failure, for the caller's log. What
    /// Windows cannot recycle — bigger than the bin allows, a bin turned off, a subst drive — the no-confirmation flag
    /// deletes for good: accepted for throwaway drafts (user decision). The start-time clean keeps to fixed drives; the
    /// undo's staging removal does not.</summary>
    public void DeleteSilently(string path)
    {
        var op = new Native.SHFILEOPSTRUCT
        {
            wFunc = Native.FO_DELETE,
            pFrom = path + "\0",            // the marshaller adds the list's second terminator
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NO_UI | Native.FOF_NO_CONNECTED_ELEMENTS),
        };
        int rc = Native.SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted) throw new System.IO.IOException($"recycle failed (0x{rc:X}) · {path}");
    }
}

/// <summary>An editor's <c>state.vscdb</c> files under its settings folder (EditorIdentities — each window's editor,
/// else config's editor.userDataDir, <c>%APPDATA%\Code</c> by default), and the user settings of a window's
/// editor.</summary>
internal sealed class WinEditorState : IEditorState
{
    public IEnumerable<string> StateDbs(string userDataDir)
    {
        if (string.IsNullOrEmpty(userDataDir)) yield break;
        var user = Path.Combine(userDataDir, "User");
        var main = Path.Combine(user, "globalStorage", "state.vscdb");
        if (File.Exists(main)) yield return main;

        var profiles = Path.Combine(user, "profiles");
        if (Directory.Exists(profiles))
            foreach (var p in Directory.GetDirectories(profiles))
            {
                var pdb = Path.Combine(p, "globalStorage", "state.vscdb");
                if (File.Exists(pdb)) yield return pdb;
            }
    }

    public string UserSettings(int hostPid) => Path.Combine(EditorIdentities.ForHost(hostPid).UserDataDir, "User", "settings.json");
}

internal sealed class WinProcesses : IProcesses
{
    // Every image name a VS Code-family editor runs under on Windows; RunningEditors checks every editor in play first.
    private static readonly string[] KnownEditors = { "Code", "Code - Insiders", "Cursor", "VSCodium", "Windsurf", "Kiro" };

    public HashSet<int> LivePids(string name) => Proc.LivePids(name);
    public Proc.KillResult KillClaude(int pid) => Proc.KillClaude(pid);

    public bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }   // no process with that id
        catch { return true; }                          // can't tell
    }

    public IReadOnlyList<string> RunningEditors() =>
        EditorIdentities.ProcessNames().Concat(KnownEditors).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (Name: n, Count: Proc.LivePids(n).Count))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Name} ×{x.Count}")
            .ToList();

    public IReadOnlyList<ChildProcess> ChildrenOf(IReadOnlyCollection<int> parents)
    {
        var wanted = new HashSet<int>(parents);
        var list = new List<ChildProcess>();
        foreach (var p in Proc.Table())
            if (wanted.Contains(p.Parent))
            {
                var (gone, cmd, bytes, error) = Proc.Inspect(p.Pid);
                if (!gone) list.Add(new ChildProcess(p.Pid, p.Parent, p.Exe, cmd, bytes, error));
            }
        return list;
    }

    public IReadOnlyList<(int Pid, int Root, string CommandLine)> DescendantsNamed(IReadOnlyCollection<int> roots, string exe)
    {
        var found = new List<(int Pid, int Root, string CommandLine)>();
        foreach (var (pid, root) in Named(Proc.Table(), roots, exe, pastMatch: true))
        {
            var (gone, cmd, _, _) = Proc.Inspect(pid);
            if (!gone) found.Add((pid, root, cmd));
        }
        return found;
    }

    public IReadOnlyList<(int Pid, int Root)> OutermostNamed(IReadOnlyCollection<int> roots, string exe)
    {
        var table = Proc.Table();
        if (!table.Any(p => roots.Contains(p.Pid))) return null;   // every root gone
        return Named(table, roots, exe, pastMatch: false).ToList();
    }

    /// <summary>Each process with this image name under the roots, breadth-first off one snapshot, with the root it sits
    /// under; the walk goes on below a match only when <paramref name="pastMatch"/>.</summary>
    private static IEnumerable<(int Pid, int Root)> Named(List<(int Pid, int Parent, string Exe)> table,
                                                          IReadOnlyCollection<int> roots, string exe, bool pastMatch)
    {
        var byParent = table.ToLookup(p => p.Parent);
        foreach (int root in roots)
        {
            // Seen-set: a reused pid can make the parent links loop.
            var seen = new HashSet<int> { root };
            var queue = new Queue<int>(new[] { root });
            while (queue.Count > 0)
                foreach (var c in byParent[queue.Dequeue()])
                    if (seen.Add(c.Pid))
                    {
                        bool match = string.Equals(c.Exe, exe, StringComparison.OrdinalIgnoreCase);
                        if (match) yield return (c.Pid, root);
                        if (!match || pastMatch) queue.Enqueue(c.Pid);
                    }
        }
    }

    public DateTime? StartedAt(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime(); }
        catch { return null; }   // gone, or another account's
    }

    public string ImagePath(int pid) => Proc.ImagePath(pid);
}

internal sealed class WinEfficiencyMode : IEfficiencyMode
{
    public Efficiency.Result Release(string image) => Efficiency.Release(image);
}
