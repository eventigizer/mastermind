using System.IO;
using System.Text.Json;

namespace Mastermind;

/// <summary>What <see cref="Sessions.ReadMeta"/> pulls from a session .jsonl. <c>Compacted</c> = a compaction
/// marker sits AFTER the last usage record, so the usage describes the pre-compact conversation —
/// <c>ContextTokens</c> is forced 0 and the card shows a "compacted" state instead of the stale number.</summary>
internal readonly record struct SessionMeta(string Cwd, string Title, long ContextTokens, string LastStop,
    IReadOnlyList<TodoItem> Todos, string Model, bool Compacted = false, string LastReply = "", string LastPrompt = "");

/// <summary>
/// Builds the "currently live" list: one row per RUNNING Claude agent (from the live-session registry,
/// see <see cref="LiveSessions"/>), enriched with that session's title + context size from its transcript
/// under ~/.claude/projects/. An open VS Code window with no live agent still gets a placeholder row so you
/// can jump there. Lets Mastermind double as a session switcher across live agents.
/// </summary>
internal static class Sessions
{
    private static string ProjectsDir => ClaudePaths.Projects;

    public static List<AgentEvent> ScanActive()
    {
        var result = new List<AgentEvent>();
        var covered = new HashSet<string>();   // project keys already represented by a live agent

        try
        {
            foreach (var s in LiveSessions.Active())
            {
                var (meta, when) = ReadSession(s.SessionId, s.Cwd);
                // Restore the real last status: remembered hook event (StopWaiting restores WAITING), else a
                // transcript that ended its turn (= finished, your turn — the WAITING rule lives only in the hook),
                // else a neutral OPEN baseline row.
                var ev = StatusCache.Get(s.SessionId) ?? (meta.LastStop == "end_turn" ? "Stop" : null);
                // Your last prompt: the tail's, newest by construction when it holds one; else the one status.json
                // kept, for a prompt a long turn's output pushed out of the tail.
                var prompt = meta.LastPrompt.Length > 0 ? meta.LastPrompt : StatusCache.Prompt(s.SessionId) ?? "";
                var card = ev != null
                    ? AgentEvent.Restored(s.Cwd, s.SessionId, meta.Title, meta.ContextTokens, when, ev, meta.Todos, meta.Model,
                        meta.LastReply, prompt)
                    : AgentEvent.Baseline(s.Cwd, s.SessionId, meta.Title, meta.ContextTokens, when, meta.Todos, meta.Model,
                        prompt);
                card.ContextCompacted = meta.Compacted;
                result.Add(card);
                covered.Add(Paths.Key(s.Cwd));
            }
        }
        catch (Exception ex) { Diag.Warn("scanActive", ex); /* best effort */ }

        // Any open VS Code window with no live agent yet -> still show it (focus works, no deep-link).
        // The key (full path) doubles as the placeholder's cwd, so focus + ProjectKey both resolve.
        foreach (var key in VsCode.OpenProjectKeys())
            if (!covered.Contains(key))
                result.Add(AgentEvent.Baseline(key, "", "", 0, DateTime.Now));

        return SessionStore.CardOrder(result, ProjectList.Order);
    }

    /// <summary>
    /// Resolve a session id (with its known cwd) to its transcript path, then read its meta + last-write
    /// time. Claude Code's project-folder slug = the cwd with <c>\ / :</c> replaced by <c>-</c>, so we open
    /// ONE file directly; the full <c>~/.claude/projects</c> walk stays as a fallback for a slug miss.
    /// </summary>
    private static (SessionMeta meta, DateTime when) ReadSession(string sessionId, string cwd)
    {
        var path = LocateTranscript(sessionId, cwd);
        if (path == null) return (new SessionMeta(cwd, "", 0, "", Array.Empty<TodoItem>(), ""), DateTime.Now);
        return (ReadMeta(path), File.GetLastWriteTime(path));
    }

    /// <summary>
    /// Does this session have a transcript on disk? A live registry entry whose <c>.jsonl</c> is gone (deleted
    /// while the claude process is still up — only its companion dir lingers) is a GHOST: Mastermind can't read
    /// a title and would show a dead "open in VS Code" card. Reuses <see cref="LocateTranscript"/> (direct slug
    /// then dir-walk fallback) so it's robust even if the registry cwd ever drifts off the launch cwd — the
    /// walk only runs when the direct slug misses (i.e. only for the rare ghost), so the poll stays cheap.
    /// </summary>
    public static bool HasTranscript(string sessionId, string cwd) => LocateTranscript(sessionId, cwd) != null;

    /// <summary>When a session's newest user or assistant record was written, in UTC — its <c>timestamp</c>, off the
    /// transcript's tail, or the file's last write when the tail holds none — or null when no transcript is found:
    /// session restore's activity test. A reopen with nothing typed adds no such record. Null too when the projects
    /// folder is missing, which the caller tells apart.</summary>
    public static DateTime? LastMessageAt(string sessionId, string cwd)
    {
        var path = LocateTranscript(sessionId, cwd);
        if (path == null) return null;
        try
        {
            DateTime? newest = null;
            foreach (var line in TailLines(path))
            {
                if (!line.Contains("\"timestamp\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                        && type.GetString() is "user" or "assistant"
                        && root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTimeOffset(out var at)
                        && (newest == null || at.UtcDateTime > newest))
                        newest = at.UtcDateTime;
                }
                catch { /* a line mid-write */ }
            }
            return newest ?? File.GetLastWriteTimeUtc(path);
        }
        catch { return null; }
    }

    /// <summary>True when a message typed into the session — a user record, not a command's expansion — carries
    /// <paramref name="text"/>, off the transcript's tail: a port-off dispatch was sent once its file name shows there.
    /// False when no transcript is found.</summary>
    public static bool UserSaid(string sessionId, string cwd, string text)
    {
        var path = LocateTranscript(sessionId, cwd);
        if (path == null || string.IsNullOrEmpty(text)) return false;
        try
        {
            foreach (var line in TailLines(path))
            {
                if (!line.Contains(text, StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var type) && type.GetString() == "user"
                        && !(root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True))
                        return true;
                }
                catch { /* a line mid-write */ }
            }
        }
        catch { /* unreadable reads as unsent: the row keeps back to inbox */ }
        return false;
    }

    /// <summary>Re-read a session's transcript meta + last-write time (the reaper's working-or-idle read).</summary>
    public static (SessionMeta meta, DateTime when) Probe(string sessionId, string cwd) => ReadSession(sessionId, cwd);

    /// <summary>
    /// True if the session is actively WORKING — the reaper SPARES it (killing a mid-write agent risks a
    /// truncated file / wedged git). NO transcript (already gone) is NOT working — reap it. A last turn that
    /// ended (<c>end_turn</c>) is idle — reap it. A turn parked on a tool call (<c>tool_use</c>) is mid-work
    /// even when the tool writes nothing for minutes (a long build/e2e), so mtime alone would wrongly read it
    /// as idle — spare it, but ONLY within a 2h last-write window: without the cap a claude STRANDED mid-tool
    /// (wedged shell, dead subagent) days ago classified "working" forever and could never be reaped. 2h
    /// comfortably covers the longest legitimate runs. Otherwise fall back to "wrote within the ~3-min
    /// stall window".
    /// </summary>
    public static bool LooksWorking(string sessionId, string cwd)
    {
        if (!HasTranscript(sessionId, cwd)) return false;
        var (meta, when) = Probe(sessionId, cwd);
        if (meta.LastStop == "end_turn") return false;    // turn ended -> idle -> reap
        if (meta.LastStop == "tool_use")                   // parked on a (possibly long) tool call -> spare,
            return (DateTime.Now - when).TotalHours < 2;   // unless it's been silent past any legitimate run
        return (DateTime.Now - when).TotalSeconds < 180;   // else: a recent write means still active
    }

    private static string LocateTranscript(string sessionId, string cwd)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !Directory.Exists(ProjectsDir)) return null;
        try
        {
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                var direct = Path.Combine(ProjectsDir, SlugForCwd(cwd), sessionId + ".jsonl");
                if (File.Exists(direct)) return direct;
            }
            foreach (var dir in Directory.GetDirectories(ProjectsDir))
            {
                var path = Path.Combine(dir, sessionId + ".jsonl");
                if (File.Exists(path)) return path;
            }
        }
        catch { /* best effort */ }
        return null;
    }

    /// <summary>
    /// The display meta for a session id, read from its .jsonl: title + context size + the latest TodoWrite +
    /// the model. Used to name live event cards (a Stop/permission card otherwise shows only a #hash) and to
    /// drive the todo list. Empty meta if the file isn't found.
    /// Resolve this OFF the UI thread — it can read a multi-MB transcript.
    /// </summary>
    public static SessionMeta MetaForSession(string sessionId, string cwd = "")
    {
        var path = LocateTranscript(sessionId, cwd);
        if (path == null) return new SessionMeta(cwd, "", 0, "", Array.Empty<TodoItem>(), "");
        return ReadMeta(path);
    }

    /// <summary>Claude Code's project-folder slug for a cwd: path separators and the drive colon → '-'
    /// (e.g. <c>c:\code\proj</c> → <c>c--code-proj</c>).</summary>
    internal static string SlugForCwd(string cwd) =>
        cwd.Replace('\\', '-').Replace('/', '-').Replace(':', '-');

    /// <summary>Delete the .jsonl (and companion dir) for each given session id, across all projects.</summary>
    public static int DeleteSessions(HashSet<string> ids)
    {
        if (ids.Count == 0 || !Directory.Exists(ProjectsDir)) return 0;
        int count = 0;
        foreach (var dir in Directory.GetDirectories(ProjectsDir))
        {
            foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (!ids.Contains(id)) continue;
                try { File.Delete(f); count++; } catch { }
                var companion = Path.Combine(dir, id);
                try { if (Directory.Exists(companion)) Directory.Delete(companion, recursive: true); } catch { }
            }
        }
        return count;
    }

    /// <summary>Each id's transcript folder name — its project's slug — off one pass over the projects folder; an id
    /// with no transcript is left out.</summary>
    public static Dictionary<string, string> ProjectSlugs(HashSet<string> ids)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0 || !Directory.Exists(ProjectsDir)) return map;
        try
        {
            foreach (var dir in Directory.GetDirectories(ProjectsDir))
                foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
                {
                    var id = Path.GetFileNameWithoutExtension(f);
                    if (ids.Contains(id)) map.TryAdd(id, Path.GetFileName(dir));
                }
        }
        catch { /* best effort — an id left out counts as archived, as before */ }
        return map;
    }

    // Cache ReadMeta by (path, mtime, length). A transcript only changes when the session advances, so a
    // live event for an idle session (or a re-scan of unchanged files) reuses the parse instead of
    // re-reading the whole .jsonl — the one genuinely expensive path.
    private static readonly Dictionary<string, (long ticks, long len, SessionMeta meta)> _metaCache = new();
    private static readonly object _metaLock = new();

    private static SessionMeta ReadMeta(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            long ticks = fi.LastWriteTimeUtc.Ticks, len = fi.Length;
            lock (_metaLock)
                if (_metaCache.TryGetValue(path, out var c) && c.ticks == ticks && c.len == len)
                    return c.meta;
            var fresh = ReadMetaUncached(path);
            lock (_metaLock)
            {
                // Bound the cache: path-keyed entries otherwise accumulate forever across weeks of session
                // churn in a long-lived tray app. Blunt clear-at-cap is fine — entries rebuild on demand.
                if (_metaCache.Count > 100) _metaCache.Clear();
                _metaCache[path] = (ticks, len, fresh);
            }
            return fresh;
        }
        catch { return ReadMetaUncached(path); }
    }

    // Mutable accumulator threaded through the head + tail passes of a transcript scan.
    private sealed class MetaAccum
    {
        public string Cwd = "", AiTitle = "", CustomTitle = "", Summary = "", FirstUser = "";
        public string LastAsst = null;
        public string LastTodoWrite = null;        // latest TodoWrite tool_use line (tail wins) — the agent's task list
        public string LastPrompt = "";             // latest typed prompt or slash command IN THE TAIL — a restored card's you block
        public bool InTail = false;                // the tail pass is running — the head's first prompt is never the last one

        // ---- current model. TWO producers, and the LATER ONE IN THE FILE WINS (hence the seq stamps):
        //   ModelFromCmd — a `/model` slash command's own stdout ("Set model to Fable 5 for this session only").
        //                  Written the INSTANT the switch happens, which is the only reason the card can go red
        //                  without waiting for the next turn (a `/model` produces NO assistant turn and NO hook).
        //   LastAsst      — that record's `message.model`, i.e. what the last turn ACTUALLY ran on. Ground truth,
        //                  but one turn behind a just-issued switch.
        // So: a command record AFTER the last assistant record = a switch not yet exercised (trust the command);
        // an assistant record after it = the switch took effect (trust what ran). If the stdout wording ever
        // drifts, the assistant path still carries the feature — it just regains the one-turn lag.
        public int Seq = 0;                        // line counter across the head + tail passes, in file order
        public string ModelFromCmd = "";
        public int ModelCmdSeq = 0;
        public int LastAsstSeq = 0;
        public int CompactSeq = 0;                 // latest compaction marker (0 = none seen)
    }

    private const long TailBytes = 256 * 1024;   // tail window: titles + last-turn usage live here
    private const int  HeadLines = 50;           // head window: cwd / summary / first user message live here

    /// <summary>A transcript's last <see cref="TailBytes"/> as lines — the whole file when it is smaller, else from the
    /// first whole line after the seek. ReadWrite share so a live writer can't block us.</summary>
    private static IEnumerable<string> TailLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bool cut = fs.Length > TailBytes;
        if (cut) fs.Seek(-TailBytes, SeekOrigin.End);
        using var sr = new StreamReader(fs);
        if (cut) sr.ReadLine();   // the partial line the seek landed in (also absorbs any leading partial UTF-8 bytes)
        string line;
        while ((line = sr.ReadLine()) != null) yield return line;
    }

    /// <summary>
    /// Pull cwd + the session's display name + context size from a .jsonl (best effort). Small files are
    /// read whole; a large transcript is read HEAD + TAIL only — the cwd / summary / first user message are
    /// written once near the top, while a manual rename ("custom-title"), the generated "ai-title" and the
    /// last assistant turn's usage+stop_reason are re-emitted every turn so the latest live near the end.
    /// Skipping the middle turns this from O(filesize) to O(constant). Parsing is substring-gated so only
    /// the handful of relevant lines are actually deserialized.
    /// </summary>
    private static SessionMeta ReadMetaUncached(string path)
    {
        var a = new MetaAccum();
        try
        {
            if (new FileInfo(path).Length > TailBytes)
            {
                // HEAD — cwd / summary / first user message (written once, near the top).
                using var sr = new StreamReader(path);
                string line; int n = 0;
                while (n++ < HeadLines && (line = sr.ReadLine()) != null) Accumulate(line, a);
            }
            // TAIL — latest title + last assistant usage/stop_reason; a small file whole, a large one past its
            // (potentially multi-MB) middle.
            a.InTail = true;
            foreach (var tline in TailLines(path)) Accumulate(tline, a);
        }
        catch { /* unreadable */ }

        // Context size + last turn's stop_reason + the model it ran on, from the final assistant message
        // (one parse — `model` is a sibling of `usage`/`stop_reason` on the same response envelope).
        long ctx = 0; string lastStop = "", asstModel = "", lastReply = "";
        if (a.LastAsst != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(a.LastAsst);
                if (doc.RootElement.TryGetProperty("message", out var msg))
                {
                    // A turn that ended (end_turn) wrote its concluding text as this very record — the reply a
                    // restored FINISHED card shows, since the Stop payload that carried it died with the last run.
                    if (msg.TryGetProperty("stop_reason", out var end) && end.ValueKind == JsonValueKind.String
                        && end.GetString() == "end_turn"
                        && doc.RootElement.TryGetProperty("type", out var rt) && rt.GetString() == "assistant"
                        && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        lastReply = HookText.MdToText(string.Join("\n\n", content.EnumerateArray()
                            .Where(b => b.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                                        && b.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                            .Select(b => b.GetProperty("text").GetString())));
                    // input + both cache components = the call's PROMPT; + output = the conversation as it
                    // now stands (the reply joins the input on the very next call). Output included so the
                    // card tracks the same quantity the IDE's own context gauge means, not one reply behind.
                    if (msg.TryGetProperty("usage", out var u))
                        ctx = Json.Long(u, "input_tokens") + Json.Long(u, "cache_read_input_tokens")
                            + Json.Long(u, "cache_creation_input_tokens") + Json.Long(u, "output_tokens");
                    if (msg.TryGetProperty("stop_reason", out var srn) && srn.ValueKind == JsonValueKind.String)
                        lastStop = srn.GetString() ?? "";
                    if (msg.TryGetProperty("model", out var mdl) && mdl.ValueKind == JsonValueKind.String)
                        asstModel = mdl.GetString() ?? "";
                }
            }
            catch { /* leave ctx=0 */ }
        }

        // Compaction reset: a marker AFTER the last usage record means that usage describes the PRE-compact
        // conversation — the number is stale-high (the one case where the card would CONTRADICT the IDE's own
        // gauge, deep red over a freshly compacted session). Report compacted + zero instead; the next turn's
        // usage record clears it naturally (its seq passes the marker's).
        bool compacted = a.CompactSeq > a.LastAsstSeq;
        if (compacted) ctx = 0;

        // Current model: whichever producer sits LATER in the file (see MetaAccum). A `/model` after the last
        // turn is a switch not yet exercised; an assistant record after it is what actually ran.
        string model = a.ModelCmdSeq > a.LastAsstSeq && a.ModelFromCmd.Length > 0 ? a.ModelFromCmd
                     : asstModel.Length > 0                                       ? asstModel
                     : a.ModelFromCmd;

        // Manual rename > generated title > summary record > raw first message.
        var detail = a.CustomTitle != "" ? a.CustomTitle
                   : a.AiTitle != ""     ? a.AiTitle
                   : a.Summary != ""     ? a.Summary
                   : a.FirstUser;
        return new SessionMeta(a.Cwd, Truncate(detail, 120), ctx, lastStop,
            ParseTodos(a.LastTodoWrite), model, compacted, lastReply, a.LastPrompt);
    }

    /// <summary>
    /// Is this line a genuine compaction marker? STRUCTURAL, never substring-only: both marker spellings occur
    /// in ordinary prose whenever a session discusses compaction (this feature's own build session planted them
    /// in its transcript — same trap the model-toggle stdout gate documents), and prose lands JSON-STRING-
    /// escaped, so it cannot form a root-level field. Two shapes accepted, either sufficient — OBSERVED LIVE on
    /// a real compacted transcript (they land as adjacent records): a system record with
    /// <c>subtype: "compact_boundary"</c>, and the summary user record flagged <c>isCompactSummary: true</c> at
    /// the ROOT. The caller's loud first-fire log is the drift alarm if the spelling ever changes. Fail-open by
    /// construction: no marker → no reset → exactly the old (stale-until-next-turn) behaviour.
    /// </summary>
    private static bool IsCompactMarker(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.TryGetProperty("isCompactSummary", out var f) && f.ValueKind == JsonValueKind.True) return true;
            return r.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String
                && st.GetString() == "compact_boundary";
        }
        catch { return false; }
    }

    /// <summary>Pull the model name out of a <c>/model</c> command's stdout line — the text between
    /// "Set model to " and the harness's " for this session only" suffix (or the closing tag). Raw substring
    /// work on the JSON line: the phrase carries no quotes or backslashes, so there is nothing to unescape and
    /// no parse to pay for on a hot path. Returns "" on any shape miss (the assistant-record path then wins).</summary>
    private static string ExtractModelFromStdout(string line)
    {
        const string marker = "Set model to ";
        int i = line.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return "";
        int start = i + marker.Length;
        int end = line.Length;
        foreach (var stop in new[] { " for this session", "<", "\\n", "\"" })
        {
            int j = line.IndexOf(stop, start, StringComparison.Ordinal);
            if (j >= 0 && j < end) end = j;
        }
        return end > start ? line[start..end].Trim() : "";
    }

    /// <summary>Parse the latest TodoWrite line's <c>todos</c> array into <see cref="TodoItem"/>s. The line is
    /// an assistant message whose content carries a <c>tool_use</c> block named TodoWrite. Best effort — any
    /// shape miss returns an empty list (the card just hides the block).</summary>
    private static IReadOnlyList<TodoItem> ParseTodos(string line)
    {
        if (string.IsNullOrEmpty(line)) return Array.Empty<TodoItem>();
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("message", out var msg)) return Array.Empty<TodoItem>();
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return Array.Empty<TodoItem>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (!(block.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String
                      && bt.GetString() == "tool_use")) continue;
                if (!(block.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                      && nm.GetString() == "TodoWrite")) continue;
                if (!block.TryGetProperty("input", out var inp)) continue;
                if (!inp.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array)
                    return Array.Empty<TodoItem>();
                var list = new List<TodoItem>();
                foreach (var t in todos.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object) continue;
                    string c = t.TryGetProperty("content", out var cc) && cc.ValueKind == JsonValueKind.String
                        ? cc.GetString() ?? "" : "";
                    string st = t.TryGetProperty("status", out var ss) && ss.ValueKind == JsonValueKind.String
                        ? ss.GetString() ?? "pending" : "pending";
                    if (c.Length > 0) list.Add(new TodoItem { Content = c, Status = st });
                }
                return list;
            }
        }
        catch { /* shape miss — no todos */ }
        return Array.Empty<TodoItem>();
    }

    /// <summary>Fold one .jsonl line into the accumulator. cwd/summary/firstUser keep the FIRST value seen
    /// (head wins); title/lastAsst keep the LAST (tail wins) — the natural head-then-tail call order.</summary>
    private static void Accumulate(string line, MetaAccum a)
    {
        if (line.Length == 0 || line[0] != '{') return;
        a.Seq++;

        // Cheapest capture of the last assistant message (carries usage + stop_reason + model).
        if (line.Contains("\"cache_read_input_tokens\"")) { a.LastAsst = line; a.LastAsstSeq = a.Seq; }

        // Compaction marker — the substring is only the cheap prefilter; IsCompactMarker does the structural
        // check that keeps prose mentions of these tokens (JSON-string-escaped, so the quoted spelling below
        // cannot appear in them) from resetting a card.
        if (line.Contains("\"subtype\":\"compact_boundary\"") || line.Contains("\"isCompactSummary\":true"))
            if (IsCompactMarker(line)) a.CompactSeq = a.Seq;

        // A `/model` switch writes its own confirmation into the transcript as command stdout — the only
        // instant signal there is (it fires no hook and produces no assistant turn). THREE gates, and each one
        // is load-bearing: the phrase, its enclosing TAG, and the record being a USER turn. "Set model to …" and
        // even the whole tagged string occur in ordinary prose — an agent discussing this very feature quotes
        // them — and that text lands in the transcript as an ASSISTANT record. Without the type gate a session
        // would flip its own card red just by talking about the toggle. (Observed while building it.)
        if (line.Contains("local-command-stdout>Set model to ") && line.Contains("\"type\":\"user\""))
        {
            var m = ExtractModelFromStdout(line);
            if (m.Length > 0) { a.ModelFromCmd = m; a.ModelCmdSeq = a.Seq; }
        }

        // TODO list (#3): keep the latest TodoWrite tool_use line; parsed once at the end.
        if (line.Contains("\"TodoWrite\"")) a.LastTodoWrite = line;

        bool wantCwd     = a.Cwd == ""       && line.Contains("\"cwd\"");
        bool wantTitle   = line.Contains("-title\"");          // ai-title / custom-title (keep latest)
        bool wantSummary = a.Summary == ""   && line.Contains("\"summary\"");
        bool wantUser    = a.FirstUser == "" && line.Contains("\"type\":\"user\"");
        // A typed prompt: a user record that is neither a tool result, a command's expansion (isMeta) nor a
        // compaction summary.
        bool wantPrompt  = a.InTail && line.Contains("\"type\":\"user\"") && !line.Contains("\"tool_result\"")
                           && !line.Contains("\"isMeta\":true") && !line.Contains("\"isCompactSummary\":true");
        if (!(wantCwd || wantTitle || wantSummary || wantUser || wantPrompt)) return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;

            if (wantCwd && r.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String)
                a.Cwd = c.GetString() ?? "";

            var type = r.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;

            // A manual rename ("custom-title") is what VS Code shows when set; otherwise the
            // generated "ai-title". Both repeat every turn, so the last value seen is current.
            if (type == "custom-title" && r.TryGetProperty("customTitle", out var ct)
                && ct.ValueKind == JsonValueKind.String)
                a.CustomTitle = ct.GetString() ?? "";
            else if (type == "ai-title" && r.TryGetProperty("aiTitle", out var at)
                && at.ValueKind == JsonValueKind.String)
                a.AiTitle = at.GetString() ?? "";
            else if (wantSummary && type == "summary" && r.TryGetProperty("summary", out var s))
                a.Summary = s.GetString() ?? "";
            else if (wantUser && type == "user" && r.TryGetProperty("message", out var m)
                && m.TryGetProperty("content", out var content))
                a.FirstUser = ExtractText(content);

            // The prompt a card's you block shows, as UserPromptSubmit words it (HookText.CleanPrompt) — EVERY text
            // block joined, since the editor's context (an opened file, a selection) can ride as its own block ahead
            // of what you typed; a slash command lands as tags the clean strips, so it reads as its name and arguments.
            if (wantPrompt && type == "user" && r.TryGetProperty("message", out var pm)
                && pm.TryGetProperty("content", out var pc))
            {
                var raw = AllText(pc);
                var prompt = HookText.CleanPrompt(raw);
                if (prompt.Length == 0 && Command.Match(raw) is { Success: true } cmd)
                    prompt = HookText.CleanPrompt(cmd.Groups[1].Value + " " + cmd.Groups[2].Value);
                if (prompt.Length > 0) a.LastPrompt = prompt;
            }
        }
        catch { /* skip unparsable line */ }
    }

    private static readonly System.Text.RegularExpressions.Regex Command = new(
        @"<command-name>(.*?)</command-name>(?:.*?<command-args>(.*?)</command-args>)?",
        System.Text.RegularExpressions.RegexOptions.Singleline);

    private static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind == JsonValueKind.Array)
            foreach (var item in content.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var txt)
                    && txt.ValueKind == JsonValueKind.String)
                    return txt.GetString() ?? "";
        return "";
    }

    private static string AllText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", content.EnumerateArray()
            .Where(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            .Select(i => i.GetProperty("text").GetString()));
    }

    private static string Truncate(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }
}
