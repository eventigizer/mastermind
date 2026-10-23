using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mastermind;

/// <summary>
/// The one project list: the manifest's entries (root + routing token) merged with config's overrides, matched
/// per project on the normalized root (<see cref="Paths.Key"/>). A config field that is set beats the manifest's, an
/// unset one keeps it. Manifest projects keep the manifest's order — the card order — so an override never reorders
/// the cards; config-only projects follow in config order. A card finds its entry by folder
/// name (<see cref="Paths.Leaf"/>), as the card order and the chips always have, so two checkouts sharing a folder
/// name share an entry.
/// </summary>
internal static class ProjectList
{
    private static readonly object _snapLock = new();
    private static List<ProjectEntry> _snapshot;       // the first good read, kept for the session
    private static List<ProjectEntry> _shortList;      // a failed read's list, served until the next retry
    private static DateTime _shortAt;

    /// <summary>The list as it stood at the first good read — chips, card order and hide read only this, so a
    /// manifest rewrite changes them at the next restart, as it always has. Until a read succeeds, the short list a failed read
    /// left is served and the read retried at most once per poll tick (8 s); <see cref="SnapshotGood"/> says which.</summary>
    public static IReadOnlyList<ProjectEntry> Snapshot
    {
        get
        {
            lock (_snapLock)
            {
                if (_snapshot != null) return _snapshot;
                if (_shortList != null && DateTime.UtcNow - _shortAt < TimeSpan.FromSeconds(8)) return _shortList;
                var (manifest, failed) = ReadManifest();
                var list = Merge(manifest?.Projects ?? new List<ProjectEntry>(), Config.Current.Projects);
                if (failed) { _shortList = list; _shortAt = DateTime.UtcNow; }
                else _snapshot = list;
                return list;
            }
        }
    }

    /// <summary>True once the snapshot came from a good read.</summary>
    public static bool SnapshotGood { get { lock (_snapLock) return _snapshot != null; } }

    /// <summary>The list re-read now — the relay's address book, re-read every tick so a manifest rewrite routes without a
    /// restart — with the manifest it was merged from. ManifestFailed as <see cref="ReadManifest"/> says — the list may
    /// be short.</summary>
    public static (List<ProjectEntry> List, ProjectsManifest Manifest, bool ManifestFailed) Fresh()
    {
        var (manifest, failed) = ReadManifest();
        return (Merge(manifest?.Projects ?? new List<ProjectEntry>(), Config.Current.Projects), manifest, failed);
    }

    private static bool _failing;   // the crash log gets one line per stretch of failed reads, not one per tick

    /// <summary>The one read of the manifest (config's manifestPath) — the address book, the snapshot and the hook
    /// drift check all come through here. (null, false) when none is configured; (null, true) when one is configured but
    /// cannot be had — absent included, since File.Exists also answers false on an access error; else the manifest.
    /// The read holds the file only for its own length, and its writer retries a replace it blocks.</summary>
    public static (ProjectsManifest Manifest, bool Failed) ReadManifest()
    {
        var path = Config.Current.ManifestPath;
        if (string.IsNullOrEmpty(path)) return (null, false);
        try
        {
            var text = Json.ReadShared(path) ?? throw new FileNotFoundException("manifest not found", path);
            var manifest = JsonSerializer.Deserialize<ProjectsManifest>(text);
            _failing = false;
            return (manifest, false);
        }
        catch (Exception ex)
        {
            if (!_failing) { _failing = true; Diag.Warn("manifest", ex); }
            return (null, true);
        }
    }

    private static List<ProjectEntry> Merge(IEnumerable<ProjectEntry> manifest, IEnumerable<ProjectEntry> overrides)
    {
        var list = new List<ProjectEntry>();
        var byKey = new Dictionary<string, ProjectEntry>();
        foreach (var m in manifest)
        {
            if (m == null || string.IsNullOrEmpty(m.Root)) continue;
            var key = Paths.Key(m.Root);
            if (byKey.ContainsKey(key)) continue;
            var e = new ProjectEntry { Root = m.Root, Token = m.Token };
            byKey[key] = e;
            list.Add(e);
        }
        foreach (var o in overrides)
        {
            var key = Paths.Key(o.Root);
            if (!byKey.TryGetValue(key, out var e))
            {
                e = new ProjectEntry { Root = o.Root };
                byKey[key] = e;
                list.Add(e);
            }
            if (!string.IsNullOrEmpty(o.Token)) e.Token = o.Token;
            if (o.Label != null) e.Label = o.Label;
            if (o.Hide != null) e.Hide = o.Hide;
        }
        return list;
    }

    /// <summary>A card's entry, by its project folder name, or null.</summary>
    public static ProjectEntry ForLeaf(string projectName) =>
        Snapshot.FirstOrDefault(p => string.Equals(Paths.Leaf(p.Root), projectName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sort rank for a project's display name: its index in the list, else <see cref="int.MaxValue"/>, so every
    /// unlisted project ties there and the caller's name tiebreaker orders them A→Z after the list.</summary>
    public static int Order(string projectName)
    {
        var list = Snapshot;
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(Paths.Leaf(list[i].Root), projectName, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    /// <summary>True when the card's project is hidden in config — the one hide test, applied at every way a card enters
    /// the store and, on the hook path, before the panel can pop.</summary>
    public static bool IsHidden(string cwd) => ForLeaf(Paths.Leaf(cwd))?.Hide == true;

    /// <summary>One activity-log line: the list as the chips and the card order read it — position, the chip label (the
    /// config label, else the token, else the folder), hidden — so a restart's log shows what config
    /// made of the manifest.</summary>
    public static string Describe()
    {
        var parts = Snapshot.Select((p, i) =>
        {
            var name = !string.IsNullOrEmpty(p.Label) ? p.Label : !string.IsNullOrEmpty(p.Token) ? p.Token : Paths.Leaf(p.Root);
            return $"{i + 1} {name}" + (p.Hide == true ? " hidden" : "");
        });
        return "projects · " + string.Join(", ", parts) + (SnapshotGood ? "" : " (manifest unreadable — retrying)");
    }
}
