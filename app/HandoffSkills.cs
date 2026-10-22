using System.IO;

namespace Mastermind;

/// <summary>
/// "Use handoffs in this project" and its undo — the two skills Mastermind writes into a project's
/// <c>.claude/skills/</c>: <c>mastermind-handoff</c>, the writer, and <c>mastermind-process</c>, the processor. Plain
/// project skills, never a skills-directory plugin: a project plugin loads only where ~/.claude.json trusts the exact
/// key of its git root, which the VS Code extension never writes, while plain project skills load in every session.
/// Their text is embedded in the exe (<c>skills/</c>), the writer's address book filled
/// in at each write, each folder beside a <c>.gitignore</c> of <c>*</c> so this machine's project names never reach
/// git. The install also makes the handoff folders and <c>.mastermind/.gitignore</c> (staging only, so pending work is
/// committed) and puts the project's routing code into config's projects; the undo takes the code out first, then the
/// skills and staging. The roots it installed are kept in handoffs.json (<see cref="RootList"/>), so each start
/// re-renders every installed copy from this run's address book — a hand edit there is overwritten — and names one
/// whose code went from config. A project the manifest lists is refused here: each start renders its skills from the
/// manifest beside the installed ones (<see cref="RefreshInstalled"/>), and its folders and add-ons are the manifest
/// writer's. UI thread.
/// </summary>
internal static class HandoffSkills
{
    // Each skill's folder under .claude/skills/ and the embedded text it is rendered from.
    private static readonly (string Folder, string Resource)[] Skills =
    {
        ("mastermind-handoff", "handoff.SKILL.md"),
        ("mastermind-process", "process.SKILL.md"),
    };
    // .mastermind/.gitignore — its own staging/ only, never a staging/ inside a pending bundle, so pending work is committed
    private const string IgnoreText = "/staging/\n";

    /// <summary>What an install or undo did: Error, else Ok; NeedCode when the project has no code and none was given;
    /// Notes for the person; Keys, the config keys written — the caller restarts when any were.</summary>
    internal sealed record Outcome(string Error, bool NeedCode, List<string> Notes, List<string> Keys)
    {
        public bool Ok => Error == null && !NeedCode;
    }

    internal static Outcome Fail(string error) => new(error, false, new List<string>(), new List<string>());

    public static bool IsInstalled(string root) =>
        Skills.Any(s => File.Exists(Path.Combine(ClaudePaths.SkillsDir(root), s.Folder, "SKILL.md")));

    /// <summary>The install: the skills rendered, the folders made, the code — and the relay when
    /// <paramref name="relayOn"/> — written to config; the restart that write sets off re-renders every other installed
    /// writer with the new code (<see cref="RefreshInstalled"/>), and no code routes before it, config being read once a
    /// run. A project with a code keeps it; one without takes <paramref name="code"/>, or answers NeedCode.</summary>
    public static Outcome Install(string root, string code, bool relayOn, Action<string> warn)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return Fail($"no folder {root}");
        var (book, manifest, failed) = ProjectList.Fresh();
        if (failed) return Fail("the manifest can't be read right now — try again in a moment");
        if (HookEntries.IsListed(manifest, root))
            return Fail($"{Paths.Leaf(root)} is listed in the manifest — its handoff setup is the manifest writer's");
        var key = Paths.Key(root);
        var own = TokenOf(book, root);
        var token = !string.IsNullOrEmpty(own) ? own : code?.Trim();
        if (string.IsNullOrEmpty(token)) return new Outcome(null, true, new List<string>(), new List<string>());
        if (!PromptRoute.IsToken(token)) return Fail($"{token} is not a routing code — uppercase letters and digits, starting with a letter");
        var other = book.FirstOrDefault(p => p.Token == token && Paths.Key(p.Root) != key);
        if (other != null) return Fail($"{token} is {Paths.Leaf(other.Root)}'s code already — a code two projects claim routes nowhere");

        bool fresh = !IsInstalled(root);
        // A session open since before the project had a .claude/skills/ does not watch it (the skills docs) — though
        // one picked up a project's first .claude/skills/ at once on extension v2.1.304.
        bool watched = Directory.Exists(ClaudePaths.SkillsDir(root));
        var error = Render(root, token, book) ?? MakeFolders(root);
        if (error != null) return Fail(error);
        var keys = ConfigWriter.WriteProject(root, token, relayOn, warn);
        if (keys == null)
        {
            // The code is what routes here: without it a first install leaves no skills behind to advertise one.
            if (fresh) RemoveSkills(root, new List<string>());
            return Fail("config.json not written");
        }
        RootList.Set(ClaudePaths.HandoffsJson, root, true);
        var notes = new List<string>
        {
            watched ? "Sessions already open there see /mastermind-handoff and /mastermind-process at once."
                    : "Sessions already open there see /mastermind-handoff and /mastermind-process after /reload-skills, or once restarted.",
            "The mastermind-handoff and mastermind-process folders in .claude/skills are Mastermind's: it rewrites them at each start.",
        };
        return new Outcome(null, false, notes, keys);
    }

    /// <summary>The undo: the code out of config first — once it is gone nothing routes here, so a delete that fails
    /// after it strands nothing — then the skills, staging, the handoff folders left empty, Mastermind's own
    /// .mastermind/.gitignore and the folders it emptied, all deleted silently (a click is answered in the reply, never
    /// a dialog on the UI thread). Handoffs still waiting are named and keep .mastermind/; so does anything of the
    /// person's there, the add-on files included. The restart the config write sets off re-renders every other installed
    /// writer without the code.</summary>
    public static Outcome Uninstall(string root, Action<string> warn)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return Fail($"no folder {root}");
        var (book, manifest, failed) = ProjectList.Fresh();
        if (failed) return Fail("the manifest can't be read right now — try again in a moment");
        var key = Paths.Key(root);
        bool recorded = RootList.Read(ClaudePaths.HandoffsJson).Any(r => Paths.Key(r) == key);
        if (HookEntries.IsListed(manifest, root) && !recorded)
            return Fail($"{Paths.Leaf(root)} is listed in the manifest — its handoff setup is the manifest writer's");
        var own = TokenOf(book, root);
        if (!IsInstalled(root) && !recorded && string.IsNullOrEmpty(own)) return Fail($"{Paths.Leaf(root)} does not use handoffs");
        var keys = ConfigWriter.WriteProject(root, null, false, warn);
        if (keys == null) return Fail("config.json not written");
        RootList.Set(ClaudePaths.HandoffsJson, root, false);
        var notes = new List<string>();
        RemoveSkills(root, notes);
        var skills = ClaudePaths.SkillsDir(root);   // .claude/skills/ — and .claude/ — when the install left them empty
        RemoveIfEmpty(skills, notes);
        RemoveIfEmpty(Path.GetDirectoryName(skills)!, notes);
        var staging = PromptRoute.StagingDir(root);
        Remove(staging, notes);
        var waiting = Waiting(root);
        if (waiting.Count > 0) notes.Add($"still waiting: {string.Join(", ", waiting)} — their folders stay");
        foreach (var d in new[] { PromptRoute.ProcessingDir(root), PromptRoute.InboxDir(root), PromptRoute.OutboxDir(root) })
            RemoveIfEmpty(d, notes);   // deepest first, should a config nest them
        var home = Path.GetDirectoryName(staging)!;   // .mastermind/
        var ignore = Path.Combine(home, ".gitignore");
        try { if (Json.ReadShared(ignore)?.Replace("\r\n", "\n") == IgnoreText) File.Delete(ignore); }
        catch (Exception ex) { notes.Add($".mastermind/.gitignore not removed ({ex.Message})"); }
        RemoveIfEmpty(home, notes);
        return new Outcome(null, false, notes, keys);
    }

    /// <summary>At startup, beside the hook entries' repoint: every root in handoffs.json, and every project the manifest
    /// lists, re-rendered from this run's address book — a code changed in config, a project added or gone, a newer
    /// build's skills. One log line per root that failed, whose code went from config (the reset is deleting
    /// config.json), or that an install recorded and the manifest lists now; a book that read short re-renders nothing
    /// this start.</summary>
    public static List<(LogKind Kind, string Text)> RefreshInstalled()
    {
        var lines = new List<(LogKind Kind, string Text)>();
        var installed = RootList.Read(ClaudePaths.HandoffsJson);
        var (book, manifest, failed) = ProjectList.Fresh();
        if (failed)
        {
            lines.Add((LogKind.Warn, "handoffs · the manifest unreadable — the skills not refreshed this start"));
            return lines;
        }
        var manifestRoots = manifest?.Projects?.Where(p => !string.IsNullOrEmpty(p?.Root)).Select(p => p.Root).ToList() ?? new List<string>();
        var recorded = installed.Select(Paths.Key).ToHashSet();
        foreach (var root in installed.Concat(manifestRoots).DistinctBy(Paths.Key))
        {
            if (!Directory.Exists(root)) continue;
            var token = TokenOf(book, root);
            var listed = HookEntries.IsListed(manifest, root);
            var error = recorded.Contains(Paths.Key(root)) && listed ? "the manifest lists it now — run Stop using handoffs there"
                : string.IsNullOrEmpty(token) && listed ? "the manifest lists it with no routing code — set one there"
                : string.IsNullOrEmpty(token) ? "its code is gone from config — run Use handoffs in this project there again"
                : Render(root, token, book);
            if (error != null) lines.Add((LogKind.Warn, $"handoffs · {Paths.Leaf(root)}: {error}"));
        }
        return lines;
    }

    /// <summary>The routing code the book holds for <paramref name="root"/>, or null.</summary>
    private static string TokenOf(IEnumerable<ProjectEntry> book, string root)
    {
        var key = Paths.Key(root);
        return book.FirstOrDefault(p => Paths.Key(p.Root) == key)?.Token;
    }

    /// <summary>Each skill's .gitignore, then its SKILL.md — so the text is never there unignored — each written only
    /// when its text changed; null when written, else why not.</summary>
    private static string Render(string root, string code, IReadOnlyList<ProjectEntry> book)
    {
        foreach (var (folder, resource) in Skills)
        {
            var dir = Path.Combine(ClaudePaths.SkillsDir(root), folder);
            foreach (var (path, text) in new[] { (Path.Combine(dir, ".gitignore"), "*\n"), (Path.Combine(dir, "SKILL.md"), Fill(Resource(resource), root, code, book)) })
            {
                string now = null;
                try { now = Json.ReadShared(path); } catch { /* an unreadable file reads as changed */ }
                if (now != text && !Json.WriteAtomic(path, text))
                    return $"{Path.GetRelativePath(root, path)} not written — the crash log has why";
            }
        }
        return null;
    }

    /// <summary>A skill's placeholders filled: this project's code, its handoff folders as paths from its root, and the
    /// address book — every other project's code and folder name, by code.</summary>
    private static string Fill(string template, string root, string code, IReadOnlyList<ProjectEntry> book)
    {
        var key = Paths.Key(root);
        var rows = book.Where(p => !string.IsNullOrEmpty(p.Token) && Paths.Key(p.Root) != key)
                       .OrderBy(p => p.Token, StringComparer.Ordinal)
                       .Select(p => $"| {p.Token} | {Paths.Leaf(p.Root)} |").ToList();
        var table = rows.Count == 0 ? "_No other project uses handoffs yet._" : "| Code | Project |\n|---|---|\n" + string.Join("\n", rows);
        string Rel(string dir) => Paths.Normalize(Path.GetRelativePath(root, dir));
        return template.Replace("{{CODE}}", code).Replace("{{BOOK}}", table)
                       .Replace("{{STAGING}}", Rel(PromptRoute.StagingDir(root)))
                       .Replace("{{OUTBOX}}", Rel(PromptRoute.OutboxDir(root)))
                       .Replace("{{PROCESSING}}", Rel(PromptRoute.ProcessingDir(root)));
    }

    /// <summary>One embedded skill's text, line endings as \n — the checkout may hand the build CRLF.</summary>
    private static string Resource(string name)
    {
        using var stream = typeof(HandoffSkills).Assembly.GetManifestResourceStream("Mastermind.skills." + name);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>The handoff folders and .mastermind/.gitignore — staging only, so outbox, inbox and processing are
    /// committed with the project; an existing .gitignore is the person's and stays. Null when made, else why not.</summary>
    private static string MakeFolders(string root)
    {
        try
        {
            foreach (var d in new[] { PromptRoute.OutboxDir(root), PromptRoute.InboxDir(root), PromptRoute.ProcessingDir(root), PromptRoute.StagingDir(root) })
                Directory.CreateDirectory(d);
        }
        catch (Exception ex) { return $"the handoff folders not made ({ex.Message})"; }
        var ignore = Path.Combine(Path.GetDirectoryName(PromptRoute.StagingDir(root))!, ".gitignore");
        return File.Exists(ignore) || Json.WriteAtomic(ignore, IgnoreText) ? null : ".mastermind/.gitignore not written — the crash log has why";
    }

    /// <summary>The handoffs still in the project's three folders — entries the filename grammar names, top level only,
    /// so a handoff folder a config nests in another never counts as one.</summary>
    private static List<string> Waiting(string root) =>
        new[] { PromptRoute.OutboxDir(root), PromptRoute.InboxDir(root), PromptRoute.ProcessingDir(root) }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.md").Where(f => PromptRoute.Parse(Path.GetFileName(f)) != null)
                .Concat(Directory.EnumerateDirectories(d).Where(x => PromptRoute.ParseDirectory(Path.GetFileName(x)) != null)))
            .Select(e => Paths.Normalize(Path.GetRelativePath(root, e)))
            .ToList();

    /// <summary>The two skill folders deleted silently; a failure becomes a note.</summary>
    private static void RemoveSkills(string root, List<string> notes)
    {
        foreach (var (folder, _) in Skills) Remove(Path.Combine(ClaudePaths.SkillsDir(root), folder), notes);
    }

    /// <summary>A folder of Mastermind's deleted silently, to the recycle bin where Windows can; a failure becomes a note.</summary>
    private static void Remove(string dir, List<string> notes)
    {
        if (!Directory.Exists(dir)) return;
        try { Platform.RecycleBin.DeleteSilently(dir); }
        catch (Exception ex) { notes.Add($"{dir} not removed ({ex.Message})"); }
    }

    /// <summary>A folder removed only when nothing is left in it — never the person's own files.</summary>
    private static void RemoveIfEmpty(string dir, List<string> notes)
    {
        if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) return;
        try { Directory.Delete(dir); }
        catch (Exception ex) { notes.Add($"{dir} not removed ({ex.Message})"); }
    }
}
