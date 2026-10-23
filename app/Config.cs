using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mastermind;

/// <summary>Every fleet- or user-specific value the exe runs on — built once by <see cref="Config"/>.</summary>
internal sealed record AppConfig
{
    public int HookPort { get; init; }
    public int DebugPort { get; init; }                  // 0 = off
    public string ClaudeDir { get; init; }
    public string EditorUserDataDir { get; init; }
    public string EditorProcessName { get; init; }
    public string EditorCli { get; init; }
    public bool EditorSet { get; init; }                 // the file sets an editor key — it overrides every window's own
    public string ManifestPath { get; init; }            // "" = none
    public string PromptsPath { get; init; }
    public string LogDir { get; init; }
    public IReadOnlyList<ProjectEntry> Projects { get; init; }
    public bool HandoffsEnabled { get; init; }
    public string Outbox { get; init; }                  // the three handoff folders, each relative to a project root
    public string Inbox { get; init; }
    public string Processing { get; init; }
    public string DispatchText { get; init; }            // {path} = the file's path from the project root
    public bool NewSessionAutoAllow { get; init; }
    public bool WhatsappEfficiencyFix { get; init; }
    public bool StartAtLogin { get; init; }

    /// <summary>The file was read and parsed and raised no warning — the only state that refreshes the last-good copy
    /// and lets anything be removed (the login shortcut). A typo key blocks both until it is fixed, on purpose.</summary>
    public bool Clean { get; init; }
}

/// <summary>
/// The exe's config, <c>%LOCALAPPDATA%\Mastermind\config.json</c> — plain JSON, loaded on first use and never again
/// (a change takes a restart). Read key by key, never deserialized whole, so one bad value cannot sink the file: a key
/// of the wrong type takes its last-good value, else its default, and warns; a missing key takes the same fallback and
/// warns when the last-good copy had it; a null key or block is unset — its default, quietly; an unknown key warns. A
/// file that does not parse — a comment included, named by its line — reads every key from the last-good copy, and
/// from the defaults when there is none; no file at all is the defaults. A clean read is copied to
/// <c>config.lastgood.json</c>. Warnings go to <see cref="Warnings"/>, never <see cref="Diag"/>, whose log
/// folder is itself read from here.
/// </summary>
internal static class Config
{
    /// <summary>%LOCALAPPDATA%\Mastermind — the variable first, as the VS Code shell reads it, else the
    /// known folder, which GetFolderPath answers whatever the variable says.</summary>
    public static string AppDir => Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local
            ? local : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mastermind");
    public static string FilePath => Path.Combine(AppDir, "config.json");
    public static string LastGoodPath => Path.Combine(AppDir, "config.lastgood.json");
    public static string DefaultPromptsPath => Path.Combine(AppDir, "prompts.json");

    /// <summary>A port config accepts: 1-65535, or 0 where 0 means off — the reader's rule and ConfigWriter's.</summary>
    public static bool IsPort(int n, bool allowZero) => n <= 65535 && (n > 0 || (allowZero && n == 0));

    /// <summary>A config path as the reader takes it — environment variables, a leading ~, relative to the app folder —
    /// so a writer finds the entry the reader would.</summary>
    public static string Expand(string path) => Reader.Expand(path);

    private static readonly Lazy<AppConfig> _current = new(Load);
    public static AppConfig Current => _current.Value;

    private static readonly string[] TopKeys = { "hookPort", "debugPort", "claudeDir", "editor", "manifestPath",
        "promptsPath", "logDir", "projects", "handoffs", "newSessionAutoAllow", "whatsappEfficiencyFix", "startAtLogin" };
    private static readonly string[] EditorKeys = { "userDataDir", "processName", "cli" };
    private static readonly string[] HandoffKeys = { "enabled", "outbox", "inbox", "processing", "dispatchText" };
    private static readonly string[] ProjectKeys = { "root", "token", "label", "hide" };

    private static AppConfig Load()
    {
        var warn = new List<string>();
        AppConfig cfg;
        try { cfg = Read(warn); }
        catch (Exception ex)
        {
            warn.Add($"config · load failed ({ex.Message}) — running on defaults");
            cfg = Build(new Reader(null, null, false, new List<string>()));
        }
        foreach (var w in warn) Warnings.Add(w);
        return cfg;
    }

    private static AppConfig Read(List<string> warn)
    {
        string text = null;
        bool missing = false;
        try { text = Json.ReadShared(FilePath); missing = text == null; }
        catch (System.Text.DecoderFallbackException) { warn.Add($"config · {FilePath} is not UTF-8 text — save it as UTF-8"); }
        catch (Exception ex) { warn.Add($"config · {FilePath} unreadable: {ex.Message}"); }

        JsonElement? file = text == null ? null : Parse(text, warn);
        JsonElement? good = null;
        try
        {
            var g = Json.ReadShared(LastGoodPath);
            if (g != null) { using var gd = JsonDocument.Parse(g); good = gd.RootElement.Clone(); }
        }
        catch { /* an unreadable last-good copy is no copy */ }

        if (missing)
            warn.Add($"config · no {FilePath} — running on defaults until one is written there, then restart Mastermind");
        else if (file == null)
            warn.Add(good != null ? "config · running on the last good copy" : "config · no last good copy — running on defaults");

        // No file at all means defaults, never the last-good copy: deleting config.json is how a reset is made, and a
        // copy resurrected behind it would make the toast above a lie.
        var r = new Reader(file, missing ? null : good, file != null, warn);
        r.WarnUnknown(TopKeys);
        var cfg = Build(r);
        cfg = cfg with { Clean = file != null && warn.Count == 0 };   // after Build: its reads add warnings too
        if (cfg.Clean) SaveLastGood(text);
        return cfg;
    }

    private static AppConfig Build(Reader r)
    {
        var editor = r.Child("editor", EditorKeys);
        var handoffs = r.Child("handoffs", HandoffKeys);
        return new AppConfig
        {
            HookPort = r.Port("hookPort", 7778, allowZero: false),   // Mastermind's own
            DebugPort = r.Port("debugPort", 0, allowZero: true),
            // Never CLAUDE_CONFIG_DIR from this process's environment: a redeploy from an agent's shell carries the
            // session's variables and a login start does not, so the same exe would read two folders. The VS Code
            // shell sends claudeDir from the extension host's environment when it is set, and ConfigWriter writes it.
            ClaudeDir = r.PathStr("claudeDir",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")),
            EditorUserDataDir = editor.PathStr("userDataDir",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code")),
            EditorProcessName = editor.Str("processName", "Code"),
            EditorCli = editor.Str("cli", "code"),
            EditorSet = EditorKeys.Any(editor.Has),
            ManifestPath = r.PathStr("manifestPath", ""),
            PromptsPath = r.PathStr("promptsPath", DefaultPromptsPath),
            LogDir = r.PathStr("logDir", AppDir),
            Projects = r.Projects(),
            HandoffsEnabled = handoffs.Bool("enabled", false),
            Outbox = handoffs.Str("outbox", ".mastermind/outbox"),
            Inbox = handoffs.Str("inbox", ".mastermind/inbox"),
            Processing = handoffs.Str("processing", ".mastermind/processing"),
            // The processor skill first: sent alone it runs the skill, and appended after a hand-typed draft the name,
            // a separate word, counts as leave to run it — only a message starting with it runs it.
            DispatchText = handoffs.Str("dispatchText", "/mastermind-process {path}"),
            NewSessionAutoAllow = r.Bool("newSessionAutoAllow", false),
            WhatsappEfficiencyFix = r.Bool("whatsappEfficiencyFix", false),
            StartAtLogin = r.Bool("startAtLogin", false),
        };
    }

    /// <summary>The file's top-level object, or null when the file is not one — a comment named by its line, since
    /// config.json is plain JSON for every reader (the exe, the extension shell, any script).</summary>
    private static JsonElement? Parse(string text, List<string> warn)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc.RootElement.Clone();
            warn.Add("config · the top level is not an object — the file is not used");
            return null;
        }
        catch (JsonException ex)
        {
            long line = (ex.LineNumber ?? 0) + 1;
            bool comment;
            try
            {
                using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                comment = true;
            }
            catch (JsonException) { comment = false; }
            warn.Add(comment
                ? $"config · line {line} holds a comment — config.json is plain JSON, so the file is not used"
                : $"config · does not parse at line {line} ({ex.Message}) — the file is not used");
            return null;
        }
    }

    private static void SaveLastGood(string text)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            var tmp = LastGoodPath + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, LastGoodPath, overwrite: true);
        }
        catch { /* the copy is a fallback, never a reason to fail the load */ }
    }

    /// <summary>Key-by-key reads over one object of the file, with the last-good copy's same object behind it.</summary>
    private sealed class Reader
    {
        private readonly JsonElement? _file, _good;
        private readonly bool _parsed;          // the file parsed — only then can a key be "missing" from it
        private readonly List<string> _warn;
        private readonly string _at;            // "" at the top, "editor." inside a block — names the key in a warning

        public Reader(JsonElement? file, JsonElement? good, bool parsed, List<string> warn, string at = "")
        {
            _file = file is { ValueKind: JsonValueKind.Object } ? file : null;
            _good = good is { ValueKind: JsonValueKind.Object } ? good : null;
            _parsed = parsed;
            _warn = warn;
            _at = at;
        }

        /// <summary>A nested block's reader. A null block is unset — every key in it takes its default, quietly; a block
        /// of any other wrong type warns and reads as absent.</summary>
        public Reader Child(string key, string[] known)
        {
            JsonElement? f = null, g = null;
            if (_file is JsonElement fe && fe.TryGetProperty(key, out var fv))
            {
                if (fv.ValueKind == JsonValueKind.Null) return new Reader(null, null, false, _warn, _at + key + ".");
                if (fv.ValueKind == JsonValueKind.Object) f = fv;
                else _warn.Add($"config · {_at}{key} is not an object — its keys take their last good values, else defaults");
            }
            if (_good is JsonElement ge && ge.TryGetProperty(key, out var gv)) g = gv;
            var child = new Reader(f, g, _parsed, _warn, _at + key + ".");
            child.WarnUnknown(known);
            return child;
        }

        public void WarnUnknown(string[] known)
        {
            if (_file is not JsonElement fe) return;
            foreach (var p in fe.EnumerateObject())
                if (!known.Contains(p.Name)) _warn.Add($"config · unknown key {_at}{p.Name} — ignored (a typo?)");
        }

        /// <summary>The file — never the last-good copy — holds a non-null value under the key: a value set by hand,
        /// which an editor block uses to override every window's own.</summary>
        public bool Has(string key) =>
            _file is JsonElement fe && fe.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null;

        /// <summary>The file's value when it converts, else the last-good copy's, else the default. JSON null is "unset"
        /// for every key: the default, no warning, the last-good copy not consulted — how a value is cleared.</summary>
        private T Pick<T>(string key, Func<JsonElement, (bool Ok, T Value)> conv, T def)
        {
            (bool Ok, T Value) Good() =>
                _good is JsonElement ge && ge.TryGetProperty(key, out var gv) ? conv(gv) : (false, default);
            if (_file is JsonElement fe && fe.TryGetProperty(key, out var fv))
            {
                if (fv.ValueKind == JsonValueKind.Null) return def;
                var c = conv(fv);
                if (c.Ok) return c.Value;
                var bad = Good();
                _warn.Add($"config · {_at}{key} is not a valid value — using its {(bad.Ok ? "last good value" : "default")}");
                return bad.Ok ? bad.Value : def;
            }
            var g = Good();
            if (g.Ok && _parsed) _warn.Add($"config · {_at}{key} is missing — keeping its last good value");
            return g.Ok ? g.Value : def;
        }

        public string Str(string key, string def) => Pick<string>(key, e =>
            e.ValueKind == JsonValueKind.String ? (true, string.IsNullOrWhiteSpace(e.GetString()) ? def : e.GetString())
            : (false, null), def);

        /// <summary>A path: %VAR% expanded and a leading ~ read as the user profile, so the spellings the key list itself
        /// uses (~/.claude, %APPDATA%\Code) mean what they say instead of a folder named "~" under the working dir. A path
        /// still relative after that resolves against the config file's folder — never the working directory, which
        /// differs by launch path (an agent shell's redeploy, a login start), so one config means one set of files.</summary>
        public string PathStr(string key, string def) => Expand(Str(key, def));

        public static string Expand(string path)
        {
            var p = Environment.ExpandEnvironmentVariables(path);
            if (p == "~" || p.StartsWith("~/") || p.StartsWith("~\\"))
                p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 2 ? p[2..] : "");
            if (p.Length > 0 && !Path.IsPathRooted(p)) p = Path.GetFullPath(Path.Combine(AppDir, p));
            return p;
        }

        public bool Bool(string key, bool def) => Pick<bool>(key, e =>
            e.ValueKind is JsonValueKind.True or JsonValueKind.False ? (true, e.GetBoolean()) : (false, false), def);

        public int Port(string key, int def, bool allowZero) => Pick<int>(key, e =>
            e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && IsPort(n, allowZero)
                ? (true, n) : (false, 0), def);

        /// <summary>The project overrides. An entry with no root is skipped; a bad field in one warns and reads as
        /// unset.</summary>
        public IReadOnlyList<ProjectEntry> Projects() => Pick<IReadOnlyList<ProjectEntry>>("projects", e =>
        {
            if (e.ValueKind != JsonValueKind.Array) return (false, null);
            var list = new List<ProjectEntry>();
            foreach (var item in e.EnumerateArray())
                if (Entry(item) is ProjectEntry p) list.Add(p);
            return (true, list);
        }, Array.Empty<ProjectEntry>());

        private ProjectEntry Entry(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) { _warn.Add("config · projects holds a non-object — skipped"); return null; }
            var root = Json.Str(e, "root");
            if (string.IsNullOrWhiteSpace(root)) { _warn.Add("config · a projects entry has no root — skipped"); return null; }
            root = Expand(root);
            var at = $"projects[{Paths.Leaf(root)}]";
            foreach (var p in e.EnumerateObject())
                if (!ProjectKeys.Contains(p.Name)) _warn.Add($"config · unknown key {at}.{p.Name} — ignored (a typo?)");
            string S(string k)
            {
                if (!e.TryGetProperty(k, out var v) || v.ValueKind == JsonValueKind.Null) return null;
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) return v.GetString();
                _warn.Add($"config · {at}.{k} is not a non-empty string — unset");
                return null;
            }
            bool? hide = null;
            if (e.TryGetProperty("hide", out var h) && h.ValueKind != JsonValueKind.Null)
            {
                if (h.ValueKind is JsonValueKind.True or JsonValueKind.False) hide = h.GetBoolean();
                else _warn.Add($"config · {at}.hide is not true or false — unset");
            }
            return new ProjectEntry { Root = root, Token = S("token"), Label = S("label"), Hide = hide };
        }
    }
}
