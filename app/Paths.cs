namespace Mastermind;

/// <summary>
/// Project-path identity. A project is keyed on its FULL normalized cwd path, never the folder leaf —
/// two checkouts that share a leaf (e.g. two <c>backend</c> folders) must stay distinct, or pruning and
/// dedup wipe/preserve the wrong cards. The leaf is for DISPLAY only. This is the one place the
/// normalize/leaf logic lives (it used to be copy-pasted across Sessions / VsCode / AgentEvent).
/// </summary>
internal static class Paths
{
    /// <summary>Forward slashes, no trailing slash, original case. "" for null/empty/whitespace.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return path.Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>Canonical project identity: the normalized path, lower-cased so matching is
    /// case-insensitive with plain ordinal equality. Compare keys with <c>==</c>; sets of keys can use the
    /// default comparer. NEVER key on <see cref="Leaf"/>.</summary>
    public static string Key(string path) => Normalize(path).ToLowerInvariant();

    /// <summary>True when a path key is the root key or sits under it — the prefix rule every "is this cwd in that
    /// folder" check shares. Both sides are <see cref="Key"/>s; an empty root holds nothing.</summary>
    public static bool Within(string key, string root) =>
        root.Length > 0 && (key == root || key.StartsWith(root + "/", StringComparison.Ordinal));

    /// <summary>The owner whose root holding this path is the deepest — a window opened inside another window's folder
    /// is the nearer match there — 0 when none holds it or two hold it at that depth. Roots are paths or keys.</summary>
    public static int DeepestHolder(string path, IEnumerable<KeyValuePair<int, IReadOnlyList<string>>> rootsByOwner)
    {
        var key = Key(path);
        if (key.Length == 0) return 0;
        // Each holding root is a prefix of the key, so the longer one is the deeper.
        var holding = rootsByOwner
            .Select(o => (Owner: o.Key, Depth: o.Value.Select(Key).Where(r => Within(key, r)).Select(r => r.Length).DefaultIfEmpty(-1).Max()))
            .Where(o => o.Depth >= 0).OrderByDescending(o => o.Depth).ToList();
        return holding.Count == 1 || (holding.Count > 1 && holding[0].Depth > holding[1].Depth) ? holding[0].Owner : 0;
    }

    /// <summary>Display-only last path segment, original case. Used for window-title scoring and the card's
    /// project label — never as an identity key.</summary>
    public static string Leaf(string path)
    {
        var p = Normalize(path);
        int i = p.LastIndexOf('/');
        return i >= 0 ? p[(i + 1)..] : p;
    }

    /// <summary>An exe under <c>bin\Debug\</c> or <c>bin\Release\</c> — a run.bat or IDE build, which never writes what
    /// outlives it (the login shortcut, a project's --hook entry) — never any folder named <c>bin</c>, which a shipped
    /// install may sit in.</summary>
    public static bool IsDevBuild(string exe) => exe != null && DevBuild.IsMatch(exe);

    private static readonly System.Text.RegularExpressions.Regex DevBuild =
        new(@"\\bin\\(Debug|Release)\\", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
