using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mastermind;

/// <summary>
/// The VS Code shell's settings into <c>config.json</c> — the exe its file's one writer here, so N windows sending the
/// same change never race on it. Only the keys the shell mirrors — and one project's routing code, for the handoff
/// install — merged key by key into the file as it is now: the
/// manifest and prompts paths and <c>whatsappEfficiencyFix</c> are never touched. A value equal to the running config
/// is no change; a hookPort this process cannot listen on is never written, since a run unable to bind it has no
/// channel left for the setting that would undo it; a file that does not parse is never rewritten. Config is read
/// once, so the caller restarts the exe when anything was written. The handoff install writes one project's routing
/// code into projects the same way (<see cref="WriteProject"/>), that entry's other fields and every other entry left
/// as they are. The projects page replaces the whole list (<see cref="WriteProjects"/>).
/// </summary>
internal static class ConfigWriter
{
    /// <summary>The values of <paramref name="values"/> that differ from the running config. A value not valid for its
    /// key, a hookPort that cannot be listened on, and a key the shell never writes are warned and left out.</summary>
    public static List<(string Key, JsonNode Value)> Changes(JsonObject values, Action<string> warn)
    {
        var run = Config.Current;
        var changes = new List<(string Key, JsonNode Value)>();
        foreach (var (key, node) in values)
        {
            switch (key)
            {
                case "hookPort":
                    if (Port(node, allowZero: false) is not int hp) warn($"config · the shell sent hookPort {Show(node)} — not a port, ignored");
                    else if (hp != run.HookPort)
                    {
                        var why = EventHub.CanBind(hp);
                        if (why == null) changes.Add((key, hp));
                        else warn($"config · hookPort {hp} cannot be listened on ({why}) — not written; set Mastermind: Hook Port to a free port");
                    }
                    break;
                case "debugPort":
                    if (Port(node, allowZero: true) is not int dp) warn($"config · the shell sent debugPort {Show(node)} — not a port, ignored");
                    else if (dp != run.DebugPort) changes.Add((key, dp));
                    break;
                case "claudeDir":
                    var dir = Json.Str(node);
                    if (!Path.IsPathRooted(dir)) warn($"config · the shell sent claudeDir {Show(node)} — not an absolute path, ignored");
                    else if (Paths.Key(dir) != Paths.Key(run.ClaudeDir)) changes.Add((key, dir));
                    break;
                case "startAtLogin": Flag(key, node, run.StartAtLogin); break;
                case "newDispatchAutoAllow": Flag(key, node, run.NewDispatchAutoAllow); break;
                case "handoffs.enabled": Flag(key, node, run.HandoffsEnabled); break;
                default: warn($"config · the shell sent {key}, a key it never writes — ignored"); break;
            }
        }
        return changes;

        void Flag(string key, JsonNode node, bool current)
        {
            if (node is JsonValue v && v.TryGetValue<bool>(out var b)) { if (b != current) changes.Add((key, b)); }
            else warn($"config · the shell sent {key} {Show(node)} — not true or false, ignored");
        }
    }

    /// <summary>
    /// <paramref name="changes"/> merged into the file as it is now; the keys written — empty when none was, since a
    /// deliberate <c>handoffs: null</c> reset is left alone — or null when the file could not be written (warned). A
    /// first write into no file at all also drops the last-good copy: deleting config.json is the reset, and the copy
    /// would otherwise refill every key the new file lacks, with a warning each and no clean read again.
    /// </summary>
    public static List<string> Write(List<(string Key, JsonNode Value)> changes, Action<string> warn)
    {
        var (root, created) = Load(warn, "the shell's settings");
        if (root == null) return null;
        var written = new List<string>();
        foreach (var (key, value) in changes)
        {
            if (key == "handoffs.enabled") { if (!SetRelay(root, value, warn)) continue; }
            else root[key] = value;
            written.Add(key);
        }
        return Save(root, created, written, warn);
    }

    /// <summary>One project's routing code into config's projects — set, or cleared when <paramref name="token"/> is
    /// null, an entry left holding only its root dropped — and handoffs.enabled true when <paramref name="relayOn"/>:
    /// the handoff install's one write, so it costs one restart. The entry is found by its root as the reader expands
    /// it. The keys written, empty when nothing changed; null when the file could not be written, or its projects is
    /// neither a list nor null (warned).</summary>
    public static List<string> WriteProject(string projectRoot, string token, bool relayOn, Action<string> warn)
    {
        var (root, created) = Load(warn, "the project's code");
        if (root == null) return null;
        if (root["projects"] is not (null or JsonArray))
        {
            warn("config · projects in config.json is not a list — the project's code not written until it is fixed");
            return null;
        }
        var list = root["projects"] as JsonArray;
        var key = Paths.Key(projectRoot);
        var entry = list?.OfType<JsonObject>()
            .FirstOrDefault(e => Json.Str(e["root"]) is { Length: > 0 } r && Paths.Key(Config.Expand(r)) == key);
        var written = new List<string>();
        if (token != null && Json.Str(entry?["token"]) != token)
        {
            if (list == null) root["projects"] = list = new JsonArray();
            if (entry == null) list.Add(entry = new JsonObject { ["root"] = projectRoot });
            entry["token"] = token;
            written.Add("projects");
        }
        else if (token == null && entry != null && entry.ContainsKey("token"))
        {
            entry.Remove("token");
            if (entry.Count == 1) list.Remove(entry);
            written.Add("projects");
        }
        if (relayOn && SetRelay(root, true, warn)) written.Add("handoffs.enabled");
        return Save(root, created, written, warn);
    }

    /// <summary>The projects page's save: config's projects replaced by <paramref name="projects"/>, every value already
    /// checked (<see cref="ProjectsPage.Save"/>). The keys written — empty when the list did not change; null when the
    /// file could not be written or does not parse (warned).</summary>
    public static List<string> WriteProjects(JsonArray projects, Action<string> warn)
    {
        var (root, created) = Load(warn, "the projects page's save");
        if (root == null) return null;
        var written = new List<string>();
        if (!JsonNode.DeepEquals(root["projects"], projects))
        {
            root["projects"] = projects;
            written.Add("projects");
        }
        return Save(root, created, written, warn);
    }

    /// <summary>The file as it is now, or a new object when there is none (Created); a null object when it cannot be
    /// read or does not parse (warned) — such a file is never rewritten.</summary>
    private static (JsonObject Root, bool Created) Load(Action<string> warn, string what)
    {
        string text;
        try { text = Json.ReadShared(Config.FilePath); }
        catch (Exception ex) { warn($"config · {Config.FilePath} unreadable ({ex.Message}) — {what} not written"); return (null, false); }
        if (text == null) return (new JsonObject(), true);
        JsonObject root = null;
        try { root = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
        if (root == null) warn($"config · {Config.FilePath} does not parse — {what} not written until it is fixed");
        return (root, false);
    }

    /// <summary>handoffs.enabled set; false when a deliberate <c>handoffs: null</c> reset holds it (warned).</summary>
    private static bool SetRelay(JsonObject root, JsonNode value, Action<string> warn)
    {
        if (root.TryGetPropertyValue("handoffs", out var current) && current == null)
        {
            warn("config · handoffs is null in config.json — a reset made by hand, so the relay setting is not written over it");
            return false;
        }
        if (current is not JsonObject handoffs) root["handoffs"] = handoffs = new JsonObject();
        handoffs["enabled"] = value;
        return true;
    }

    /// <summary>The object written back when anything changed: <paramref name="written"/>, or null when the write failed
    /// (warned). A file this run created drops the last-good copy (<see cref="Write"/>).</summary>
    private static List<string> Save(JsonObject root, bool created, List<string> written, Action<string> warn)
    {
        if (written.Count == 0) return written;
        if (!Json.WriteAtomic(Config.FilePath, root.ToJsonString(Json.Indented) + "\n"))
        {
            warn($"config · {Config.FilePath} not written — the crash log has why");
            return null;
        }
        if (created)
            try { File.Delete(Config.LastGoodPath); } catch { /* a copy left behind refills keys, as before */ }
        return written;
    }

    private static int? Port(JsonNode node, bool allowZero) =>
        node is JsonValue v && v.TryGetValue<int>(out var n) && Config.IsPort(n, allowZero) ? n : null;

    private static string Show(JsonNode node) => node?.ToJsonString() ?? "null";
}
