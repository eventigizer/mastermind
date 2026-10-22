using System.IO;
using System.Text.RegularExpressions;

namespace Mastermind;

/// <summary>
/// The card's wording for a hook event: the permission and live-action phrases, the project-relative path, the prompt cleaner, the markdown strip,
/// and the line a WAITING card leads with. Pure functions — no disk, no UI.
///
/// Detail strings stay UNTRUNCATED: the card's detail box scrolls, and on a Stop the text IS the conclusion read to
/// decide what to do next — a cap cuts exactly the part worth having. A long permission command is likewise when all
/// of it must be seen before granting.
/// </summary>
internal static class HookText
{
    // The Notification types a card shows, by notification_type: a dialog waiting on a grant, or the agent waiting on
    // you. The rest — auth, completion, quota, plugin, computer use, push — change nothing the panel tracks.
    private static readonly HashSet<string> PermissionTypes = new(StringComparer.Ordinal)
    { "permission_prompt", "worker_permission_prompt" };
    private static readonly HashSet<string> WaitTypes = new(StringComparer.Ordinal)
    { "idle_prompt", "elicitation_dialog", "elicitation_url_dialog", "agent_needs_input" };

    public static bool IsPermissionNotification(string type) => PermissionTypes.Contains(type ?? "");
    public static bool IsAttentionNotification(string type) => IsPermissionNotification(type) || WaitTypes.Contains(type ?? "");

    // Wrapper blocks the harness injects as their own user turns, or prepends to a real prompt.
    private static readonly Regex Wrap = new(
        @"<(ide_opened_file|ide_selection|system-reminder|command-name|command-message|command-args"
        + @"|local-command-stdout|task-notification)>.*?</\1>", RegexOptions.Singleline);

    /// <summary>The genuine human prompt: injected wrapper blocks stripped, whitespace collapsed — or "".</summary>
    public static string CleanPrompt(string text) => string.IsNullOrEmpty(text) ? "" : Collapse(Wrap.Replace(text, ""));

    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+");
    private static readonly Regex Quote   = new(@"^(\s*)>\s?");
    private static readonly Regex Bullet  = new(@"^(\s*)[*+]\s+");
    private static readonly Regex Link    = new(@"\[([^\]]+)\]\([^)]*\)");
    private static readonly Regex Bold    = new(@"\*\*(.+?)\*\*");

    /// <summary>
    /// Markdown → readable plain text for the detail box, which renders raw characters: the line structure kept (the
    /// bullets and paragraphs are the readability), the inline noise stripped — <c>**bold**</c>, inline code ticks,
    /// <c>[text](url)</c> links, ATX headings, blockquote markers — <c>*</c> / <c>+</c> bullets normalised to
    /// <c>-</c>, blank runs squeezed. Single <c>_</c> and <c>*</c> stay: they live inside code identifiers.
    /// </summary>
    public static string MdToText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = new List<string>();
        bool blank = false;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            line = Heading.Replace(line, "", 1);
            line = Quote.Replace(line, "$1", 1);
            line = Bullet.Replace(line, "$1- ", 1);
            line = Link.Replace(line, "$1");
            line = Bold.Replace(line, "$1");
            line = line.Replace("`", "");
            if (line.Trim().Length == 0)
            {
                if (lines.Count > 0 && !blank) lines.Add("");   // squeeze blank runs; no leading blank
                blank = true;
            }
            else { lines.Add(line); blank = false; }
        }
        return string.Join("\n", lines).TrimEnd();
    }

    /// <summary>The path relative to <paramref name="cwd"/>, forward-slashed, when it sits under it — else the file
    /// name — so a card reads <c>src/foo.ts</c> rather than a long absolute path.</summary>
    public static string ShortPath(string fp, string cwd)
    {
        if (string.IsNullOrEmpty(fp)) return "";
        try
        {
            if (!string.IsNullOrEmpty(cwd))
            {
                var rel = Path.GetRelativePath(cwd, fp);
                if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) return rel.Replace('\\', '/');
            }
        }
        catch { /* an unparsable path reads as its name */ }
        return Path.GetFileName(fp);
    }

    /// <summary>What a PermissionRequest wants — "run npm test", "edit src/foo.ts" — framed by the card as
    /// "wants to …"; falls back to "use &lt;tool&gt;", then "".</summary>
    public static string PermissionAction(string toolName, IReadOnlyDictionary<string, string> input, string cwd)
    {
        var name = toolName ?? "";
        switch (name)
        {
            case "Bash":
            {
                var cmd = Collapse(In(input, "command"));   // newlines and runs collapsed, so a heredoc flows
                return cmd.Length > 0 ? "run " + cmd : "run a command";
            }
            case "Edit": case "MultiEdit": case "NotebookEdit":
            {
                var fp = ShortPath(First(In(input, "file_path"), In(input, "notebook_path")), cwd);
                return fp.Length > 0 ? "edit " + fp : "edit a file";
            }
            case "Write":
            {
                var fp = ShortPath(In(input, "file_path"), cwd);
                return fp.Length > 0 ? "write " + fp : "write a file";
            }
            case "Read":
            {
                var fp = ShortPath(In(input, "file_path"), cwd);
                return fp.Length > 0 ? "read " + fp : "read a file";
            }
            case "Glob": case "Grep": return (name.ToLowerInvariant() + " " + In(input, "pattern")).Trim();
            case "WebFetch":  return ("fetch " + In(input, "url")).Trim();
            case "WebSearch": return ("search " + In(input, "query")).Trim();
            case "Task": case "Agent":
                return ("run agent " + First(In(input, "description"), In(input, "subagent_type"))).Trim();
            default: return name.Length > 0 ? "use " + name : "";
        }
    }

    /// <summary>The live phrase for a tool starting — "running npm test", "editing src/foo.ts" — for a command, a
    /// file change or a subagent only; any other tool gives "", a heartbeat that leaves the detail line alone.</summary>
    public static string PreToolAction(string toolName, IReadOnlyDictionary<string, string> input, string cwd)
    {
        switch (toolName ?? "")
        {
            case "Bash":
            {
                var cmd = Collapse(In(input, "command"));
                return cmd.Length > 0 ? "running " + cmd : "running a command";
            }
            case "Edit": case "MultiEdit": case "NotebookEdit":
            {
                var fp = ShortPath(First(In(input, "file_path"), In(input, "notebook_path")), cwd);
                return fp.Length > 0 ? "editing " + fp : "editing a file";
            }
            case "Write":
            {
                var fp = ShortPath(In(input, "file_path"), cwd);
                return fp.Length > 0 ? "writing " + fp : "writing a file";
            }
            case "Task": case "Agent":
                return ("spawning agent: " + First(In(input, "description"), In(input, "subagent_type"))).Trim();
            default: return "";
        }
    }

    /// <summary>What a Stop waits on — each background task (its description, else its command, else its type) and
    /// each session cron (its next wake, off its schedule) — joined with "; ", or "" when the payload lists neither.</summary>
    public static string WaitingOn(IReadOnlyList<HookTask> tasks, IReadOnlyList<string> cronSchedules, DateTime now)
    {
        var items = new List<string>();
        foreach (var t in tasks ?? Array.Empty<HookTask>())
            items.Add(First(Collapse(t.Description), Collapse(t.Command), t.Type, "a background task"));
        foreach (var schedule in cronSchedules ?? Array.Empty<string>())
            items.Add(NextCronFire(schedule, now) is DateTime at
                ? "a wake-up " + (at.Date == now.Date ? $"at {at:HH:mm}" : $"{at:MMM d HH:mm}")
                : "a wake-up on " + schedule);
        return string.Join("; ", items);
    }

    /// <summary>The first minute after <paramref name="from"/>'s at which a five-field cron expression fires, local
    /// time, within a year — read as the engine reads it (v2.1.295): numbers, <c>*</c>, ranges, steps on <c>*</c> or
    /// a range, comma lists; a day field unrestricted only when it covers every value; when both day fields are
    /// restricted, either one matching fires. Null for anything the engine itself refuses.</summary>
    public static DateTime? NextCronFire(string schedule, DateTime from)
    {
        var f = (schedule ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5) return null;
        var min = CronField(f[0], 0, 59); var hour = CronField(f[1], 0, 23); var dom = CronField(f[2], 1, 31);
        var mon = CronField(f[3], 1, 12); var dow = CronField(f[4], 0, 7);
        if (min == null || hour == null || dom == null || mon == null || dow == null) return null;
        if (dow[7]) dow[0] = true;   // 7 is Sunday too
        bool domAny = dom.Skip(1).All(x => x), dowAny = dow.Take(7).All(x => x);
        var start = new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0).AddMinutes(1);
        for (var day = start.Date; day <= start.Date.AddDays(366); day = day.AddDays(1))
        {
            if (!mon[day.Month]) continue;
            bool d = dom[day.Day], w = dow[(int)day.DayOfWeek];
            // An unrestricted day field matches every day, so this is "both match" with either one unrestricted and
            // "either matches" with both restricted — the engine's rule.
            if (!(domAny || dowAny ? d && w : d || w)) continue;
            for (int h = 0; h < 24; h++)
            {
                if (!hour[h]) continue;
                for (int m = 0; m < 60; m++)
                    if (min[m] && day.AddHours(h).AddMinutes(m) >= start) return day.AddHours(h).AddMinutes(m);
            }
        }
        return null;
    }

    /// <summary>One cron field as the set of values it matches — <c>*</c> or <c>a-b</c>, each with an optional
    /// <c>/step</c>, or a bare <c>n</c>, comma-joined — or null for anything else or out of range.</summary>
    private static bool[] CronField(string field, int lo, int hi)
    {
        var set = new bool[hi + 1];
        foreach (var part in field.Split(','))
        {
            int slash = part.IndexOf('/'), step = 1;
            var range = slash >= 0 ? part[..slash] : part;
            if (slash >= 0 && (!int.TryParse(part[(slash + 1)..], out step) || step < 1)) return null;
            int a, b;
            if (range == "*") { a = lo; b = hi; }
            else
            {
                int dash = range.IndexOf('-');
                if (dash >= 0)
                {
                    if (!int.TryParse(range[..dash], out a) || !int.TryParse(range[(dash + 1)..], out b)) return null;
                }
                else
                {
                    if (slash >= 0 || !int.TryParse(range, out a)) return null;   // `n/step`: the engine refuses it
                    b = a;
                }
            }
            if (a < lo || b > hi || a > b) return null;
            for (int i = a; i <= b; i += step) set[i] = true;
        }
        return set;
    }

    private static string In(IReadOnlyDictionary<string, string> input, string key) =>
        input != null && input.TryGetValue(key, out var v) ? v ?? "" : "";

    private static string Collapse(string s) =>
        string.Join(" ", (s ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

    private static string First(params string[] values)
    {
        foreach (var v in values) if (!string.IsNullOrEmpty(v)) return v;
        return "";
    }
}
