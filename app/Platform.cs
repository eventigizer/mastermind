namespace Mastermind;

/// <summary>How a window raise ended. Refused: the window was found, but Windows' foreground lock kept it back — the
/// taskbar flashes instead.</summary>
internal enum RaiseResult { NotFound, Raised, Refused }

/// <summary>Editor windows, by intent — raise the one holding a project or carrying an exact title, and whether any
/// is open. Never a window handle, so a Mac or Linux implementation can exist.</summary>
internal interface IEditorWindows
{
    /// <summary>Raise the editor window whose title best matches the project folder. NotFound = no confident match.</summary>
    RaiseResult RaiseProject(string cwd);
    /// <summary>Raise the editor window whose title is exactly <paramref name="title"/>. NotFound = no such window.</summary>
    RaiseResult RaiseByTitle(string title);
    /// <summary>Let another app take the foreground for a raise it performs — called while Mastermind is foreground (a
    /// card click), before a raise the editor runs itself.</summary>
    void GrantForeground();
    /// <summary>True when any editor window is open.</summary>
    bool AnyOpen();
}

/// <summary>Start at login.</summary>
internal interface ILoginStartup
{
    /// <summary>Make the login entry match <paramref name="enabled"/>; a removal runs only when
    /// <paramref name="cleanRead"/>.</summary>
    void Apply(bool enabled, bool cleanRead);
}

/// <summary>Recycle-bin deletion. Delete's error dialogs are UI, so it runs on the UI thread; DeleteSilently shows
/// nothing and throws instead, for a sweep nobody clicked.</summary>
internal interface IRecycleBin { void Delete(string path, bool isDirectory); void DeleteSilently(string path); }

internal interface IEditorState
{
    /// <summary>Every state DB that exists under one editor's settings folder — the global one, then one per
    /// profile.</summary>
    IEnumerable<string> StateDbs(string userDataDir);

    /// <summary>The user settings file of the editor whose window runs under this extension host — its default
    /// profile's.</summary>
    string UserSettings(int hostPid);
}

/// <summary>One direct child process — its image, command line and private memory; Error set when it could not be read
/// (another account's process), never zeros passed off as a reading.</summary>
internal readonly record struct ChildProcess(int Pid, int Parent, string Exe, string CommandLine, long PrivateBytes, string Error);

internal interface IProcesses
{
    HashSet<int> LivePids(string name);
    Proc.KillResult KillClaude(int pid);
    /// <summary>The pid is a running process. Can't tell → true: the callers guard destructive writes.</summary>
    bool IsAlive(int pid);
    /// <summary>Every VS Code-family editor process running — every editor in play and the known look-alikes, so a
    /// wrong processName cannot disarm a check guarding a destructive write — one "name ×count" entry per
    /// image name; empty when none runs.</summary>
    IReadOnlyList<string> RunningEditors();

    /// <summary>Every direct child of these pids, off one snapshot, each inspected; a child gone mid-read is left out.
    /// Throws when the snapshot fails.</summary>
    IReadOnlyList<ChildProcess> ChildrenOf(IReadOnlyCollection<int> parents);

    /// <summary>Every process with this image name anywhere under these pids, off one snapshot — its pid, the root it sits
    /// under and its command line ("" when unreadable) — so a wrapper process between them hides none; one gone
    /// mid-read is left out. Throws when the snapshot fails.</summary>
    IReadOnlyList<(int Pid, int Root, string CommandLine)> DescendantsNamed(IReadOnlyCollection<int> roots, string exe);

    /// <summary>The first process with this image name on each branch under these pids, off one snapshot — its pid and
    /// the root it sits under. One nested under another of the same name (a <c>claude -p</c> a session's shell runs)
    /// is left out; a wrapper of another name between them is walked through. Null when the snapshot holds none of
    /// the roots — every one gone; throws when the snapshot fails.</summary>
    IReadOnlyList<(int Pid, int Root)> OutermostNamed(IReadOnlyCollection<int> roots, string exe);

    /// <summary>When a running process started, in UTC, or null when it is not running or cannot be read.</summary>
    DateTime? StartedAt(int pid);

    /// <summary>A process's full image path, or null when it cannot be read.</summary>
    string ImagePath(int pid);

    /// <summary>Delete these folders once this process has exited — a running exe's own folder cannot go while it
    /// runs. Fire and forget: a folder that will not go stays.</summary>
    void DeleteAfterExit(params string[] folders);
}

internal interface IEfficiencyMode { Efficiency.Result Release(string image); }

/// <summary>The platform implementations — Windows today. The composition root (App) may replace one; a caller never
/// names an implementation, so the Avalonia phase swaps these and touches no caller. The two that touch the UI — the
/// editor-window raise and the recycle bin's error dialogs — are handed the UI thread's context; nothing in the others
/// is bound to a thread, so they take none.</summary>
internal static class Platform
{
    public static IEditorWindows EditorWindows { get; set; } = new WinEditorWindows(null);
    public static ILoginStartup LoginStartup { get; set; } = new WinLoginStartup();
    public static IRecycleBin RecycleBin { get; set; } = new WinRecycleBin(null);
    public static IEditorState EditorState { get; set; } = new WinEditorState();
    public static IProcesses Processes { get; set; } = new WinProcesses();
    public static IEfficiencyMode EfficiencyMode { get; set; } = new WinEfficiencyMode();
}
