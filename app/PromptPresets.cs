using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Mastermind;

/// <summary>One canned prompt: a short menu <see cref="Label"/> and the full <see cref="Text"/> injected into
/// the session's composer.</summary>
public sealed record PromptPreset(string Label, string Text);

/// <summary>
/// The prompts the card's "send a prompt" picker offers, plus the go / rec replies, the model toggle's two commands
/// and what counts as the heavy model. Backed by config's promptsPath — point it at a git-tracked file and edits
/// travel over git; by default it is <c>prompts.json</c> in the app folder, written from the built-in prompts when missing. Read on startup and re-read LIVE on every save
/// (a <see cref="FileSystemWatcher"/> on that file name repopulates <see cref="All"/> in place). The XAML menu binds to
/// the stable <see cref="All"/> instance via {x:Static}; mutating its contents updates the open menu with no restart.
/// A configured path that is missing warns, and the built-in prompts stand. The Claude health check's prompt is a
/// second file beside it (<see cref="HealthCheckPath"/>), read at each open and never watched.
/// </summary>
public static class PromptPresets
{
    // The built-in prompts: written to the default prompts.json when it is missing, and the fallback when the
    // configured file is absent / corrupt / empty. One entry on purpose — a real menu lives in its prompts.json;
    // don't grow this into a copy of one.
    private static readonly PromptPreset[] Defaults =
    {
        new("Continue — no deferring, no skipping",
            "continue one by one till you finish or hit a wall — no deferring, no skipping"),
    };

    private const string DefaultGo = "go";
    private const string DefaultRec = "as you recommend";

    // The card's model toggle sends these two slash commands into the composer. They live in prompts.json
    // (not baked as constants) for the same reason `go` does: Claude Code's model aliases are version-
    // sensitive, so a rename stays a JSON edit that hot-reloads, never a rebuild + redeploy. Deliberately NOT
    // in `presets`: those are prose sent TO the agent, while these change WHAT THE AGENT IS — mixing them into
    // one menu makes a misclick between "Verify & fix" and a model switch trivially easy.
    // "fable", never "best": best falls back to Opus where Fable is unavailable, and an Opus session reads unlit against
    // heavy_match, so the next click sends model_on again with no way back — on Foundry, whose default is Sonnet, that
    // strands it on Opus. Without Fable, Claude Code refuses "/model fable" in the panel and the session stays as it
    // was.
    private const string DefaultModelOn  = "/model fable";
    private const string DefaultModelOff = "/model default";
    private const string DefaultHeavyMatch = "fable";

    /// <summary>The live picker source — a STABLE instance read each time the picker opens. Reloads mutate its
    /// contents (Clear + Add on the UI thread) so the picker reflects prompts.json without an app restart.</summary>
    public static readonly ObservableCollection<PromptPreset> All = new(Defaults);

    /// <summary>The card's two quick replies (NOT in the picker) — `go` and `rec`, each sent whole when the agent has
    /// handed you the turn. Editable via prompts.json's <c>go</c> / <c>rec</c> keys.</summary>
    public static string Go { get; private set; } = DefaultGo;
    public static string Rec { get; private set; } = DefaultRec;

    /// <summary>The card's model toggle: the command that switches a session ONTO the heavy model, and the one
    /// that puts it back on the default. Editable via prompts.json's <c>model_on</c> / <c>model_off</c> keys.</summary>
    public static string ModelOn  { get; private set; } = DefaultModelOn;
    public static string ModelOff { get; private set; } = DefaultModelOff;

    /// <summary>What counts as the heavy model: a card whose model name, any <c>[…]</c> suffix stripped, contains this
    /// (case-insensitively) shows the ⚡ toggle lit. prompts.json's <c>heavy_match</c>.</summary>
    public static string HeavyMatch { get; private set; } = DefaultHeavyMatch;

    /// <summary>Raised on the UI thread when a reload moves <see cref="HeavyMatch"/> — a card re-reads its heavy test only
    /// when its model name changes, so MainWindow re-lights every card on this (the hot-reload, no restart).</summary>
    public static event Action HeavyMatchChanged;

    /// <summary>The prompts.json this app reads + watches — config's promptsPath. The tray
    /// "Edit prompts" action opens exactly this file.</summary>
    public static string FilePath { get; private set; }

    private static FileSystemWatcher _watch;

    /// <summary>Load config's promptsPath (read on startup + watched for live edits). The default path is written from
    /// the built-in prompts when missing — the only file a read ever writes; a configured one that is missing warns
    /// and the built-ins stand. Call once at startup, before the window shows.</summary>
    public static void Initialize()
    {
        FilePath = Config.Current.PromptsPath;
        if (!File.Exists(FilePath))
        {
            if (IsDefaultPath(FilePath)) WriteBuiltIns();
            else Warnings.Add($"prompts · {FilePath} (config's promptsPath) is missing — using the built-in prompts");
        }
        if (File.Exists(FilePath)) Reload();
        StartWatch();
    }

    private static bool IsDefaultPath(string path)
    {
        try { return string.Equals(Path.GetFullPath(path), Path.GetFullPath(Config.DefaultPromptsPath), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>The Claude health check's prompt file — health_check.md in promptsPath's folder, so it follows config
    /// without a key of its own. The tray's "Edit this prompt…" opens exactly this file.</summary>
    public static string HealthCheckPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(FilePath))!, "health_check.md");

    /// <summary>The health check's text, read at each open — nothing watches the file, and the read never writes it.
    /// Missing, the built-in text stands with no warning, so a later build's corrected prompt reaches everyone who
    /// never edited theirs; only <see cref="EnsureHealthCheckFile"/> creates the file. A failed read warns, since the
    /// crash log alone goes unseen. Line endings come back as \n.</summary>
    public static string HealthCheckText()
    {
        var path = HealthCheckPath;
        string text = null;
        try { if (File.Exists(path)) text = File.ReadAllText(path); }
        catch (Exception ex)
        {
            Warnings.Add($"prompts · {path} could not be read ({ex.Message}) — using the built-in health check");
            Diag.Warn("prompts.healthcheck", ex);
        }
        if (string.IsNullOrWhiteSpace(text)) text = BuiltInHealthCheck();
        return text.Replace("\r\n", "\n").Trim();
    }

    /// <summary>The file "Edit this prompt…" opens — written from the built-in text first when missing, so the editor
    /// shows the text the check would send; from then on the file is the user's, and a later build's text no longer
    /// reaches it. A failed write warns, and the editor opens the missing path.</summary>
    public static string EnsureHealthCheckFile()
    {
        var path = HealthCheckPath;
        if (!File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, BuiltInHealthCheck());
            }
            catch (Exception ex)
            {
                Warnings.Add($"prompts · {path} could not be written ({ex.Message})");
                Diag.Warn("prompts.healthcheck.write", ex);
            }
        }
        return path;
    }

    /// <summary>The health check shipped in the exe — health_check.md, embedded at build time.</summary>
    private static string BuiltInHealthCheck()
    {
        using var stream = typeof(PromptPresets).Assembly.GetManifestResourceStream("Mastermind.health_check.md");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    /// <summary>The built-in prompts as a file a person can edit, at the default path.</summary>
    private static void WriteBuiltIns()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var model = new FileModel
            {
                go = DefaultGo, rec = DefaultRec, model_on = DefaultModelOn, model_off = DefaultModelOff,
                heavy_match = DefaultHeavyMatch,
                presets = Defaults.Select(p => new Item { label = p.Label, text = p.Text }).ToArray(),
            };
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(model, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep "—" readable
            }));
        }
        catch (Exception ex) { Diag.Warn("prompts.write", ex); }
    }

    // JSON shape (lowercase keys for friendly hand-editing):
    // { "go": "...", "rec": "...", "model_on": "...", "model_off": "...", "heavy_match": "...", "presets": [ { "label", "text" } ] }
    private sealed class FileModel
    {
        public string go { get; set; }
        public string rec { get; set; }
        public string model_on { get; set; }
        public string model_off { get; set; }
        public string heavy_match { get; set; }
        public Item[] presets { get; set; }
    }
    private sealed class Item { public string label { get; set; } public string text { get; set; } }

    private static readonly System.Text.Json.JsonSerializerOptions ReadOpts =
        new() { PropertyNameCaseInsensitive = true };

    private static void Reload()
    {
        // A file save can fire the watcher mid-write — read up to 3× before giving up, so a partial-write read
        // doesn't clobber the live menu down to defaults.
        FileModel model = null;
        for (int i = 0; i < 3 && model == null; i++)
        {
            try { model = System.Text.Json.JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath), ReadOpts); }
            catch { if (File.Exists(FilePath)) System.Threading.Thread.Sleep(60); }
        }
        if (model == null) return;   // gone, or still unreadable after retries — keep the current menu, never blank it

        var items = (model.presets ?? Array.Empty<Item>())
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.label) && !string.IsNullOrWhiteSpace(p.text))
            .Select(p => new PromptPreset(p.label.Trim(), p.text.Trim()))
            .ToArray();
        if (items.Length == 0) items = Defaults;   // empty / all-garbage list -> defaults, never an empty menu
        var go = string.IsNullOrWhiteSpace(model.go) ? DefaultGo : model.go.Trim();
        var rec = string.IsNullOrWhiteSpace(model.rec) ? DefaultRec : model.rec.Trim();
        var modelOn  = string.IsNullOrWhiteSpace(model.model_on)  ? DefaultModelOn  : model.model_on.Trim();
        var modelOff = string.IsNullOrWhiteSpace(model.model_off) ? DefaultModelOff : model.model_off.Trim();
        var heavy = string.IsNullOrWhiteSpace(model.heavy_match) ? DefaultHeavyMatch : model.heavy_match.Trim();

        void apply()
        {
            Go = go;
            Rec = rec;
            ModelOn = modelOn;
            ModelOff = modelOff;
            bool heavyMoved = !string.Equals(HeavyMatch, heavy, StringComparison.Ordinal);
            HeavyMatch = heavy;
            All.Clear();
            foreach (var it in items) All.Add(it);
            if (heavyMoved) HeavyMatchChanged?.Invoke();   // apply runs on the UI thread (the dispatch below)
        }
        // The watcher fires off a threadpool thread; the ObservableCollection feeds bound WPF menus, so the
        // mutation MUST run on the UI thread.
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.Invoke(apply); else apply();
    }

    private static void StartWatch()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;   // folder absent — the built-ins stand
            _watch = new FileSystemWatcher(dir, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            void onChange(object _, FileSystemEventArgs __) { try { Reload(); } catch { } }
            _watch.Changed += onChange;
            _watch.Created += onChange;
            _watch.Renamed += (_, __) => { try { Reload(); } catch { } };
        }
        catch { /* the watch is a convenience; the startup read already applied the file */ }
    }
}
