using System.IO;

namespace Mastermind;

/// <summary>
/// The ONE place the on-disk <c>~/.claude</c> layout lives. Every reader/writer of a Claude Code or
/// Mastermind file resolves its path here instead of re-building
/// <c>Path.Combine(UserProfile, ".claude", …)</c> — so adding a new stored file (or relocating the layout)
/// is a one-line change, not a hunt across the app. VS Code's own <c>%APPDATA%\Code</c> state lives in
/// <see cref="VsCodeState"/>, not here — this file is strictly the <c>~/.claude</c> tree.
/// </summary>
internal static class ClaudePaths
{
    /// <summary>Claude Code's folder — config's claudeDir, ~/.claude by default.</summary>
    public static string Home => Config.Current.ClaudeDir;

    // Claude Code's own dirs.
    public static string Projects => Path.Combine(Home, "projects");   // transcripts: <slug>/<session>.jsonl
    public static string Sessions => Path.Combine(Home, "sessions");   // live-agent registry: <pid>.json
    public static string Ide      => Path.Combine(Home, "ide");        // IDE lock files: *.lock

    // The exe's own state lives in its app folder.
    public static string StatusJson    => Path.Combine(Config.AppDir, "status.json");     // per-session last status
    public static string WindowJson    => Path.Combine(Config.AppDir, "window.json");     // window placement
    public static string AutoAllowJson => Path.Combine(Config.AppDir, "autoallow.json");  // per-session auto-allow toggle
    public static string PromptOwnersJson => Path.Combine(Config.AppDir, "prompt_owners.json"); // per-processing-file owner session id (orphan detection for the incoming-prompt inbox)
    public static string WatchedJson => Path.Combine(Config.AppDir, "watched.json");   // the roots the shell's watch wrote to
    public static string HandoffsJson => Path.Combine(Config.AppDir, "handoffs.json");   // the roots the handoff install wrote its skills to
    public static string RestoreJson => Path.Combine(Config.AppDir, "restore.json");   // session restore's record: each window's folder set → its sessions
    public static string EditorsJson => Path.Combine(Config.AppDir, "editors.json");   // every editor a window's hello named
    /// <summary>A project's local settings file — where Mastermind's hook entries live.</summary>
    public static string SettingsLocal(string root) => Path.Combine(root, ".claude", "settings.local.json");
    /// <summary>A project's skills folder — where the handoff install writes its two skills (HandoffSkills.cs).</summary>
    public static string SkillsDir(string root) => Path.Combine(root, ".claude", "skills");
    // The projects.json manifest and the prompts file are config's manifestPath and promptsPath, not here.
}
