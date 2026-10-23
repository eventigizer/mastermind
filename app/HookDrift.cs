using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mastermind;

/// <summary>One monitored checkout from the manifest: its root path plus the prompt-routing token it
/// DECLARES — the {SRC}to{DST} filename token that resolves to this project by exact match (see
/// <see cref="PromptRoute.DestRoot"/>). Declared per checkout so two projects of the same class (two
/// per-event mobile apps) can never capture each other's prompts.</summary>
internal sealed class ProjectEntry
{
    [JsonPropertyName("root")]   public string Root   { get; set; } = "";
    [JsonPropertyName("token")]  public string Token  { get; set; } = "";
    // Config overrides only (config.json's projects) — the manifest carries root and token. Null = unset, so the
    // manifest's value stands (ProjectList's merge).
    [JsonPropertyName("label")]  public string Label  { get; set; }
    [JsonPropertyName("hide")]   public bool? Hide    { get; set; }
}

/// <summary>The manifest at config's manifestPath — the
/// monitored projects (root + routing token) and the events Mastermind's hook entries carry.</summary>
internal sealed class ProjectsManifest
{
    [JsonPropertyName("mastermindEvents")] public List<string> MastermindEvents { get; set; } = new();
    [JsonPropertyName("projects")]      public List<ProjectEntry> Projects { get; set; } = new();
}

/// <summary>
/// #6 — the hook check. A project whose <c>.claude/settings.local.json</c> lost Mastermind's hook entries makes the panel
/// silently BLIND for it — you'd never see its agents need you. This walks the manifest and the projects the
/// shell watched (<see cref="HookEntries"/>) and reports any project not fully wired: on every manifest event an entry
/// posting to <c>127.0.0.1:&lt;hookPort&gt;/hook</c> — a background <c>curl</c> command hook, a plain one on SessionEnd
/// (a background hook is killed at process exit), and on PermissionRequest the HTTP hook whose response carries the
/// allow answer, beside the exe's own <c>--hook</c> entry, naming an exe that exists.
/// Pure file reads through the shared read (no UI, no CDP); the caller runs it every tick and logs only a change.
/// </summary>
internal static class HookDrift
{
    /// <summary>The project keys (<see cref="Paths.Key"/>) of the roots the last <see cref="Check"/> found fully wired —
    /// the projects the symptom warning watches.</summary>
    public static IReadOnlyList<string> Wired => _wired;
    private static List<string> _wired = new();

    // A settings file caught mid-rewrite reads as unreadable once, so a failure is reported only when the next check
    // fails on the same file too. UI thread.
    private static HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One human-readable line per drift finding; empty list = every monitored project is fully wired.
    /// Null = nothing was checked: no manifest configured, none written there yet, or neither a manifest project nor a
    /// watched one — never reported as "all wired".</summary>
    public static List<string> Check()
    {
        var findings = new List<string>();
        var wired = new List<string>();
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _wired = wired;
        var (manifest, failed) = ProjectList.ReadManifest();
        if (failed)
            return File.Exists(Config.Current.ManifestPath)
                ? new List<string> { $"manifest {Config.Current.ManifestPath} unreadable — the crash log has why" }
                : null;   // configured but not written yet — no manifest writer has run on this machine
        // The manifest's projects, then every project the shell watched that still exists.
        var roots = (manifest?.Projects ?? new List<ProjectEntry>())
            .Where(e => !string.IsNullOrEmpty(e?.Root)).Select(e => (Root: e.Root, Listed: true)).ToList();
        foreach (var root in HookEntries.Watched())
            if (Directory.Exists(root) && !HookEntries.IsListed(manifest, root)) roots.Add((root, false));
        if (roots.Count == 0) return null;

        var events = Events(manifest);
        var url = EventHub.HookUrl;
        foreach (var (root, listed) in roots)
        {
            var name = Paths.Leaf(root);
            var missing = MissingEntries(ClaudePaths.SettingsLocal(root), events, url);
            if (missing == null)
            {
                unreadable.Add(root);
                if (_unreadable.Contains(root)) findings.Add($"{name}: settings.local.json unreadable");
            }
            else if (missing.Count > 0)
                findings.Add($"{name}: Mastermind's hook entries missing or of the wrong kind → {string.Join(", ", missing)}"
                             + (listed ? " — re-run whatever writes the manifest" : " — run Mastermind: Watch this project again"));
            else wired.Add(Paths.Key(root));
        }
        _unreadable = unreadable;
        return findings;
    }

    private static readonly List<string> DefaultEvents = new()
    { "PermissionRequest", "Notification", "Stop", "SessionStart", "SessionEnd", "UserPromptSubmit", "PreToolUse",
      "PostToolUse", "PostModelSwitch" };

    /// <summary>The events Mastermind's entries carry: the manifest's mastermindEvents, else the built-in set.</summary>
    internal static List<string> Events(ProjectsManifest manifest) =>
        manifest?.MastermindEvents != null && manifest.MastermindEvents.Count > 0 ? manifest.MastermindEvents : DefaultEvents;

    /// <summary>The project carries every one of Mastermind's entries, each of the wanted kind and posting to this run's
    /// port — the state the shell's badge shows. False on an unreadable file.</summary>
    internal static bool IsWired(string root, ProjectsManifest manifest) =>
        MissingEntries(ClaudePaths.SettingsLocal(root), Events(manifest), EventHub.HookUrl) is { Count: 0 };

    internal enum Kind { Background, Plain, Http, Exe }

    /// <summary>The events that also carry the exe's own entry (<see cref="HookRun"/>) — PermissionRequest, the
    /// auto-allow answer while Mastermind is closed. A dev build writes and expects none.</summary>
    internal static bool WantsExe(string ev) => ev == "PermissionRequest" && !Paths.IsDevBuild(Environment.ProcessPath);

    /// <summary>The exe's own entry: <c>Mastermind.exe --hook</c> by full path, to an exe that exists — a stale path
    /// fails silently, so it counts as missing.</summary>
    private static bool IsExeEntry(JsonElement h)
    {
        var command = Json.Str(h, "command");
        return string.Equals(Path.GetFileName(command), "Mastermind.exe", StringComparison.OrdinalIgnoreCase)
            && h.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array && args.GetArrayLength() == 1
            && args[0].ValueKind == JsonValueKind.String && args[0].GetString() == "--hook"
            && File.Exists(command);
    }

    /// <summary>The entry kind an event wants: SessionEnd plain, PermissionRequest HTTP (Mastermind answers it), the
    /// rest background.</summary>
    internal static Kind Want(string ev) =>
        ev == "SessionEnd" ? Kind.Plain
        : ev == "PermissionRequest" ? Kind.Http
        : Kind.Background;

    /// <summary>Events lacking an entry of the wanted kind that posts to <paramref name="url"/>. Null if the file can't
    /// be read (distinct from "all present" = empty list). The shared read never blocks an in-place rewrite of the
    /// file; a replace landing mid-read fails whatever the sharing, and its writer retries the replace.</summary>
    private static List<string> MissingEntries(string settingsPath, List<string> events, string url)
    {
        try
        {
            var text = Json.ReadShared(settingsPath);
            if (text == null) return events.ToList();   // whole project unwired
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Object)
                return events.ToList();
            var missing = new List<string>();
            foreach (var ev in events)
            {
                if (!HasEntry(hooks, ev, Want(ev), url)) missing.Add(ev);
                if (WantsExe(ev) && !HasEntry(hooks, ev, Kind.Exe, url)) missing.Add($"{ev} (Mastermind.exe --hook)");
            }
            return missing;
        }
        catch { return null; }
    }

    private static bool HasEntry(JsonElement hooks, string ev, Kind want, string url)
    {
        if (!hooks.TryGetProperty(ev, out var arr) || arr.ValueKind != JsonValueKind.Array) return false;
        foreach (var block in arr.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty("hooks", out var hs) || hs.ValueKind != JsonValueKind.Array) continue;
            foreach (var h in hs.EnumerateArray())
                if (KindOf(h, url) == want) return true;
        }
        return false;
    }

    /// <summary>One entry's kind when it posts to <paramref name="url"/>, else null.</summary>
    private static Kind? KindOf(JsonElement h, string url)
    {
        if (h.ValueKind != JsonValueKind.Object) return null;
        var type = Json.Str(h, "type");
        if (type == "command" && IsExeEntry(h)) return Kind.Exe;
        if (type == "http") return Json.Str(h, "url") == url ? Kind.Http : null;
        if (type != "command" || Path.GetFileNameWithoutExtension(Json.Str(h, "command")) != "curl") return null;
        if (!h.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array
            || !args.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == url)) return null;
        return h.TryGetProperty("async", out var bg) && bg.ValueKind == JsonValueKind.True ? Kind.Background : Kind.Plain;
    }
}
