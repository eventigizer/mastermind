using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using BrushConverter = System.Windows.Media.BrushConverter;
using Color = System.Windows.Media.Color;

namespace Mastermind;

/// <summary>One item of an agent's TodoWrite list, surfaced on the card (#3). Status is the raw transcript
/// value (<c>pending</c> / <c>in_progress</c> / <c>completed</c>); the glyph + colour are derived for XAML.</summary>
public sealed class TodoItem
{
    public string Content { get; init; } = "";
    public string Status { get; init; } = "pending";
    public string Glyph => Status == "completed" ? "✓" : Status == "in_progress" ? "▸" : "○";
    public Brush GlyphBrush => Status == "completed" ? Done : Status == "in_progress" ? Active : Pending;

    private static SolidColorBrush F(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
    private static readonly Brush Done    = F("#5FB87A");   // finished — the FINISHED green
    private static readonly Brush Active  = F("#F4F4F6");   // in progress — bright neutral; blue means your turn
    private static readonly Brush Pending = F("#8893A6");
}

/// <summary>The display state of a session card. Each value maps to a label + colours + sort rank via the
/// one style table on <see cref="AgentEvent"/> — no more comparing magic status strings with <c>==</c>.
/// <c>AwaitingTask</c> is the WAITING pill (a turn that ended with a background task still running) — named apart
/// from <c>WaitingForYou</c>, which is the blue your-turn state.</summary>
public enum AgentStatus { NeedsPermission, WaitingForYou, Finished, AwaitingTask, Working, Open }

/// <summary>The hook event that drove a card, typed instead of a raw string compared all over the app.
/// <c>Baseline</c> is the synthetic "currently open" row; <c>Other</c> is any unrecognized event.
/// <c>StopWaiting</c> is not a Claude Code hook: <see cref="AgentEvent.FromHook"/> casts a Stop whose payload lists a
/// background task or a session cron under that name, so StatusCache persists the distinction a rescan restores.
/// <c>PostModelSwitch</c> moves the card's model and nothing else.</summary>
public enum RawKind { PermissionRequest, Notification, Stop, StopWaiting, SessionStart, SessionEnd, UserPromptSubmit, PreToolUse, PostToolUse, PostModelSwitch, Baseline, Other }

/// <summary>
/// One row in the list: a single hook event from a Claude Code session.
/// Carries both the raw routing data (SessionId, Cwd) and the pre-computed
/// display fields the XAML binds to.
/// </summary>
public sealed class AgentEvent : INotifyPropertyChanged
{
    public string SessionId { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string RawEvent { get; init; } = "";              // raw hook event name; persisted by StatusCache
    public RawKind Kind { get; init; } = RawKind.Other;      // typed event kind, for control flow
    public AgentStatus Status { get; init; } = AgentStatus.Open;  // display state -> label/colour/rank via Styles

    // Last-activity time. SETTABLE (not init): a working card is "touched" on every tool ping (Pre/PostToolUse)
    // so the stall detector measures silence-since-last-tool, not age-since-card-created. The setter refreshes
    // the recency text + recomputes the stall marker.
    private DateTime _when = DateTime.Now;
    public DateTime When
    {
        get => _when;
        set { _when = value; PropertyChanged?.Invoke(this, new(nameof(When))); RefreshTime(); }
    }

    /// <summary>True for the "currently open" rows shown at startup (vs a live triggered event).</summary>
    public bool IsBaseline { get; init; }
    /// <summary>A window-with-no-live-agent row (the "open in VS Code" placeholder). It TOP-aligns like every
    /// other card: the monogram-chip row is the grid's anchor line across the whole list, and an earlier
    /// centre-the-sparse-content treatment left the chip floating mid-card, which read as broken.</summary>
    public bool IsPlaceholder => string.IsNullOrEmpty(SessionId);

    public string ProjectName { get; init; } = "";

    // Session name (ai-title); the card's main line. NOTIFYING: a live event's card is shown immediately
    // with this empty (DisplayTitle falls back to the project name), then a background meta read fills it
    // in — so the setter must raise PropertyChanged for itself + the props derived from it.
    private string _title = "";
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            PropertyChanged?.Invoke(this, new(nameof(Title)));
            PropertyChanged?.Invoke(this, new(nameof(DisplayTitle)));
            PropertyChanged?.Invoke(this, new(nameof(TitleDisplay)));
            PropertyChanged?.Invoke(this, new(nameof(TitleBrush)));
            // The card-action visibilities are deliberately NOT raised here, and re-adding them is a no-op that
            // reads like a fix. Send / Reply / PermissionAnswer / ModelToggle all key on
            // SessionId + Status — both `init`, so neither can change after construction — plus _autoAllow,
            // which the AutoAllow setter raises itself. Not one of them reads Title. They date from when the
            // CDP actions matched sessions BY TITLE; identity moved to the session id (CdpJs) and these
            // notifications were left behind announcing a change that cannot happen.
        }
    }

    // CDP card actions address a session by ID (CdpJs), so a session id is the only requirement — a card whose
    // ai-title hasn't resolved yet is still actionable; only placeholder rows (no session) hide. #5 allow/deny
    // shows only while NEEDS PERMISSION; the quick replies (`rec` / `go`) only while the agent has handed you the
    // turn (FINISHED / WAITING FOR YOU) — never mid-turn, never on a WAITING card whose background task will wake it.
    // Allow/deny hide when auto-allow is ON — the toggle already answers the prompt, so the manual buttons
    // would be dead (and contradictory). _autoAllow change raises this (see the AutoAllow setter).
    public System.Windows.Visibility PermissionAnswerVisibility =>
        Status == AgentStatus.NeedsPermission && !string.IsNullOrEmpty(SessionId) && !_autoAllow
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility ReplyVisibility =>
        (Status is AgentStatus.Finished or AgentStatus.WaitingForYou) && !string.IsNullOrEmpty(SessionId)
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    // Archive (archive + stop the process) only on an IDLE card: an archived session's hook events are dropped, so an
    // archived agent that is still working — or asking, or waiting on a background task — would run on unseen.
    public System.Windows.Visibility ArchiveVisibility =>
        (Status is AgentStatus.Finished or AgentStatus.WaitingForYou or AgentStatus.Open) && !string.IsNullOrEmpty(SessionId)
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    // The detail line: the live event (agent output on finish / the live tool action while working), or "" for
    // idle rows. NOTIFYING: SessionStore.TouchWorking updates it in place on an already-WORKING card as new
    // PreToolUse actions arrive (#2 live action line), so the binding must react.
    private string _detail = "";
    public string Detail
    {
        get => _detail;
        set
        {
            if (_detail == value) return;
            _detail = value;
            PropertyChanged?.Invoke(this, new(nameof(Detail)));
            PropertyChanged?.Invoke(this, new(nameof(DetailVisibility)));
            PropertyChanged?.Invoke(this, new(nameof(DetailIsTool)));
        }
    }

    /// <summary>The held Stop's note (<see cref="SessionStore.HoldStop"/>) — the one prose line a WORKING card's detail
    /// carries.</summary>
    public const string FinishingNote = "finishing — its Stop hooks still run";

    // A WORKING card's detail is the agent's tool line — your prompt rides the you block — so the XAML sets it in the
    // mono face; the held Stop's note, and every other status's detail, stay prose.
    public bool DetailIsTool => Status == AgentStatus.Working && Detail != FinishingNote;

    // YOUR side of the card: the last prompt you sent this session — typed by hand, picked from the panel or a slash
    // command — in its own block above the detail, which keeps only the agent's side (the reply, the live action). Off
    // the UserPromptSubmit hook live, off the transcript after a restart; SessionStore keeps it per session.
    // NOTIFYING: a prompt queued while the card is already WORKING updates it in place (TouchWorking).
    private string _youText = "";
    public string YouText
    {
        get => _youText;
        set
        {
            value ??= "";
            if (_youText == value) return;
            _youText = value;
            PropertyChanged?.Invoke(this, new(nameof(YouText)));
            PropertyChanged?.Invoke(this, new(nameof(YouVisibility)));
        }
    }
    public System.Windows.Visibility YouVisibility => string.IsNullOrWhiteSpace(_youText)
        ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    // ---- TODO list (#3) — the agent's latest TodoWrite, read off the transcript tail. The card shows a
    // compact "<done>/<total> · <current item>" summary, expandable to the full checklist.
    private IReadOnlyList<TodoItem> _todos = Array.Empty<TodoItem>();
    public IReadOnlyList<TodoItem> Todos
    {
        get => _todos;
        set
        {
            _todos = value ?? Array.Empty<TodoItem>();
            PropertyChanged?.Invoke(this, new(nameof(Todos)));
            PropertyChanged?.Invoke(this, new(nameof(TodoSummary)));
            PropertyChanged?.Invoke(this, new(nameof(TodoVisibility)));
        }
    }
    public System.Windows.Visibility TodoVisibility => _todos.Count > 0
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string TodoSummary
    {
        get
        {
            if (_todos.Count == 0) return "";
            int done = _todos.Count(t => t.Status == "completed");
            var cur = _todos.FirstOrDefault(t => t.Status == "in_progress")
                      ?? _todos.FirstOrDefault(t => t.Status != "completed");
            return cur != null && cur.Content.Length > 0 ? $"{done}/{_todos.Count} · {cur.Content}"
                                                         : $"{done}/{_todos.Count}";
        }
    }

    // ---- STALL marker (#1) — "go and look", never "stuck". A WORKING card with no tool starting or finishing for
    // three minutes: a single long foreground command (one PreToolUse at its start, one PostToolUse at its end) and
    // long thinking or writing trip it as readily as a hang does. A WAITING card past half an hour: its background
    // task or wake-up may have died with nothing else changing. A LOUD per-card marker, deliberately NOT folded into
    // the "who needs you" count (a stall is a maybe, and that count must stay high-trust) — no alert, no panel pop.
    // Recomputed every tick by RefreshTime (and on each When touch).
    private const double StallThresholdSeconds = 180;
    private const double WaitStallThresholdSeconds = 1800;
    public bool Stalled => Status switch
    {
        AgentStatus.Working      => (DateTime.Now - _when).TotalSeconds > StallThresholdSeconds,
        AgentStatus.AwaitingTask => (DateTime.Now - _when).TotalSeconds > WaitStallThresholdSeconds,
        _                        => false,
    };
    public System.Windows.Visibility StallVisibility => Stalled
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string StallTip => Status == AgentStatus.AwaitingTask
        ? "Waiting on a background task or a wake-up for over half an hour — check it is still running"
        : "No tool started or finished for a while — stuck, or deep in a long command or long thinking";

    // ---- DUPLICATE-NAME marker — two+ live cards share this exact title. The pill warns the USER, not the app:
    // CDP actions address sessions by id and can no longer hit the wrong one, but a human reading two
    // identically-named cards can still type a prompt meant for one into the other. It says "tell these apart by
    // the project chip — or rename one" (the session id is never displayed). Set by SessionStore.RecomputeDuplicates.
    private bool _duplicateName;
    public bool DuplicateName
    {
        get => _duplicateName;
        set
        {
            if (_duplicateName == value) return;
            _duplicateName = value;
            PropertyChanged?.Invoke(this, new(nameof(DuplicateName)));
            PropertyChanged?.Invoke(this, new(nameof(DuplicateVisibility)));        }
    }
    public System.Windows.Visibility DuplicateVisibility => _duplicateName
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    // The title line ALWAYS shows — a card with no resolved title gets a muted default instead of a blank
    // slot: "open in VS Code" for a placeholder (no agent — the note that used to sit in the detail line),
    // "new session" for a live agent whose ai-title hasn't resolved yet. NOT "waiting…" — that word is a status
    // (WAITING FOR YOU, WAITING), and a default title must never read as a status claim. TitleBrush mutes the
    // fallbacks so a real title reads brighter than a stand-in. A card that does not need you (busy, waiting on a
    // background task, open) dims its title and detail one step, so the eye lands on the ones that do — no hue, no
    // border; Status is init-only, so neither brush needs to notify.
    public string TitleDisplay => !string.IsNullOrWhiteSpace(Title) ? Title
        : IsPlaceholder ? "open in VS Code" : "new session";
    public Brush TitleBrush => string.IsNullOrWhiteSpace(Title)
        ? (NeedsYou ? CTitleMuted : CTextLo)
        : (NeedsYou ? CTitleHi : CTitleMuted);
    public Brush DetailBrush => NeedsYou ? CTitleMuted : CTextLo;
    public System.Windows.Visibility DetailVisibility => string.IsNullOrWhiteSpace(Detail)
        ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    // AUTO-ALLOW — a per-session "stop asking me" toggle. When ON, this session's PermissionRequest hook
    // auto-answers allow (via autoallow.json) instead of prompting. The SETTER persists the change to
    // AutoAllowStore — so the card's CheckBox drives the policy; the factory methods seed the backing field
    // directly from the store (no redundant write-back). Only shown on real session cards (a placeholder
    // row has no session to govern).
    private bool _autoAllow;
    public bool AutoAllow
    {
        get => _autoAllow;
        set
        {
            if (_autoAllow == value) return;
            ReflectAutoAllow(value);
            AutoAllowStore.Set(SessionId, value);
        }
    }

    /// <summary>Show an auto-allow state without persisting it — the setter's half that touches no file
    /// (<see cref="SessionStore.SyncAutoAllow"/>).</summary>
    public void ReflectAutoAllow(bool on)
    {
        if (_autoAllow == on) return;
        _autoAllow = on;
        PropertyChanged?.Invoke(this, new(nameof(AutoAllow)));
        PropertyChanged?.Invoke(this, new(nameof(PermissionAnswerVisibility)));   // ON hides allow/deny
    }

    public System.Windows.Visibility AutoAllowVisibility => string.IsNullOrEmpty(SessionId)
        ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    // ---- MODEL — which model this session is currently on: its last PostModelSwitch hook's to_model once one has
    // arrived (SessionStore.SetHookModel), else the transcript (Sessions.ReadMeta) — a pending `/model` switch's own
    // stdout, else the last assistant turn's message.model. The HEAVY model's
    // indicator is the model toggle ITSELF, a text toggle labelled with heavy_match: solid red fill + white text
    // while heavy (red is the cost hue), dim text when default — the toggle-button idiom. State lives on the one
    // control that changes it; the earlier red card BORDER + "FABLE" pill pair is retired — three surfaces
    // for one bit was the indicator overload that made none of them read.
    // Heavy = the model name, any `[…]` suffix stripped, contains prompts.json's heavy_match (default "fable"),
    // case-insensitively — so a version bump needs no code change, and both spellings the two producers yield
    // ("Fable 5" / "claude-fable-5[1m]") match.
    private string _model = "";
    public string Model
    {
        get => _model;
        set
        {
            value ??= "";
            if (_model == value) return;
            _model = value;
            PropertyChanged?.Invoke(this, new(nameof(Model)));
            RefreshHeavy();
        }
    }
    public bool IsHeavy
    {
        get
        {
            int cut = _model.IndexOf('[');
            return (cut >= 0 ? _model[..cut] : _model).Contains(PromptPresets.HeavyMatch, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Re-read the heavy test and the model toggle drawn from it — on a model change (the setter) and when
    /// prompts.json moves heavy_match (<see cref="PromptPresets.HeavyMatchChanged"/>), which is also its label.</summary>
    public void RefreshHeavy()
    {
        PropertyChanged?.Invoke(this, new(nameof(IsHeavy)));
        PropertyChanged?.Invoke(this, new(nameof(ModelToggleBg)));
        PropertyChanged?.Invoke(this, new(nameof(ModelGlyphBrush)));
        PropertyChanged?.Invoke(this, new(nameof(ModelToggleTip)));
        PropertyChanged?.Invoke(this, new(nameof(ModelToggleLabel)));
    }
    // The toggle addresses the session over CDP like every other card action, so a session id is all it needs.
    public System.Windows.Visibility ModelToggleVisibility => string.IsNullOrEmpty(SessionId)
        ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public string ModelToggleLabel => PromptPresets.HeavyMatch.ToLowerInvariant();
    public Brush ModelToggleBg  => IsHeavy ? CFable : System.Windows.Media.Brushes.Transparent;
    public Brush ModelGlyphBrush => IsHeavy ? System.Windows.Media.Brushes.White : CTextLo;   // off = the `auto` toggle's dim text
    public string ModelToggleTip => IsHeavy
        ? $"Running {_model} — click to put this session back on the default model"
        : "Switch this session to the heavy model for a tough problem";

    // SEND A PROMPT — the card's canned-prompt menu injects text into this session's composer over CDP.
    // Needs only a session id (the CDP path addresses the sidebar by id, exactly like the navigate switch),
    // so it hides only on placeholder rows that have no session to address.
    public System.Windows.Visibility SendVisibility =>
        !string.IsNullOrEmpty(SessionId)
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    // Context-window fill — tokens in the last assistant turn's prompt (input + cache read + cache
    // creation). Window assumed 1M; the bar greens→ambers→reds as it fills.
    // NOTIFYING for the same reason as Title — the last assistant turn's prompt size is resolved off the
    // dispatcher and filled in after the card is already on screen.
    private long _contextTokens;
    public long ContextTokens
    {
        get => _contextTokens;
        set
        {
            if (_contextTokens == value) return;
            _contextTokens = value;
            PropertyChanged?.Invoke(this, new(nameof(ContextTokens)));
            PropertyChanged?.Invoke(this, new(nameof(ContextText)));
            PropertyChanged?.Invoke(this, new(nameof(ContextVisibility)));
            PropertyChanged?.Invoke(this, new(nameof(ContextBrush)));
        }
    }
    // Compacted (context reset) — the transcript's last usage record describes the PRE-compact conversation,
    // so showing it would contradict the IDE's own gauge (deep red over a freshly emptied context). The flag
    // OVERRIDES the token display with a muted "compacted" until the next turn's usage record clears it.
    // Deliberately not swallowed by the ContextTokens>0 appliers' guard — same reasoning as the model
    // reconcile: a reset state must be able to clear a stale number.
    private bool _contextCompacted;
    public bool ContextCompacted
    {
        get => _contextCompacted;
        set
        {
            if (_contextCompacted == value) return;
            _contextCompacted = value;
            PropertyChanged?.Invoke(this, new(nameof(ContextCompacted)));
            PropertyChanged?.Invoke(this, new(nameof(ContextText)));
            PropertyChanged?.Invoke(this, new(nameof(ContextVisibility)));
            PropertyChanged?.Invoke(this, new(nameof(ContextBrush)));
            PropertyChanged?.Invoke(this, new(nameof(ContextFraction)));
        }
    }
    private const double CtxWindow = 1_000_000;
    public double ContextFraction => _contextCompacted || ContextTokens <= 0 ? 0 : Math.Min(1.0, ContextTokens / CtxWindow);
    public string ContextText => _contextCompacted ? "compacted"
        : ContextTokens <= 0 ? ""
        : ContextTokens >= 1_000_000 ? $"{ContextTokens / 1_000_000.0:0.0}M"
        : ContextTokens >= 1_000     ? $"{ContextTokens / 1000}k"
        : ContextTokens.ToString();
    public System.Windows.Visibility ContextVisibility => _contextCompacted || ContextTokens > 0
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public Brush ContextBrush => CtxColor(ContextFraction);

    // Human-readable label for logs and the title-fallback compare: title, else project name.
    // The VISIBLE big title binds Title + TitleVisibility, so it just hides when there's no title yet —
    // the project name (always shown in its muted slot below) then identifies the card with no duplication.
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? ProjectName : Title;
    // Project name shows on EVERY card now (not only title-ful ones), so you can always tell which project
    // a card belongs to — even placeholder "open in VS Code" rows and live cards whose ai-title isn't read yet.
    public System.Windows.Visibility ProjectVisibility => System.Windows.Visibility.Visible;

    // The "who needs you" count (header badge, tray tooltip, trim protection): every state where the agent waits on
    // YOU — a dialog, an idle-wait, or a finished turn. FINISHED stays green so "just finished" still reads apart
    // from "has sat a while"; WORKING / WAITING (a background task will wake it) / OPEN never count, nor STALLED?.
    public bool NeedsYou => Status is AgentStatus.NeedsPermission or AgentStatus.WaitingForYou or AgentStatus.Finished;

    /// <summary>Copy a transcript read onto the live card through the notifying setters — the ONE applier the event
    /// resolve and the 15s working sweep share. Title / tokens / model are null-guarded so an empty read never blanks a
    /// resolved card; <paramref name="keepModel"/> leaves the model alone — set once a PostModelSwitch hook named it,
    /// since the transcript runs a turn behind after an automatic or resume switch. Returns true when this read is the
    /// first sighting of a compaction marker on the card (the caller logs it — the parser shipped unprobed).</summary>
    internal bool ApplyMeta(SessionMeta m, bool keepModel = false)
    {
        bool newlyCompacted = m.Compacted && !ContextCompacted;
        if (!string.IsNullOrEmpty(m.Title)) Title = m.Title;
        ContextCompacted = m.Compacted;
        if (m.ContextTokens > 0) ContextTokens = m.ContextTokens;
        Todos = m.Todos;
        if (!keepModel && !string.IsNullOrEmpty(m.Model)) Model = m.Model;
        return newlyCompacted;
    }

    private static Brush CtxColor(double f)
    {
        f = Math.Clamp(f, 0, 1);
        // 3-stop escalation on the fixed 1M window: flat GREY until 45% (nothing to watch), then grey -> red
        // (45-60%), red -> deep "crazy" red (60-100%). Tuned so full RED lands at ~60% — the top of the "close the
        // session" window — and crazy-red past it. Red is the panel's COST hue (with the heavy-model toggle); no
        // orange stop, which would read as the amber of NEEDS PERMISSION.
        static double L(double x, double y, double t) => x + (y - x) * t;
        // grey #8C8C92 -> red #E5484D -> crazy #FF1A1A
        double r, g, b;
        if (f < 0.45)      { r = 140; g = 140; b = 146; }                                                               // flat grey
        else if (f < 0.60) { double t = (f - 0.45) / (0.60 - 0.45); r = L(140, 229, t); g = L(140, 72, t); b = L(146, 77, t); } // grey -> red
        else               { double t = (f - 0.60) / 0.40; r = L(229, 255, t); g = L(72, 26, t);   b = L(77, 26, t); }  // red  -> crazy red
        var br = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b));
        br.Freeze();
        return br;
    }

    // Label + colours, all derived from Status through the one style table: pill label, pill text colour, pill bg tint.
    // Status is fixed at construction (clicking a card no longer mutates it), so these don't need to notify.
    public string StatusText => Styles[Status].Label;
    public Brush Accent => Styles[Status].Accent;
    public Brush AccentTint => Styles[Status].Tint;


    private string _timeText = "just now";
    public string TimeText
    {
        get => _timeText;
        set { _timeText = value; PropertyChanged?.Invoke(this, new(nameof(TimeText))); }
    }

    public void RefreshTime()
    {
        var d = DateTime.Now - When;
        TimeText = d.TotalSeconds < 8 ? "just now"
                 : d.TotalSeconds < 60 ? $"{(int)d.TotalSeconds}s ago"
                 : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m ago"
                 : d.TotalHours   < 24 ? $"{(int)d.TotalHours}h ago"
                 : When.ToString("MMM d");
        // The stall marker is time-derived — recompute it on the same 15s tick that refreshes the recency text.
        PropertyChanged?.Invoke(this, new(nameof(Stalled)));
        PropertyChanged?.Invoke(this, new(nameof(StallVisibility)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- colours -----------------------------------------------------------
    private static SolidColorBrush Solid(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
    private static readonly Brush CTitleHi    = Solid("#F4F4F6");   // real title — matches the XAML TextHi
    private static readonly Brush CTitleMuted = Solid("#A8A8B4");   // default-title stand-in — matches TextMid
    private static readonly Brush CTextLo     = Solid("#868691");   // a dimmed card's text — matches TextLo
    private static readonly Brush CAmber  = Solid("#E0A458");
    private static readonly Brush CGreen  = Solid("#5FB87A");
    private static readonly Brush CBlue   = Solid("#6EA8FE");
    private static readonly Brush CSlate  = Solid("#8893A6");
    private static readonly Brush CWork   = Solid("#6E7E8F");   // muted steel-GREY: agent busy, recede — pulled off the waiting-blue so "your turn" and "just busy" never read alike
    private static readonly Brush CFable  = Solid("#FF3B30");   // HOT red for the heavy model — louder than the muted #E0524D error red on purpose; it fills the model TOGGLE (its only surface), never the card or the status accent
    private static readonly Brush TAmber  = Solid("#26E0A458");
    private static readonly Brush TGreen  = Solid("#265FB87A");
    private static readonly Brush TBlue   = Solid("#266EA8FE");
    private static readonly Brush TSlate  = Solid("#208893A6");
    private static readonly Brush TWork   = Solid("#206E7E8F");

    // ---- per-project MONOGRAM chip -------------------------------------------------------------------
    // Project identity is ONE element: an inverted chip — the bright neutral Accent fill, dark initials — leading the
    // card's secondary row. Identity rides the TEXT and brightness, never a hue: every hue on the panel names a status
    // (green finished, blue your turn, amber permission, red cost, grey busy), and a hued chip beside a same-hued
    // status pill read as one signal. The project-key idiom of status desks (Jira and Linear keys, Grafana panel
    // titles), where colour is kept for state. Everything else carries no project marking either: the card
    // background is flat Surface, the border the neutral hairline, the project-name text TextMid.

    /// <summary>The chip's initials — the project's identity: its config label, else its routing token (the one the
    /// handoff filenames use: FEtoBE__…), else initials off the folder name, so an unknown checkout still reads. The
    /// list is read once at startup — restart Mastermind after the manifest changes.</summary>
    public string ProjectMonogram
    {
        get
        {
            if (string.IsNullOrEmpty(ProjectName) || ProjectName == "unknown project") return "?";
            var entry = ProjectList.ForLeaf(ProjectName);
            if (!string.IsNullOrEmpty(entry?.Label)) return entry.Label;
            if (!string.IsNullOrEmpty(entry?.Token)) return entry.Token;
            var parts = ProjectName.Split('-', '_', ' ', '.');
            var sb = new System.Text.StringBuilder();
            foreach (var p in parts)
                if (p.Length > 0 && sb.Length < 3) sb.Append(char.ToUpperInvariant(p[0]));
            return sb.Length > 0 ? sb.ToString() : "?";
        }
    }

    // THE table: status -> (pill label, accent colour, faint pill-bg tint). Adding a status means adding one row
    // here; StatusText/Accent/AccentTint all read from it. Declared after the brush fields so their static
    // initializers have run by the time this one references them.
    private readonly record struct StatusStyle(string Label, Brush Accent, Brush Tint);
    private static readonly Dictionary<AgentStatus, StatusStyle> Styles = new()
    {
        [AgentStatus.NeedsPermission] = new("NEEDS PERMISSION", CAmber,  TAmber),
        [AgentStatus.WaitingForYou]   = new("WAITING FOR YOU",  CBlue,   TBlue),
        [AgentStatus.Finished]        = new("FINISHED",         CGreen,  TGreen),
        [AgentStatus.AwaitingTask]    = new("WAITING",          CWork,   TWork),   // WORKING's colours: nothing for you yet
        [AgentStatus.Working]         = new("WORKING",          CWork,   TWork),
        [AgentStatus.Open]            = new("OPEN",             CSlate,  TSlate),
    };

    /// <summary>
    /// Build an event from Claude Code's raw hook payload, its project identity <paramref name="cwd"/> resolved by the
    /// caller off the live-session registry. No transcript read — Title / ContextTokens stay empty and the caller fills
    /// them off-thread via <see cref="Sessions.MetaForSession"/>; the auto-allow toggle comes off AutoAllowStore's cached
    /// map. Null for an event no card state answers to: a Notification type outside the attention set.
    /// </summary>
    internal static AgentEvent FromHook(HookPayload p, string cwd)
    {
        var name = p.Event;
        var kind = KindOf(name);
        var autoAllow = AutoAllowStore.IsOn(p.SessionId);
        if (kind == RawKind.PermissionRequest && autoAllow)
        {
            // Auto-allowed: answered with no dialog by this exe, so the agent is already proceeding: a WORKING ping like PreToolUse, never an amber attention
            // event, and the panel never pops for a prompt you told it to stop asking about.
            kind = RawKind.PreToolUse;
            name = "PreToolUse";
        }
        string tool = "", message = "", model = "", you = "";
        switch (kind)
        {
            case RawKind.PermissionRequest:
                tool = HookText.PermissionAction(p.ToolName, p.ToolInput, cwd);
                break;
            case RawKind.PreToolUse:
                tool = HookText.PreToolAction(p.ToolName, p.ToolInput, cwd);
                break;
            case RawKind.Notification:
                if (!HookText.IsAttentionNotification(p.NotificationType)) return null;
                tool = p.NotificationType;
                message = p.Message;
                break;
            case RawKind.Stop:
                message = HookText.MdToText(p.LastAssistantMessage);
                var waiting = HookText.WaitingOn(p.BackgroundTasks, p.SessionCrons, DateTime.Now);
                if (waiting.Length > 0)
                {
                    // Background work will wake the agent — the quiet WAITING card, recorded under its own name so a
                    // rescan restores WAITING rather than FINISHED.
                    kind = RawKind.StopWaiting;
                    name = "StopWaiting";
                    message = "waiting on: " + waiting + (message.Length > 0 ? "\n\n" + message : "");
                }
                break;
            case RawKind.UserPromptSubmit:
                you = HookText.CleanPrompt(p.Prompt);   // your side — the you block, never the detail line
                break;
            case RawKind.PostModelSwitch:
                model = p.ToModel;
                break;
        }

        var project = Paths.Leaf(cwd);
        var (status, detail) = Resolve(kind, tool, message);
        var e = new AgentEvent
        {
            SessionId    = p.SessionId,
            Cwd          = cwd,
            RawEvent     = name,
            Kind         = kind,
            ProjectName  = string.IsNullOrEmpty(project) ? "unknown project" : project,
            Status       = status,
            Detail       = detail,           // Title/ContextTokens stay empty -> filled off-thread by caller
            Model        = model,
            YouText      = you,              // empty but on a prompt — SessionStore.AddEvent fills the rest per session
        };
        e._autoAllow = autoAllow;   // reflect the persisted toggle (no write-back)
        e.RefreshTime();
        return e;
    }

    /// <summary>A passive "currently open" row (no trigger fired). Shown at startup for navigation.</summary>
    public static AgentEvent Baseline(string cwd, string sessionId, string summary, long ctx, DateTime when,
        IReadOnlyList<TodoItem> todos = null, string model = "", string lastPrompt = "")
    {
        var project = Paths.Leaf(cwd);
        var e = new AgentEvent
        {
            SessionId    = sessionId,
            Cwd          = cwd,
            RawEvent     = "baseline",
            Kind         = RawKind.Baseline,
            When         = when,
            IsBaseline   = true,
            ProjectName  = string.IsNullOrEmpty(project) ? "unknown project" : project,
            Status       = AgentStatus.Open,
            Title        = summary,   // ai-title is the row's name; TitleDisplay supplies the default when empty
            Detail       = "",        // the "open in VS Code" note moved INTO the title slot (TitleDisplay)
            ContextTokens = ctx,
            Todos        = todos,
            Model        = model,
            YouText      = lastPrompt,
        };
        e._autoAllow = AutoAllowStore.IsOn(sessionId);
        e.RefreshTime();
        return e;
    }

    /// <summary>
    /// A scan row whose status is restored from the session's last known situation (a remembered hook
    /// event, or a transcript that ended its turn) instead of a neutral OPEN. Otherwise like Baseline.
    /// A restored FINISHED row shows <paramref name="lastReply"/>, the transcript's concluding text, and every restored
    /// row <paramref name="lastPrompt"/> in its you block — the hook payloads that carried both died with the last run.
    /// </summary>
    public static AgentEvent Restored(string cwd, string sessionId, string title, long ctx, DateTime when, string eventName,
        IReadOnlyList<TodoItem> todos = null, string model = "", string lastReply = "", string lastPrompt = "")
    {
        var project = Paths.Leaf(cwd);
        var kind = KindOf(eventName);
        var (status, detail) = Resolve(kind, "", kind == RawKind.Stop ? lastReply : "");
        var e = new AgentEvent
        {
            SessionId    = sessionId,
            Cwd          = cwd,
            RawEvent     = "baseline",
            Kind         = RawKind.Baseline,   // a restored "currently open" row, whatever status it carries
            When         = when,
            IsBaseline   = true,
            ProjectName  = string.IsNullOrEmpty(project) ? "unknown project" : project,
            Status       = status,
            Title        = title,
            Detail       = detail,
            ContextTokens = ctx,
            Todos        = todos,
            Model        = model,
            YouText      = lastPrompt,
        };
        e._autoAllow = AutoAllowStore.IsOn(sessionId);
        e.RefreshTime();
        return e;
    }

    /// <summary>
    /// A locally-driven status change (not from a hook) — a transition Claude Code fires no hook for, read off the
    /// live-session registry's own busy / idle state instead (<see cref="SessionStore.ApplyRunStates"/>): above
    /// all a turn stopped in VS Code, which leaves the agent idle but emits no Stop. Pushed through the normal
    /// store path so it re-sorts + the prior card's resolved title/context/todos carry across via InheritResolved.
    /// </summary>
    public static AgentEvent LocalStatus(string sessionId, string cwd, AgentStatus status, string detail)
    {
        var project = Paths.Leaf(cwd);
        var e = new AgentEvent
        {
            SessionId    = sessionId,
            Cwd          = cwd,
            RawEvent     = "local",
            Kind         = RawKind.Other,
            ProjectName  = string.IsNullOrEmpty(project) ? "unknown project" : project,
            Status       = status,
            Detail       = detail,
        };
        e._autoAllow = AutoAllowStore.IsOn(sessionId);
        e.RefreshTime();
        return e;
    }

    /// <summary>Canonical project identity (full normalized path) — matches a live event to its baseline
    /// row and prunes by project. Full-path, NOT the leaf, so two same-leaf checkouts never collide.</summary>
    public static string ProjectKey(string cwd) => Paths.Key(cwd);

    /// <summary>Parse a raw hook event name into its typed kind ("baseline" is the synthetic scan row).</summary>
    private static RawKind KindOf(string ev) => ev switch
    {
        "PermissionRequest" => RawKind.PermissionRequest,
        "Notification"      => RawKind.Notification,
        "Stop"              => RawKind.Stop,
        "StopWaiting"       => RawKind.StopWaiting,
        "SessionStart"      => RawKind.SessionStart,
        "SessionEnd"        => RawKind.SessionEnd,
        "UserPromptSubmit"  => RawKind.UserPromptSubmit,
        "PreToolUse"        => RawKind.PreToolUse,
        "PostToolUse"       => RawKind.PostToolUse,
        "PostModelSwitch"   => RawKind.PostModelSwitch,
        "baseline"          => RawKind.Baseline,
        _                   => RawKind.Other,
    };

    /// <summary>
    /// Map an event kind to its display status + the detail line. ONLY the detail depends on the
    /// tool/message payload; the status → label/colour/rank mapping lives in the <c>Styles</c> table.
    /// </summary>
    private static (AgentStatus status, string detail) Resolve(RawKind kind, string tool, string message)
    {
        switch (kind)
        {
            case RawKind.PermissionRequest:
                // `tool` is HookText.PermissionAction's verb-led phrase ("run npm test", "edit src/foo.ts").
                return (AgentStatus.NeedsPermission,
                        !string.IsNullOrWhiteSpace(tool) ? "wants to " + tool
                        : !string.IsNullOrWhiteSpace(message) ? message
                        : "is asking for permission");

            case RawKind.Notification:
                // `tool` is the notification_type — FromHook drops every type outside the attention set. A restored
                // row carries none and reads as the idle wait.
                return HookText.IsPermissionNotification(tool)
                    ? (AgentStatus.NeedsPermission,
                       string.IsNullOrWhiteSpace(message) ? "is asking for permission" : message)
                    : (AgentStatus.WaitingForYou,
                       string.IsNullOrWhiteSpace(message) ? "is waiting for your input" : message);

            case RawKind.Stop:
                // `message` carries the agent's FINAL output for this turn (the payload's last_assistant_message,
                // markdown stripped) — what you read to decide what to do next. The FINISHED pill already says
                // "done". Falls back when none captured.
                return (AgentStatus.Finished,
                        !string.IsNullOrWhiteSpace(message) ? message : "finished responding — your turn");

            case RawKind.StopWaiting:
                // The payload listed a background task or a session cron: its completion or the wake-up wakes the
                // agent. `message` leads with what it waits on, then the final reply.
                return (AgentStatus.AwaitingTask,
                        !string.IsNullOrWhiteSpace(message) ? message : "waiting on a background task");

            case RawKind.SessionStart:
                // A session came online (startup / resume). List it like a baseline row; no note.
                return (AgentStatus.Open, "");

            case RawKind.UserPromptSubmit:
                // You replied → the agent is working again. Your prompt rides the you block (YouText), so the
                // detail starts empty until a tool phrase lands. Calm state — you can ignore it.
                return (AgentStatus.Working, message);

            case RawKind.PreToolUse:
                // A tool is about to RUN — the live "what it's doing now". `tool` is the present-continuous
                // phrase from HookText.PreToolAction ("running npm test", "editing x.ts"), EMPTY for reads/greps (those
                // are heartbeat-only — they advance the card's When without changing the detail line).
                return (AgentStatus.Working, tool ?? "");

            case RawKind.PostToolUse:
                // A tool just ran — the agent is busy. This is the ONLY signal that fires after you GRANT
                // a permission (Claude Code has no "permission approved" hook): the approved tool executes,
                // PostToolUse fires, and the amber NEEDS-PERMISSION card clears to WORKING. Calm — ignore.
                return (AgentStatus.Working, "");

            // SessionEnd / Baseline / Other -> neutral open row. (SessionEnd's card is removed by the
            // caller, so the status it carries here is never actually shown.)
            default:
                return (AgentStatus.Open, string.IsNullOrWhiteSpace(message) ? tool : message);
        }
    }
}
