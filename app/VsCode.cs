namespace Mastermind;

/// <summary>
/// Which editor workspaces are open, off the IDE lock files. The window raise is <see cref="Platform.EditorWindows"/>'s;
/// a <c>vscode://</c> deep link is never used (it lands a session in an editor tab), and the sidebar switch itself is
/// <see cref="CdpSwitcher"/>'s.
/// </summary>
internal static class VsCode
{
    /// <summary>
    /// Canonical keys (full normalized cwd paths, NOT leaves) of every workspace folder open in a live
    /// VS Code window — sourced from the IDE lock files (authoritative + multi-root aware), not window
    /// titles. Full-path so two same-leaf checkouts stay distinct. Drives the session list + reconcile poll.
    /// </summary>
    public static HashSet<string> OpenProjectKeys()
    {
        var set = new HashSet<string>();   // keys are lower-cased by Paths.Key -> ordinal is enough
        foreach (var folder in IdeRegistry.OpenWorkspaceFolders())
        {
            var key = Paths.Key(folder);
            if (!string.IsNullOrEmpty(key)) set.Add(key);
        }
        return set;
    }

    /// <summary>True if ANY editor window is open (Claude-attached or not) — the reconciler's blind-spot test, so it
    /// errs toward "open": a live IDE lock OR a visible editor window both count.</summary>
    public static bool AnyVsCodeOpen() =>
        IdeRegistry.OpenWorkspaceFolders().Count > 0 || Platform.EditorWindows.AnyOpen();
}
