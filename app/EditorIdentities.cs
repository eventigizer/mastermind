using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Serialization;

namespace Mastermind;

/// <summary>A VS Code-family editor as a window's shell names it in its hello: the image name its windows run under,
/// its settings folder (the one holding <c>User\</c> and <c>logs\</c>) and its command-line tool — a full path from a
/// hello, a bare name from config — and the UTC date a window of it last connected.</summary>
internal sealed class EditorIdentity
{
    [JsonPropertyName("processName")] public string ProcessName { get; set; } = "";
    [JsonPropertyName("userDataDir")] public string UserDataDir { get; set; } = "";
    [JsonPropertyName("cli")] public string Cli { get; set; } = "";
    [JsonPropertyName("lastSeen")] public DateOnly? LastSeen { get; set; }   // null on config's, which no window stamps

    [JsonIgnore] public string Key => $"{ProcessName}|{UserDataDir}".ToLowerInvariant();
}

/// <summary>
/// The editors Mastermind works with: each connected window names its own in its hello, so VS Code and a look-alike
/// running at once each get their windows raised and their archived sessions read — no config write, no restart.
/// Every editor a hello named is remembered in <c>editors.json</c> with the UTC date a window of it last connected, so
/// Clean hidden sessions, which runs with every editor closed, still reaches each one's state — for 14 days after that,
/// so an editor tried once never holds the projects it opened out of Clean and Reap for good.
/// Config's editor block, when the file sets a key, replaces them all — the manual override; with no editor seen within
/// the 14 days, config's defaults, regular VS Code, stand in. Written on the UI thread by ClientHub, read from any
/// thread.
/// </summary>
internal static class EditorIdentities
{
    private const int SeenDays = 14;   // an editor stays in play this long after its last window connected
    private static readonly object _lock = new();
    private static readonly ConcurrentDictionary<int, EditorIdentity> _byHost = new();
    private static readonly ConcurrentDictionary<int, IReadOnlyList<string>> _foldersByHost = new();   // path keys
    private static List<EditorIdentity> _seen;   // under _lock

    private static EditorIdentity Configured => new()
    {
        ProcessName = Config.Current.EditorProcessName,
        UserDataDir = Config.Current.EditorUserDataDir,
        Cli = Config.Current.EditorCli,
    };

    private static List<EditorIdentity> Seen() =>
        _seen ??= (Json.Load<List<EditorIdentity>>(ClaudePaths.EditorsJson) ?? new())
            .Where(e => !string.IsNullOrEmpty(e?.ProcessName)).ToList();

    /// <summary>A window's hello named its editor: held for that extension host — a part the hello left empty taken
    /// from config's — and remembered with today's UTC date, <c>editors.json</c> rewritten at most once a day per
    /// editor.</summary>
    public static void Connected(int hostPid, EditorIdentity named)
    {
        if (hostPid <= 0 || string.IsNullOrEmpty(named?.ProcessName)) return;
        var configured = Configured;
        var editor = new EditorIdentity
        {
            ProcessName = named.ProcessName,
            UserDataDir = string.IsNullOrEmpty(named.UserDataDir) ? configured.UserDataDir : named.UserDataDir,
            Cli = string.IsNullOrEmpty(named.Cli) ? configured.Cli : named.Cli,
            LastSeen = DateOnly.FromDateTime(DateTime.UtcNow),
        };
        _byHost[hostPid] = editor;
        lock (_lock)
        {
            var seen = Seen();
            int at = seen.FindIndex(e => e.Key == editor.Key);
            if (at >= 0 && seen[at].Cli == editor.Cli && seen[at].LastSeen == editor.LastSeen) return;
            if (at >= 0) seen[at] = editor; else seen.Add(editor);
            Json.SaveAtomic(ClaudePaths.EditorsJson, seen);
        }
    }

    /// <summary>The folders the window under this extension host holds — its hello's, then each folders
    /// message's.</summary>
    public static void Folders(int hostPid, IEnumerable<string> folders) =>
        _foldersByHost[hostPid] = folders.Select(Paths.Key).Where(k => k.Length > 0).ToList();

    /// <summary>No window under this extension host is connected any more.</summary>
    public static void Disconnected(int hostPid)
    {
        _byHost.TryRemove(hostPid, out _);
        _foldersByHost.TryRemove(hostPid, out _);
    }

    /// <summary>Every editor in play: config's alone when the file sets its editor block, else each editor a window
    /// connected from within <see cref="SeenDays"/> days — whole UTC days, so no clock time moves the edge — else
    /// config's defaults.</summary>
    public static IReadOnlyList<EditorIdentity> All()
    {
        if (Config.Current.EditorSet) return new[] { Configured };
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-SeenDays);
        List<EditorIdentity> list;
        lock (_lock) list = Seen().Where(e => e.LastSeen >= since).ToList();
        return list.Count > 0 ? list : new List<EditorIdentity> { Configured };
    }

    /// <summary>The editors of the windows connected now, else config's — the live reads' set (the archived-set
    /// cache), which never pays a state.vscdb copy for an editor nobody has open.</summary>
    public static IReadOnlyList<EditorIdentity> ConnectedEditors()
    {
        if (Config.Current.EditorSet) return new[] { Configured };
        var list = _byHost.Values.GroupBy(e => e.Key).Select(g => g.First()).ToList();
        return list.Count > 0 ? list : new List<EditorIdentity> { Configured };
    }

    public static IReadOnlyList<string> ProcessNames() =>
        All().Select(e => e.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The editor whose window runs under this extension host — config's when the file sets it, else that
    /// window's hello's, else the first in play.</summary>
    public static EditorIdentity ForHost(int hostPid) =>
        Config.Current.EditorSet ? Configured : _byHost.TryGetValue(hostPid, out var e) ? e : All()[0];

    /// <summary>The image name of the editor whose connected window holds this folder — the deepest-folder window's, as
    /// a panel's placement picks it, so a dev host opened inside another window's folder is its own; a card's raise
    /// looks among that editor's windows alone — or null when no connected window holds it or two hold it at that
    /// depth.</summary>
    public static string ProcessNameFor(string cwd)
    {
        int host = Paths.DeepestHolder(cwd, _foldersByHost);
        return host != 0 ? ForHost(host).ProcessName : null;
    }

    /// <summary>The command-line tool a file opens with: config's when the file sets it, else a connected window's full
    /// path, else the first in play's.</summary>
    public static string Cli() =>
        Config.Current.EditorSet ? Config.Current.EditorCli
        : _byHost.Values.FirstOrDefault(e => !string.IsNullOrEmpty(e.Cli))?.Cli ?? All()[0].Cli;
}
