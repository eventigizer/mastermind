using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mastermind;

/// <summary>
/// Mastermind's hook entries in a project's <c>.claude/settings.local.json</c> for every project the manifest does not
/// list — the shell's "watch this project". The kind per event is <see cref="HookDrift.Want"/>'s: a background
/// <c>curl</c> on every event, a plain one on SessionEnd, the HTTP entry on PermissionRequest, and beside the HTTP one
/// on PermissionRequest the exe's own --hook entry. A change is
/// proposed — the file's text before and after, in its own line endings — and applied only while the file still reads
/// as the proposal's before. The projects watched this way are listed in <c>watched.json</c>, so a port change or the
/// swap repoints their entries at the next start, and the hook check covers them beside the manifest's. While the
/// manifest cannot be read, nothing is written: a project it lists is never this file's to touch.
/// </summary>
internal static class HookEntries
{
    internal sealed record Proposal(string Id, string Root, bool Remove, string Before, string After, List<string> Warnings);

    private const string NullDevice = "nul";        // Windows' — a Mac or Linux port picks the OS's
    private const string CurlCap = "2";             // curl's -m, seconds
    private const string CurlSessionEndCap = "1";   // curl's -m on SessionEnd — inside the 1.5 s budget
    private const int HttpTimeout = 3;              // the HTTP entry's timeout, seconds
    private const int ExeTimeout = 5;               // the exe entry's timeout, seconds
    private static readonly Regex MastermindUrl = new(@"^http://127\.0\.0\.1:\d+/hook$");

    /// <summary>The roots the shell's install wrote to.</summary>
    public static List<string> Watched() => RootList.Read(ClaudePaths.WatchedJson);

    /// <summary>The root is one the manifest lists.</summary>
    public static bool IsListed(ProjectsManifest manifest, string root)
    {
        var key = Paths.Key(root);
        return manifest?.Projects?.Any(p => !string.IsNullOrEmpty(p?.Root) && Paths.Key(p.Root) == key) == true;
    }

    /// <summary>Mastermind's entries replaced (watch) or removed (stop watching) in the project's file, as a proposal; an
    /// error text instead when the manifest cannot be read or lists the project, nothing would change, or
    /// the file cannot be read as a settings object. <paramref name="env"/> is the asking window's environment, for the
    /// proxy warning.</summary>
    public static (Proposal Proposal, string Error) Propose(string root, bool remove, IReadOnlyDictionary<string, string> env)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return (null, $"no folder {root}");
        var (manifest, failed) = ProjectList.ReadManifest();
        if (failed) return (null, "the manifest can't be read right now — try again in a moment");
        if (IsListed(manifest, root)) return (null, $"{Paths.Leaf(root)} is listed in the manifest (config's manifestPath) — its hook entries are the manifest writer's");
        var (before, data, error) = Read(ClaudePaths.SettingsLocal(root));
        if (error != null) return (null, error);
        var original = data.DeepClone();
        Rewrite(data, remove ? null : HookDrift.Events(manifest));
        if (JsonNode.DeepEquals(original, data))
            return (null, remove ? $"{Paths.Leaf(root)} carries no Mastermind entries" : $"{Paths.Leaf(root)} is already watched");
        var warnings = new List<string>();
        if (!remove && ProxyWithoutLoopback(env))
            warnings.Add("A proxy variable is set without 127.0.0.1 in NO_PROXY — Claude Code would send the permission " +
                         "entry's HTTP post to the proxy. Add 127.0.0.1 to NO_PROXY.");
        return (new Proposal(Guid.NewGuid().ToString("N"), root, remove, before, Text(data, before), warnings), null);
    }

    /// <summary>Write the proposal while the file still reads as its before — null when written, else why not.</summary>
    public static string Apply(Proposal p)
    {
        string now;
        try { now = Json.ReadShared(ClaudePaths.SettingsLocal(p.Root)) ?? ""; }
        catch (Exception ex) { return $"settings.local.json unreadable ({ex.Message})"; }
        if (now != p.Before) return "settings.local.json changed since the diff — run the command again";
        if (!Json.WriteAtomic(ClaudePaths.SettingsLocal(p.Root), p.After)) return "settings.local.json not written — the crash log has why";
        SetWatched(p.Root, !p.Remove);
        return null;
    }

    /// <summary>At startup: in each watched project, every Mastermind entry this run would write differently — posting to
    /// another port, or another permission kind — is repointed in place. An event's entry the user took
    /// out stays out, and a project the manifest lists is never touched. One log line per project moved or failed.</summary>
    public static List<(LogKind Kind, string Text)> RewriteWatched()
    {
        var lines = new List<(LogKind Kind, string Text)>();
        var (manifest, failed) = ProjectList.ReadManifest();
        if (failed)
        {
            lines.Add((LogKind.Warn, "hooks · the manifest unreadable — watched projects not repointed this start"));
            return lines;
        }
        foreach (var root in Watched())
        {
            if (!Directory.Exists(root) || IsListed(manifest, root)) continue;
            var (before, data, error) = Read(ClaudePaths.SettingsLocal(root));
            if (error != null) { lines.Add((LogKind.Warn, $"hooks · {Paths.Leaf(root)}: {error}")); continue; }
            if (!Repoint(data)) continue;
            lines.Add(Json.WriteAtomic(ClaudePaths.SettingsLocal(root), Text(data, before))
                ? (LogKind.Info, $"hooks · {Paths.Leaf(root)}: Mastermind's entries repointed to port {EventHub.Port}")
                : (LogKind.Warn, $"hooks · {Paths.Leaf(root)}: Mastermind's entries not repointed — the crash log has why"));
        }
        return lines;
    }

    /// <summary>The file's text ("" when missing) and its object; an error text instead when it cannot be read or is
    /// not an object whose hooks, when present, is an object.</summary>
    private static (string Text, JsonObject Data, string Error) Read(string path)
    {
        string text;
        try { text = Json.ReadShared(path) ?? ""; }
        catch (Exception ex) { return (null, null, $"{path} unreadable ({ex.Message})"); }
        if (text.Trim().Length == 0) return (text, new JsonObject(), null);
        JsonObject data = null;
        try { data = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
        if (data == null) return (null, null, $"{path} is not a JSON object — fix it by hand first");
        if (data["hooks"] is { } hooks && hooks is not JsonObject) return (null, null, $"{path}: its hooks is not an object");
        return (text, data, null);
    }

    /// <summary>The strip, then, when <paramref name="events"/> is given, one block per event
    /// appended: every Mastermind entry out, a block it leaves empty out, an event it leaves empty out, a <c>hooks</c>
    /// left empty out.</summary>
    private static void Rewrite(JsonObject data, List<string> events)
    {
        if (data["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            data["hooks"] = hooks;
        }
        foreach (var ev in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[ev] is not JsonArray blocks) continue;
            bool hit = false;
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                if (blocks[i] is not JsonObject block || block["hooks"] is not JsonArray hs) continue;
                int count = hs.Count;
                for (int j = hs.Count - 1; j >= 0; j--)
                    if (IsMastermind(hs[j])) hs.RemoveAt(j);
                if (hs.Count == count) continue;
                hit = true;
                if (hs.Count == 0) blocks.RemoveAt(i);
            }
            if (hit && blocks.Count == 0) hooks.Remove(ev);
        }
        if (events != null)
            foreach (var ev in events)
            {
                if (hooks[ev] is not JsonArray blocks)
                {
                    blocks = new JsonArray();
                    hooks[ev] = blocks;
                }
                var entries = new JsonArray(Entry(ev));
                if (HookDrift.WantsExe(ev)) entries.Add(ExeEntry());
                blocks.Add(new JsonObject { ["matcher"] = "*", ["hooks"] = entries });
            }
        if (hooks.Count == 0) data.Remove("hooks");
    }

    /// <summary>Every Mastermind entry this run would write differently replaced in place by this run's; true when any
    /// was. Never adds an entry.</summary>
    private static bool Repoint(JsonObject data)
    {
        bool moved = false;
        if (data["hooks"] is JsonObject hooks)
            foreach (var (ev, node) in hooks)
                if (node is JsonArray blocks)
                    foreach (var block in blocks)
                        if (block is JsonObject o && o["hooks"] is JsonArray hs)
                            for (int j = 0; j < hs.Count; j++)
                                if (IsMastermind(hs[j]))
                                {
                                    bool isExe = hs[j] is JsonObject x && IsExe(x);
                                    if (isExe && !HookDrift.WantsExe(ev)) continue;   // a dev build leaves the exe's entry be
                                    var want = isExe ? ExeEntry() : Entry(ev);
                                    if (!JsonNode.DeepEquals(hs[j], want)) { hs[j] = want; moved = true; }
                                }
        return moved;
    }

    /// <summary>One event's entry, of the kind it wants.</summary>
    private static JsonObject Entry(string ev) => HookDrift.Want(ev) switch
    {
        HookDrift.Kind.Http => new JsonObject { ["type"] = "http", ["url"] = EventHub.HookUrl, ["timeout"] = HttpTimeout },
        HookDrift.Kind.Plain => Curl(CurlSessionEndCap, background: false),
        _ => Curl(CurlCap, background: true),
    };

    /// <summary>A curl entry: exec form, the payload on stdin, the answer to the null device (a hook's
    /// stdout reaches the model), <c>-q</c> first so no .curlrc is read, <c>--noproxy *</c> so no proxy variable takes
    /// the loopback post.</summary>
    private static JsonObject Curl(string cap, bool background)
    {
        var hook = new JsonObject
        {
            ["type"] = "command",
            ["command"] = "curl",
            ["args"] = new JsonArray("-q", "-s", "-o", NullDevice, "-m", cap, "--noproxy", "*", "-H",
                "Content-Type: application/json", "--data-binary", "@-", EventHub.HookUrl),
        };
        if (background) hook["async"] = true;
        return hook;
    }

    /// <summary>The exe's own entry (<see cref="HookRun"/>): this run's exe by full path, exec form, a short
    /// timeout — the engine waits on a PermissionRequest hook before its dialog.</summary>
    private static JsonObject ExeEntry() => new()
    {
        ["type"] = "command",
        ["command"] = Environment.ProcessPath,
        ["args"] = new JsonArray("--hook"),
        ["timeout"] = ExeTimeout,
    };

    /// <summary>The exe's own entry, wherever its exe sits — the stale ones included, so a strip takes them too.</summary>
    private static bool IsExe(JsonObject o) =>
        Json.Str(o["type"]) == "command"
        && string.Equals(Path.GetFileName(Json.Str(o["command"])), "Mastermind.exe", StringComparison.OrdinalIgnoreCase)
        && o["args"] is JsonArray a && a.Count == 1 && Json.Str(a[0]) == "--hook";

    /// <summary>A Mastermind entry: one posting to a 127.0.0.1:&lt;port&gt;/hook, any port, or the
    /// exe's own --hook entry.</summary>
    private static bool IsMastermind(JsonNode h)
    {
        if (h is not JsonObject o) return false;
        var type = Json.Str(o["type"]);
        if (type == "http") return MastermindUrl.IsMatch(Json.Str(o["url"]));
        if (IsExe(o)) return true;
        return type == "command" && o["args"] is JsonArray args && args.Any(a => MastermindUrl.IsMatch(Json.Str(a)));
    }

    /// <summary>The object as the file's text: two-space indent, a closing newline, the line endings of
    /// <paramref name="before"/> — LF for a new file.</summary>
    private static string Text(JsonObject data, string before)
    {
        var text = data.ToJsonString(Json.Indented) + "\n";
        return before.Contains("\r\n") ? text.Replace("\n", "\r\n") : text;
    }

    private static void SetWatched(string root, bool on) => RootList.Set(ClaudePaths.WatchedJson, root, on);

    /// <summary>A proxy variable set with no NO_PROXY entry covering 127.0.0.1 — the literal, or <c>*</c>.</summary>
    private static bool ProxyWithoutLoopback(IReadOnlyDictionary<string, string> env)
    {
        bool proxy = new[] { "HTTP_PROXY", "HTTPS_PROXY" }.Any(k => env.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v));
        var noProxy = (env.TryGetValue("NO_PROXY", out var n) ? n : "").Split(',').Select(s => s.Trim()).ToList();
        return proxy && !noProxy.Contains("127.0.0.1") && !noProxy.Contains("*");
    }
}
