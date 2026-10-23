using System.Linq;
using System.Text.Json.Nodes;

namespace Mastermind;

/// <summary>
/// "Mastermind: Projects" — the page's rows and its save. The rows are every project Mastermind knows: the
/// manifest's and config's entries in the list's order, then each folder an open editor window holds and each running
/// session's launch folder that no row already covers. A manifest project's order and routing code are the manifest's
/// — the page shows them locked — while its name and hide are config's, like any other project's. A save
/// replaces config's projects, every non-manifest row kept so its place holds, and restarts Mastermind as a settings
/// change does.
/// </summary>
internal static class ProjectsPage
{
    /// <summary>The page's rows. UI thread.</summary>
    public static JsonArray Rows()
    {
        var (list, manifest, _) = ProjectList.Fresh();
        var rows = new JsonArray();
        var covered = new List<string>();
        void Add(string root, ProjectEntry p, bool fromManifest)
        {
            covered.Add(Paths.Key(root));
            rows.Add(new JsonObject
            {
                ["root"] = root,
                ["name"] = Paths.Leaf(root),
                ["token"] = p?.Token ?? "",
                ["label"] = p?.Label ?? "",
                ["hide"] = p?.Hide == true,
                ["manifest"] = fromManifest,
            });
        }
        foreach (var p in list) Add(p.Root, p, HookEntries.IsListed(manifest, p.Root));
        foreach (var folder in IdeRegistry.OpenWorkspaceFolders().Concat(LiveSessions.Snapshot.Values.Select(s => s.Cwd)))
        {
            var key = Paths.Key(folder ?? "");
            if (key.Length > 0 && !covered.Any(root => Paths.Within(key, root))) Add(folder, null, false);
        }
        return rows;
    }

    /// <summary>The page's rows checked and written as config's projects: a non-manifest row keeps its place and its
    /// code; a manifest row is written only when it sets a name or hide. Refused, naming the row: a code outside the
    /// grammar (PromptRoute's), a code another project holds — a manifest's included — or a code cleared on a project
    /// that uses handoffs. The keys written, or why not.</summary>
    public static (List<string> Keys, string Error) Save(JsonArray rows, Action<string> warn)
    {
        var (list, manifest, failed) = ProjectList.Fresh();
        if (failed) return (null, "the manifest can't be read right now — save again in a moment");
        var codes = new Dictionary<string, string>();   // code → the folder holding it
        foreach (var m in manifest?.Projects ?? new List<ProjectEntry>())
            if (!string.IsNullOrEmpty(m?.Token)) codes[m.Token] = m.Root;
        var projects = new JsonArray();
        foreach (var node in rows)
        {
            if (node is not JsonObject r || Json.Str(r["root"]) is not { Length: > 0 } root) continue;
            var name = Paths.Leaf(root);
            bool listed = HookEntries.IsListed(manifest, root);
            var entry = new JsonObject { ["root"] = root };
            var token = Json.Str(r["token"]).Trim();
            if (!listed && token.Length == 0 && CodeOf(list, root).Length > 0 && UsesHandoffs(root))
                return (null, $"{name} uses handoffs — run \"Stop using handoffs here\" there before clearing its code");
            if (!listed && token.Length > 0)
            {
                if (!PromptRoute.IsToken(token))
                    return (null, $"{name}: the code {token} is not uppercase letters and digits starting with a letter");
                if (codes.TryGetValue(token, out var holder) && Paths.Key(holder) != Paths.Key(root))
                    return (null, $"{name}: the code {token} is {Paths.Leaf(holder)}'s");
                codes[token] = root;
                entry["token"] = token;
            }
            if (Json.Str(r["label"]).Trim() is { Length: > 0 } label) entry["label"] = label;
            if (Json.True(r["hide"])) entry["hide"] = true;
            if (!listed || entry.Count > 1) projects.Add(entry);
        }
        var keys = ConfigWriter.WriteProjects(projects, warn);
        return keys == null ? (null, "config.json was not written — the activity log says why") : (keys, null);
    }

    /// <summary>The project's code in the list as it stands — config's, for a row the manifest does not set.</summary>
    private static string CodeOf(List<ProjectEntry> list, string root) =>
        list.FirstOrDefault(p => Paths.Key(p.Root) == Paths.Key(root))?.Token ?? "";

    /// <summary>The project carries the handoff skills or the install's record — clearing its code here would leave
    /// skills advertising a code no route holds, which HandoffSkills' uninstall pairs with the code's removal.</summary>
    private static bool UsesHandoffs(string root) =>
        HandoffSkills.IsInstalled(root) || RootList.Read(ClaudePaths.HandoffsJson).Any(r => Paths.Key(r) == Paths.Key(root));
}
