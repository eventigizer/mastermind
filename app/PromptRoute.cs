using System.IO;
using System.Text.RegularExpressions;

namespace Mastermind;

/// <summary>Parses a cross-agent handoff filename ({SRC}to{DST}__scope__topic.md) and resolves the DST token
/// against the per-project tokens of the merged project list (ProjectList — the manifest plus config). Resolution is
/// an EXACT token match — no leaf-name heuristics — so two checkouts of the same class (two per-event mobile
/// apps) can never capture each other's prompts; an undeclared token stays unresolved and the caller flags it
/// (UNKNOWN DEST), never guesses. SRC is display-only; DST drives routing.</summary>
internal static class PromptRoute
{
    // {SRC}to{DST}__...  — uppercase-letter-led tokens (digits allowed after the first char, so an
    // event-year-bearing token stays legal) + literal `to` + `__`. `to` is lowercase, so a greedy token
    // match can never swallow the separator.
    private const string Token = "[A-Z][A-Z0-9]*";
    private static readonly Regex Name = new($@"^(?<src>{Token})to(?<dst>{Token})__.*\.md$", RegexOptions.Compiled);
    private static readonly Regex TokenOnly = new($@"^{Token}\z", RegexOptions.Compiled);

    public readonly record struct Parsed(string Src, string Dst, string FileName);

    public static Parsed? Parse(string fileName)
    {
        var m = Name.Match(fileName);
        return m.Success ? new Parsed(m.Groups["src"].Value, m.Groups["dst"].Value, fileName) : null;
    }

    /// <summary>A BUNDLE directory carries the same grammar minus the extension, so this borrows the regex
    /// above rather than declaring a second one — a filename grammar spelled twice is a contract with no
    /// compiler, and the narrower copy is the one that goes quiet. The returned FileName is the real directory
    /// name; only the MATCH borrows the suffix.</summary>
    public static Parsed? ParseDirectory(string dirName)
    {
        var p = Parse(dirName + ".md");
        return p is null ? null : new Parsed(p.Value.Src, p.Value.Dst, dirName);
    }

    /// <summary>A routing code the filename grammar accepts — the install's check, off the same token pattern the
    /// filename regex is built from, so the grammar is never spelled twice.</summary>
    public static bool IsToken(string token) => token != null && TokenOnly.IsMatch(token);

    /// <summary>The address book — the merged project list re-read now (<see cref="ProjectList.Fresh"/>), so a
    /// manifest rewrite routes without a restart — with the manifest it came from. ManifestFailed: the manifest could not be read
    /// this tick, so the book may be short.</summary>
    public static (List<ProjectEntry> List, ProjectsManifest Manifest, bool ManifestFailed) Projects() => ProjectList.Fresh();

    private static readonly HashSet<string> _warnedTokens = new(StringComparer.Ordinal);   // UI thread (the tick) only

    /// <summary>DST token → the root of the one project carrying exactly that token. Null when none does (the caller
    /// flags UNKNOWN DEST, never guesses — the retired bare MOB / MOBFUNC spellings included) and when two do: a token
    /// two projects claim routes nowhere, and warns once.</summary>
    public static string DestRoot(string dst, IReadOnlyList<ProjectEntry> projects)
    {
        string root = null;
        int claims = 0;
        foreach (var p in projects)
            if (p != null && !string.IsNullOrEmpty(p.Token) && string.Equals(p.Token, dst, StringComparison.Ordinal))
            {
                root ??= p.Root;
                claims++;
            }
        if (claims < 2) return root;
        if (_warnedTokens.Add(dst)) Warnings.Add($"relay · token {dst} is claimed by {claims} projects — it routes nowhere until one drops it");
        return null;
    }

    /// <summary>A project's three handoff folders — config's outbox / inbox / processing, each relative to the project
    /// root.</summary>
    public static string OutboxDir(string root) => Path.GetFullPath(Path.Combine(root, Config.Current.Outbox));
    public static string InboxDir(string root) => Path.GetFullPath(Path.Combine(root, Config.Current.Inbox));
    public static string ProcessingDir(string root) => Path.GetFullPath(Path.Combine(root, Config.Current.Processing));

    /// <summary>A project's staging folder — drafts on their way to the outbox. Fixed, never config: the start-time
    /// clean recycles what sits here, and a configured path could point it at the project itself or its neighbours.</summary>
    public static string StagingDir(string root) => Path.GetFullPath(Path.Combine(root, ".mastermind", "staging"));

    /// <summary>What a dispatch types into the receiving session: config's dispatchText with {path} as the file's path
    /// from the project root, forward slashes. The one builder — both sends and the send ▾ preview.</summary>
    public static string DispatchText(string fileName) => Config.Current.DispatchText.Replace(
        "{path}", Paths.Normalize(Config.Current.Processing) + "/" + fileName);
}
