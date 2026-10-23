using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace Mastermind;

public partial class App : System.Windows.Application
{
    private static Mutex _single;
    private EventHub _hub;
    private ClientHub _clients;
    private SessionRestore _restore;
    private WinForms.NotifyIcon _tray;
    private Icon _trayWeb;             // the web at the tray's size — the icon at a zero count
    private Icon _trayCount;           // the web with the count and VS Code health's triangle drawn on — its handle destroyed when the next replaces it
    private string _trayDrawn = "";    // what _trayCount shows: "" with neither mark, else the count's text and colour, and " health" for the triangle
    private SessionStore _store;
    private LogStore _log;
    private WindowReconciler _reconciler;
    private PromptStore _prompts;
    private MainWindow _win;
    private DispatcherTimer _rebind;
    // Reads the registry each second while a Stop is held on its session's Stop hooks (OnEvent), and stops once none is.
    private readonly DispatcherTimer _heldStopTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    // --background: the panel never shows by itself — not at startup, not on an attention event; the tray's Show still
    // opens it. A test restart runs this way, so the topmost panel never lands over the one the user works from, and
    // its outcome is read off the activity log's file.
    private bool _background;
    // --hidden: a run a settings change started while the panel was hidden to the tray keeps it hidden.
    private bool _startHidden;
    // The keys the settings change that started this run moved (--restart:<keys>) — ClientHub holds them a minute.
    private IReadOnlyList<string> _restartedFor = Array.Empty<string>();
    // The panel never shows by itself under --background (a test redeploy): it is topmost, so even a show without focus
    // covers what the person works in. The tray's Show opens it.
    private bool PanelStaysHidden => _background;
    private readonly HashSet<string> _hiddenLogged = new(StringComparer.OrdinalIgnoreCase);   // projects whose hide was logged this run
    private readonly HookSymptom _symptom = new();   // the hooks-aren't-arriving warning
    private string _driftSeen;                        // the hook check's last findings — logged only on a change
    // A tool ping this soon after the turn's Stop is that turn's last one arriving late — every event rides its own curl.
    private static readonly TimeSpan LateTool = TimeSpan.FromSeconds(3);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _background = e.Args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
        _startHidden = e.Args.Any(a => string.Equals(a, "--hidden", StringComparison.OrdinalIgnoreCase));

        DispatcherUnhandledException += (_, ex) => { Diag.Fatal("Dispatcher", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Diag.Fatal("Domain", ex.ExceptionObject);

        // single instance — except a --restart run (a settings change), which waits for the run that started it to
        // let go: finding the lock still held and quitting would leave none running
        _single = new Mutex(true, "Mastermind.SingleInstance", out bool isNew);
        var restartFor = e.Args.FirstOrDefault(a => a.StartsWith("--restart:", StringComparison.OrdinalIgnoreCase));
        if (restartFor != null)
            _restartedFor = restartFor["--restart:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!isNew && restartFor != null)
        {
            try { isNew = _single.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { isNew = true; }   // the old run exited holding it — the lock is ours
        }
        if (!isNew) { Shutdown(); return; }

        // A redeploy started from an agent's shell inherits that session's environment; a login start does not.
        DropSessionEnvironment();

        // The two platform calls that touch the UI run on this thread's context, at Send priority — what the plain
        // Dispatcher.Invoke they replace used, so a raise never queues behind the hook-event firehose.
        var ui = new DispatcherSynchronizationContext(Dispatcher, DispatcherPriority.Send);
        Platform.EditorWindows = new WinEditorWindows(ui);
        Platform.RecycleBin = new WinRecycleBin(ui);
        // Add or remove the per-user login entry to match config's startAtLogin (a removal only after a clean read). It
        // only ever writes Mastermind.lnk.
        Platform.LoginStartup.Apply(Config.Current.StartAtLogin, Config.Current.Clean);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Load the editable send-a-prompt menu from config's promptsPath and watch it so wording edits appear live
        // (no rebuild). Before the window shows.
        PromptPresets.Initialize();

        // Composition root: the store owns the card collection + merge rules; the reconciler turns
        // VS Code/hidden-set signals into store mutations behind seams; the window is just the view.
        _store = new SessionStore(ProjectList.IsHidden, ProjectList.Order);
        _log = new LogStore(System.IO.Path.Combine(Config.Current.LogDir, "mastermind_activity.log"));
        _reconciler = new WindowReconciler(new VsCodeProbe(), new SessionScanner(), _store);
        var dispatcher = new PromptDispatcher(_log);
        _prompts = new PromptStore(it => dispatcher.Transport(it), dispatcher.CleanStaging);   // auto-move → Transport (synchronous File.Move; return path discarded)
        _win = new MainWindow(_store, _reconciler, _log, _prompts, dispatcher);
        // Config's and every other early warning was held (Warnings) — replay them into the activity log now that the
        // window that toasts a Warn exists, and route each later one the same way. Queued: one can come off any thread.
        Warnings.Attach(text => Dispatcher.BeginInvoke(new Action(() => _log.Add(LogKind.Warn, text))));
        if (_background)
            _log.Add(LogKind.Info, "background · started with --background — the panel stays hidden until the tray's Show");
        // The activity log starts fresh each run, so the run a settings change started names it.
        if (restartFor != null)
            _log.Add(LogKind.Info, $"config · restarted for {string.Join(", ", _restartedFor)}");
        // What config made of the manifest — the card order, chip labels and hidden projects.
        _log.Add(LogKind.Info, ProjectList.Describe());
        // A SessionEnd sent while Mastermind was down is lost, so the auto-allow map is cleaned here instead: an entry whose
        // session no longer runs goes, and a resumed session re-prompts — unless session restore brings it back, which
        // sets it again from its own record.
        int swept = AutoAllowStore.Sweep();
        if (swept > 0) _log.Add(LogKind.Info, $"auto-allow · {swept} entries of sessions no longer running dropped");

        // Seed with the sessions currently open in VS Code (navigation list).
        try
        {
            _store.AddRange(Sessions.ScanActive());
            _reconciler.RecomputeDegraded();   // empty baseline while VS Code is open = blind, not quiet
        }
        catch { }

        _hub = new EventHub();
        // The listener answers once this returns, so the panel work is queued, not waited for, and the next request
        // taken. Order holds — one priority is FIFO — and a throw reaches DispatcherUnhandledException, which logs it:
        // BeginInvoke, never InvokeAsync, whose operation keeps a throw in its task, unlogged.
        _hub.EventReceived += payload => Dispatcher.BeginInvoke(new Action(() => OnEvent(payload)));
        _hub.Refused += line => Dispatcher.BeginInvoke(new Action(() => _log.Add(LogKind.Warn, "refused " + line)));
        // The VS Code windows' shells on /client — built here, on the UI thread its debounce timer runs on.
        _clients = new ClientHub(_store, (kind, text) => _log.Add(kind, text), () => _win.ShowPanel(force: true),
                                 ExitApp, Restart, _restartedFor, Dispatcher);
        _restore = new SessionRestore(_clients, () => _store.SyncAutoAllow(), (kind, text) => _log.Add(kind, text), Dispatcher);
        _clients.WindowConnected += _restore.Connected;       // a window that came back gets its sessions
        _clients.HealthReloaded += _restore.HealthReloaded;   // Reload panels ends them with the window still connected
        _hub.ClientRequested += _clients.Accept;
        _clients.HealthChanged += UpdateTrayText;   // a window entering or leaving its health error band
        _win.Clients = _clients;   // a card click asks the session's own window first
        dispatcher.Clients = _clients;   // a port-off dispatch types through the windows' shells
        BuildTray();   // BEFORE Start(): an event queued the moment the port binds touches _tray
        _heldStopTimer.Tick += (_, _) => ReleaseHeldStops();
        _store.Changed += UpdateTrayText;   // every store mutation, not just hook events — a registry correction moves the count too
        _store.Changed += _clients.NeedsChanged;   // the shells' badge count, debounced
        try
        {
            _hub.Start();
            _symptom.Listening(DateTime.Now);
            _win.SetStatus($"listening on 127.0.0.1:{EventHub.Port}", true);
            _log.Add(LogKind.Info, $"listening on 127.0.0.1:{EventHub.Port}");
        }
        catch (Exception ex)
        {
            // Stay up either way on the scanned baseline: hook events sent meanwhile are lost, and the registry correction
            // restores each card once the port binds. Only a BUSY port enters the retry loop — a reserved or invalid one
            // never frees itself, so a retry would hide it.
            var cause = EventHub.Classify(ex);
            string why = EventHub.Describe(cause, ex)
                         + (cause == EventHub.BindFailure.Busy ? " — retrying every 30s" : " — not retrying");
            _win.SetStatus($"port {EventHub.Port} {why}", false);
            _log.Add(LogKind.Warn, $"port {EventHub.Port} {why}");
            if (cause == EventHub.BindFailure.Busy) StartRebindLoop();
        }

        // Shell-watched projects whose entries post to another port, or carry the other permission kind, move to this run's.
        foreach (var (kind, text) in HookEntries.RewriteWatched()) _log.Add(kind, text);
        // Every project using handoffs gets its skills re-rendered from this run's address book.
        foreach (var (kind, text) in HandoffSkills.RefreshInstalled()) _log.Add(kind, text);

        _win.RestoreWindowState();          // reopen where it was left (position / size / monitor / fullscreen)
        if (!PanelStaysHidden && !_startHidden) _win.ShowPanel(bringToFront: false);
        _win.StartAutoRefresh();            // poll window lifecycle (close=prune, open=pull sessions)

        RunHealthChecks();                  // #6 hook-drift + #7 CDP-selector self-check (don't-go-blind guards)
    }

    /// <summary>
    /// Don't-go-blind guards (#6/#7): surface, in the activity log, anything that would make the panel silently miss
    /// agents — a project whose hook entries drifted or a session whose hooks never arrive (#6, every 8 s, the drift
    /// logged on a change), or a Claude-extension DOM rename that kills every CDP action (#7, an async probe at startup;
    /// later selector drift surfaces through the CDP actions' own failure lines).
    /// </summary>
    private void RunHealthChecks()
    {
        CheckHooks();
        // Never stopped; a running DispatcherTimer is held by its dispatcher, so no field keeps it.
        var health = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        health.Tick += (_, _) =>
        {
            CheckHooks();
            WarnSilentSessions();
            _clients.RefreshHealth();
            _clients.RefreshWatched();
            _restore.Track(_clients.Windows());
        };
        health.Start();

        SelectorSelfCheck.RunAsync().ContinueWith(t =>
        {
            var lines = t.IsFaulted ? new List<string> { "selector self-check failed" } : t.Result;
            Dispatcher.Invoke(() =>
            {
                if (lines.Count == 0) _log.Add(LogKind.Info, "selector check: all CDP anchors resolve");
                else foreach (var l in lines)
                    _log.Add(l.StartsWith("CDP selector MISSING") ? LogKind.Warn : LogKind.Info, l);
            });
        });
    }

    /// <summary>The hook check (#6), logged when its findings change: a wiped entry is flagged within a tick, and a
    /// standing drift is not repeated every 8 s.</summary>
    private void CheckHooks()
    {
        List<string> drift;
        try { drift = HookDrift.Check(); }
        catch (Exception ex) { Diag.Warn("hookDrift", ex); return; }
        var seen = drift == null ? "skipped" : "checked:" + string.Join("\n", drift);
        if (seen == _driftSeen) return;
        _driftSeen = seen;
        if (drift == null) _log.Add(LogKind.Info, "hook check: skipped — no manifest (config's manifestPath) and no watched project");
        else if (drift.Count == 0) _log.Add(LogKind.Info, "hook check: all monitored projects wired");
        else foreach (var d in drift) _log.Add(LogKind.Warn, "hook drift · " + d);
    }

    /// <summary>The symptom warning (<see cref="HookSymptom"/>), once per session.</summary>
    private void WarnSilentSessions()
    {
        foreach (var project in _symptom.Silent(LiveSessions.Snapshot, IsWatched, DateTime.Now))
            _log.Add(LogKind.Warn, $"hooks aren't arriving from {project} — a policy blocking them, a wrong port or an "
                                   + "engine too old for them");
    }

    /// <summary>True when a cwd sits in a project the last hook check found fully wired — a project without Mastermind's
    /// entries is the hook check's to report, never a silent session.</summary>
    private static bool IsWatched(string cwd)
    {
        var key = Paths.Key(cwd);
        return HookDrift.Wired.Any(root => Paths.Within(key, root));
    }

    /// <summary>
    /// Started when the listener couldn't bind (busy port). Retries every 30s; on the
    /// first success it flips the status back to live and stops itself.
    /// </summary>
    private void StartRebindLoop()
    {
        _rebind = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _rebind.Tick += (_, _) =>
        {
            if (!_hub.TryRebind()) return;
            _rebind.Stop();
            _symptom.Listening(DateTime.Now);
            _win.SetStatus($"listening on 127.0.0.1:{EventHub.Port}", true);
            _log.Add(LogKind.Info, $"listening on 127.0.0.1:{EventHub.Port} (rebound)");
            UpdateTrayText();
        };
        _rebind.Start();
    }

    private void OnEvent(HookPayload p)
    {
        _symptom.Heard(p.SessionId);
        // Project identity off the live-session registry: the payload's cwd follows the agent's shell `cd`, so a cd into
        // a subfolder would relabel the card. A session newer than the last registry read falls back to the payload's,
        // still the launch folder that early.
        var cwd = LiveSessions.CwdOf(p.SessionId) ?? p.Cwd;
        var ev = AgentEvent.FromHook(p, cwd);
        if (ev == null)
        {
            _log.Add(LogKind.Drop, $"Notification {p.NotificationType} · {Paths.Leaf(cwd)} → not a card state, dropped");
            return;
        }

        // Label the log line with the session's NAME (its card title), falling back to the project name
        // when the title isn't resolved yet (the first event of a session). No hash — it's noise to a human.
        string who = _store.TitleFor(ev.SessionId);
        if (string.IsNullOrEmpty(who)) who = ev.ProjectName;
        string tag = ev.RawEvent + " · " + who;

        // A project hidden in config never shows a card and never pops the panel — dropped before any status work, and
        // quietly: its per-tool firehose would otherwise fill the log strip. SessionEnd is exempt, as it is for a
        // session hidden in the extension, so its auto-allow and status entries are still cleaned.
        if (ev.Kind != RawKind.SessionEnd && ProjectList.IsHidden(ev.Cwd))
        {
            // One line per project per run: the log shows the hide took effect, never the firehose.
            if (_hiddenLogged.Add(ev.ProjectName))
                _log.Add(LogKind.Drop, $"{tag} → project hidden in config — its events are dropped from now on, quietly");
            return;
        }

        // A session the user HID in the extension's list stays hidden here too: no card, no panel pop —
        // hidden means gone. SessionEnd is exempt so a hidden session that ends still gets cleaned out of
        // the store if it ever slipped in.
        if (ev.Kind != RawKind.SessionEnd && !string.IsNullOrEmpty(ev.SessionId)
            && VsCodeState.HiddenSessionIdsCached().Contains(ev.SessionId))
        {
            _log.Add(LogKind.Drop, tag + " → hidden, ignored");
            return;
        }

        // A model switch moves the model toggle and nothing else — no status, no card swap, no panel. The hook's model
        // outranks every later transcript read of this session (SessionStore.SetHookModel).
        if (ev.Kind == RawKind.PostModelSwitch)
        {
            if (_store.SetHookModel(ev.SessionId, ev.Model)) _log.Add(LogKind.Info, $"{tag} → model {ev.Model}");
            return;
        }

        // Your prompt outlives a restart: a long turn's output can push it past the transcript tail the scan reads.
        if (ev.Kind == RawKind.UserPromptSubmit && !string.IsNullOrEmpty(ev.YouText))
            StatusCache.RecordPrompt(ev.SessionId, ev.YouText);

        // The session's card as it stands — nothing below changes it before the last guard reads it.
        var current = _store.CurrentStatus(ev.SessionId);
        bool toolPing = ev.Kind is RawKind.PreToolUse or RawKind.PostToolUse;

        // A new prompt, a permission ask, a question or a background wait: the turn a held Stop belonged to did not end
        // with it. A tool ping keeps the hold — a Stop hook that blocks continues the turn, and its next Stop replaces it.
        if (ev.Kind is RawKind.UserPromptSubmit or RawKind.PermissionRequest or RawKind.Notification or RawKind.StopWaiting)
            _store.DropHeldStop(ev.SessionId);

        // The engine sends a permission Notification 6 s into a dialog nobody has answered yet. On a card already
        // asking it would swap the "wants to …" line for a generic one just as you come to read it — so it changes
        // nothing there (user decision). It still counts where no request line exists, a worker's permission
        // relayed to the lead.
        if (ev.Kind == RawKind.Notification && HookText.IsPermissionNotification(p.NotificationType)
            && current == AgentStatus.NeedsPermission)
        {
            _log.Add(LogKind.Drop, tag + " → already asking, its request line kept");
            return;
        }

        // A subagent's tool ping (agent_id set) arrives under the parent's session id. It is a heartbeat, never a
        // status change (user decision): a background subagent or workflow would flip a WAITING card to WORKING, and a
        // sibling's PostToolUse would clear an open dialog's NEEDS PERMISSION. A subagent's PermissionRequest still
        // asks — it reaches here as PreToolUse only when auto-allowed, already proceeding.
        if (toolPing && !string.IsNullOrEmpty(p.AgentId))
        {
            _store.TouchSubagent(ev.SessionId, ev.When, ev.Detail);
            return;
        }

        // Arrival order is not send order: every event rides its own curl. A PreToolUse is a heartbeat, never a grant —
        // only the approved tool's PostToolUse is — and the permission request comes by HTTP from inside
        // the engine, ahead of the PreToolUse curl sent before it, so a PreToolUse reaching a card that is asking changes
        // nothing. A tool ping landing within LateTool of the turn's Stop is that turn's last one arriving late. The
        // first test reads the payload's event: an auto-allowed PermissionRequest is cast as PreToolUse and proceeds.
        if (p.Event == "PreToolUse" && current == AgentStatus.NeedsPermission) return;
        if (toolPing && _store.StoppedWithin(ev.SessionId, LateTool)) return;

        // A working-ping (a reply, or a Pre/PostToolUse) for a card that's ALREADY working must NOT re-add it
        // (that swaps the card out = churn, and the per-tool firehose would spam the log). Instead TOUCH it in
        // place: advance its last-activity time (feeds the stall detector) and, for a meaningful PreToolUse,
        // update its live-action detail line. No log, no panel — it's already in the right state.
        if ((ev.Kind == RawKind.UserPromptSubmit || toolPing) && current == AgentStatus.Working)
        {
            _store.TouchWorking(ev.SessionId, ev.When, ev.Detail, ev.YouText);
            return;
        }

        // Mastermind's Stop entry is a background curl that fires as the session's Stop hooks START, beside the project's
        // own. While those still run — a build gate can take tens of seconds — the registry keeps the session busy, the
        // panel shows it working, and one can block and continue the turn. So a Stop the registry still reads busy is
        // held: the card stays WORKING, finishing, and ReleaseHeldStops delivers the Stop once the registry reads idle.
        if (ev.Kind == RawKind.Stop && LiveSessions.RunStates().TryGetValue(ev.SessionId, out var run) && run.Status == "busy")
        {
            _log.Add(LogKind.Event, tag + " → finishing, its Stop hooks still run");
            _store.HoldStop(ev);
            _heldStopTimer.Start();
            UpdateTrayText();
            return;
        }

        // Session lifecycle (pushed by hooks): keep the list in sync. Only attention events (permission / finished /
        // waiting for you) surface the panel; SessionStart/UserPromptSubmit/StopWaiting are quiet.
        switch (ev.Kind)
        {
            case RawKind.SessionEnd:
                _log.Add(LogKind.Info, tag + " → session ended, removed");
                StatusCache.Remove(ev.SessionId);
                AutoAllowStore.Remove(ev.SessionId);   // a resumed id starts over — its SessionStart or session restore sets it again
                _store.RemoveSession(ev.SessionId);
                break;

            case RawKind.SessionStart:
                // Config's newSessionAutoAllow: every session starts auto-allow ON — a fresh one, a /clear's, a resumed
                // one unless a person switched it off while session restore recorded it — never on a compaction, the
                // same session going on, where an off set mid-session stands. Never in a hidden project (Arm).
                bool armed = Config.Current.NewSessionAutoAllow && p.Source != "compact"
                    && !(p.Source == "resume" && _restore.SwitchedOff(ev.SessionId))
                    && AutoAllowStore.Arm(ev.SessionId, ev.Cwd);
                _log.Add(LogKind.Event, tag + (armed ? " → listed, auto-allow on" : " → listed"));
                _store.AddEvent(ev);                     // list it quietly — no focus steal
                if (armed) _store.SyncAutoAllow();       // the card was built off the map before the arm
                break;

            case RawKind.UserPromptSubmit:
            case RawKind.PreToolUse:
            case RawKind.PostToolUse:
                // You replied (UserPromptSubmit) OR a tool is starting/just ran (Pre/PostToolUse) → the agent is
                // working again. Remember WORKING + clear the card's amber; never pop the panel. These fire per
                // tool, so StatusCache.Record collapses the repeats to one write per transition (and an
                // already-WORKING card never reaches here — it's touched in place above).
                _log.Add(LogKind.Event, tag + " → working");
                StatusCache.Record(ev.SessionId, ev.RawEvent);
                _store.AddEvent(ev);
                break;

            case RawKind.StopWaiting:
                // The payload lists a background task or a session cron — its completion or the wake-up wakes the
                // agent, so nothing needs you yet: quiet like WORKING, never a panel pop. Recorded under its own name
                // so a rescan or relaunch restores WAITING rather than FINISHED.
                _log.Add(LogKind.Event, tag + " → waiting on background work");
                StatusCache.Record(ev.SessionId, ev.RawEvent);
                _store.AddEvent(ev);
                break;

            default:
                Surface(ev, tag);
                break;
        }

        // FromHook reads no transcript; the card is on screen now with no title. Resolve its name + context size + todos +
        // error state off the dispatcher (a transcript read can be multi-MB) and patch the live card via the
        // notifying setters. Skip SessionEnd (its card is already gone) and the per-tool Pre/PostToolUse pings
        // (re-reading the transcript every tool isn't worth it — the 15s ticker refreshes WORKING cards' meta).
        if (ev.Kind != RawKind.SessionEnd && ev.Kind != RawKind.PreToolUse && ev.Kind != RawKind.PostToolUse)
            ResolveMetaAsync(ev);

        UpdateTrayText();
    }

    /// <summary>An attention event onto its card: remembered for rescans, the panel surfaced. No tray balloon — it
    /// triggers the Windows notification sound; the panel popping up is our alert.</summary>
    private void Surface(AgentEvent ev, string tag)
    {
        _log.Add(LogKind.Event, tag + " → " + ev.StatusText.ToLowerInvariant()
                                + (PanelStaysHidden ? ", panel kept hidden (--background)" : ", panel up"));
        StatusCache.Record(ev.SessionId, ev.RawEvent);
        _store.AddEvent(ev);
        if (!PanelStaysHidden) _win.ShowPanel();
    }

    /// <summary>Each held Stop whose session the registry reads idle, or no longer lists, delivered as the Stop it is —
    /// unless the session was archived meanwhile, which OnEvent would have dropped it for. The timer stops once nothing
    /// is held. UI thread.</summary>
    private void ReleaseHeldStops()
    {
        foreach (var stop in _store.ReleaseHeldStops(LiveSessions.RunStates()))
        {
            if (VsCodeState.HiddenSessionIdsCached().Contains(stop.SessionId)) continue;
            string who = _store.TitleFor(stop.SessionId);
            Surface(stop, stop.RawEvent + " · " + (string.IsNullOrEmpty(who) ? stop.ProjectName : who));
            ResolveMetaAsync(stop);
        }
        if (!_store.HasHeldStops) _heldStopTimer.Stop();
        UpdateTrayText();
    }

    /// <summary>
    /// Fill a live card's Title + ContextTokens without ever touching disk on the UI thread: read the
    /// transcript on a threadpool thread, then marshal just the property writes back to the dispatcher.
    /// </summary>
    private void ResolveMetaAsync(AgentEvent ev)
    {
        if (string.IsNullOrEmpty(ev.SessionId)) return;
        string sid = ev.SessionId, cwd = ev.Cwd;
        Task.Run(() =>
        {
            SessionMeta meta;
            try { meta = Sessions.MetaForSession(sid, cwd); } catch { return; }
            Dispatcher.Invoke(() =>
            {
                // First live sighting of the knowledge-derived compaction marker is worth a loud line — it is
                // the observation the parser was shipped without (no compacted transcript existed to probe).
                if (_store.ApplyMeta(ev, meta))
                    _log.Add(LogKind.Warn, $"context compacted · {ev.DisplayTitle} — marker observed live");
                _store.Resort();     // a just-resolved title moves the card and may collide with another card's
            });
        });
    }

    /// <summary>The tray leads with the only number that matters: how many sessions need you — in the tooltip, and
    /// drawn on the web; then VS Code health's worst window in its error band, marked by the triangle.</summary>
    private void UpdateTrayText()
    {
        if (_tray == null) return;
        int need = _store.NeedCount;
        var health = _clients?.HealthErrors() ?? new List<string>();
        string text = need > 0 ? $"Mastermind — {need} need you" : "Mastermind — idle";
        if (health.Count > 0) text += "\n⚠ " + health[0] + (health.Count > 1 ? $" +{health.Count - 1} more" : "");
        _tray.Text = text.Length > 127 ? text[..126] + "…" : text;   // NotifyIcon.Text holds 127 characters
        UpdateTrayIcon(need, _store.Events.Any(e => e.Status == AgentStatus.NeedsPermission), health.Count > 0);
    }

    /// <summary>The count on a disc over the web's upper-left corner, the empty one — blue, amber (the NEEDS PERMISSION
    /// pill's) while a session waits on a permission; VS Code health's orange triangle over its lower-left while a window is in its error band; the plain
    /// web with neither, and "9+" past nine, since a 16 px icon has no room for two digits. Redrawn only when what it
    /// shows changes.</summary>
    private void UpdateTrayIcon(int need, bool permission, bool health)
    {
        string text = need > 9 ? "9+" : need.ToString();
        string shows = (need == 0 ? "" : text + (permission ? " amber" : " blue")) + (health ? " health" : "");
        if (shows == _trayDrawn) return;
        _trayDrawn = shows;
        var old = _trayCount;
        _trayCount = shows.Length == 0 ? null : DrawMarks(_trayWeb, need == 0 ? null : text, permission, health);
        _tray.Icon = _trayCount ?? _trayWeb;
        if (old != null)
        {
            Native.DestroyIcon(old.Handle);
            old.Dispose();
        }
    }

    private void BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => _win.ShowPanel(force: true));
        // The panel can sit on a monitor you can't see (fullscreen on the second screen) — the tray is the one
        // control always on the primary, so the rescue lives here, not on the panel.
        menu.Items.Add("Move to primary monitor", null, (_, _) => { _win.ShowPanel(force: true); _win.MoveToPrimary(); });
        // Clean hidden/stale sessions from disk. Always present — the handler itself enforces the safety gate
        // (refuses + tells you to close VS Code first). No Refresh item: that's the header ↻ in the panel.
        menu.Items.Add("Clean hidden sessions", null, (_, _) => _win.CleanHiddenSessions());
        // Kill archived-but-idle claude processes — the extension's archive hides a session but never terminates it.
        menu.Items.Add("Reap background agents", null, (_, _) => _win.ReapBackgroundAgents());
        // A fresh Claude session in a chosen window with the health-check prompt typed in, unsent — the user reads it
        // and presses Enter. The submenu is rebuilt as it opens and always ends with the prompt's edit, so it is never
        // empty and its opening event always fires.
        var healthCheck = new WinForms.ToolStripMenuItem("Claude health check — long sessions or hot machine");
        healthCheck.DropDownItems.Add(EditHealthCheckItem());
        healthCheck.DropDownOpening += (_, _) => FillHealthCheck(healthCheck);
        menu.Items.Add(healthCheck);
        // Windows re-applies efficiency mode to WhatsApp on its own and it starts failing — clear it on demand. Behind
        // config's whatsappEfficiencyFix: a tray without WhatsApp in it carries no such item.
        if (Config.Current.WhatsappEfficiencyFix)
            menu.Items.Add("Remove WhatsApp efficiency mode", null, (_, _) => ReleaseWhatsApp());
        // Open the live send-a-prompt file for editing — edits hot-reload via PromptPresets' watcher, no restart.
        menu.Items.Add("Edit prompts (prompts.json)", null, (_, _) => OpenInEditor(PromptPresets.FilePath));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitApp());

        _trayWeb = MakeIcon();
        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayWeb,
            Visible = true,
            Text = "Mastermind",
            ContextMenuStrip = menu,
        };
        // Single LEFT-click shows/raises the panel (right-click still opens the context menu).
        // No toggle — hiding is the header's hide-to-tray button.
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) _win.ShowPanel(force: true); };
    }

    /// <summary>Clear efficiency mode off WhatsApp's process tree (<see cref="Efficiency.Release"/>). The panel comes
    /// up so the outcome is readable on the status line; not running, or a process that could not be written, is a
    /// <c>Warn</c> (toast).</summary>
    private void ReleaseWhatsApp()
    {
        _win.ShowPanel(force: true);
        var r = Platform.EfficiencyMode.Release(Efficiency.WhatsAppImage);
        string text = r.Processes == 0
            ? "whatsapp efficiency · WhatsApp is not running"
            : $"whatsapp efficiency · cleared on {r.Cleared} of {r.Processes} processes"
              + (r.Failed > 0 ? $" ({r.Failed} failed)" : "");
        bool ok = r.Processes > 0 && r.Failed == 0;
        _log.Add(ok ? LogKind.Info : LogKind.Warn, text);
        _win.SetStatus(text, ok);
    }

    /// <summary>Open a file Mastermind reads for editing — with the editor's CLI — a connected window's own full path,
    /// else config's editor.cli found on PATH — which reuses the last-focused editor window and opens the file as a tab there; it is a <c>.cmd</c> file
    /// (code.cmd), which only cmd.exe runs. Else the system's default app for the file's type. prompts.json hot-reloads
    /// via <see cref="PromptPresets"/>' watcher; the health check is read at each open.</summary>
    private static void OpenInEditor(string path)
    {
        try
        {
            var named = EditorIdentities.Cli();
            var cli = System.IO.Path.IsPathRooted(named) && System.IO.File.Exists(named) ? named : FindOnPath(named);
            var psi = cli != null
                ? new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{cli}\" \"{path}\"\"")
                    { UseShellExecute = false, CreateNoWindow = true }
                : new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Diag.Warn("OpenInEditor", ex); }
    }

    /// <summary>The health check's submenu, rebuilt as it opens: each connected window with a local folder by its
    /// folders, or a greyed line when none is, then the prompt's edit. UI thread.</summary>
    private void FillHealthCheck(WinForms.ToolStripMenuItem item)
    {
        item.DropDownItems.Clear();
        var windows = _clients.Windows();
        // A WinForms label eats & as an access-key marker, and && shows one — the label only; the log keeps the name.
        foreach (var (hostPid, name, folders) in windows)
            item.DropDownItems.Add(name.Replace("&", "&&"), null, (_, _) => OpenHealthCheck(hostPid, name, folders));
        if (windows.Count == 0)
            item.DropDownItems.Add(new WinForms.ToolStripMenuItem("No VS Code window with a local folder") { Enabled = false });
        item.DropDownItems.Add(new WinForms.ToolStripSeparator());
        item.DropDownItems.Add(EditHealthCheckItem());
    }

    private static WinForms.ToolStripMenuItem EditHealthCheckItem() =>
        new("Edit this prompt…", null, (_, _) => OpenInEditor(PromptPresets.EnsureHealthCheckFile()));

    /// <summary>A fresh sidebar session in that window with the health check typed in, unsent, then the window raised:
    /// with the debug port on, exactly — the sidebar within its folders that moved to the
    /// new session; else, or when that raise missed, by its first folder's title, as a card click falls back. The menu
    /// click holds the foreground, so the grant comes first. A menu handler, so every failure is caught here, and each
    /// brings the panel up, logs its Warn line and opens the dialog — the toast lives in the panel, and the crash
    /// log alone goes unseen. A success leaves the panel where it was: it is topmost, and would cover the window just
    /// raised.</summary>
    private async void OpenHealthCheck(int hostPid, string name, List<string> folders)
    {
        void Fail(string text, string why, string next)
        {
            _win.ShowPanel(force: true);
            _log.Add(LogKind.Warn, text);
            MastermindDialog.Tell(_win, text, why, next);
        }
        try
        {
            Platform.EditorWindows.GrantForeground();
            var prompt = PromptPresets.HealthCheckText();
            var before = await CdpSwitcher.SidebarSessions();
            var open = _clients.OpenNew(hostPid, prompt);
            var error = open == null ? "it is no longer connected" : await open;
            if (error != null)
            {
                Fail($"health check · {name}: {error}", "The window did not open the session.", "Check the window is still open, then pick it again.");
                return;
            }
            _log.Add(LogKind.Info, $"health check · sent to {name}");   // the window ran it; the sidebar loads after
            if (await CdpSwitcher.RaiseMovedSidebar(before, folders)) return;
            var raise = Platform.EditorWindows.RaiseProject(folders[0]);
            // global:: — inside App, a bare MainWindow is Application.MainWindow, the inherited Window property.
            if (raise != RaiseResult.Raised) Fail(global::Mastermind.MainWindow.RaiseNote(raise, name), "The session opened, but Windows kept its window in the background.", "Switch to that window yourself — the health check is typed in there, unsent.");
        }
        catch (Exception ex)
        {
            Fail($"health check · {name} failed ({ex.Message})", ex.Message, "Try again; Mastermind's crash log has the details.");
            Diag.Warn("HealthCheck", ex);
        }
    }

    /// <summary>The first <c>&lt;name&gt;.cmd</c>, <c>.exe</c> or <c>.bat</c> on PATH — never the extensionless shell
    /// script VS Code's bin folder also carries, which cmd cannot run — or null. Searched in-process: no child process
    /// that can hang the UI thread, and no console code page to garble a non-ASCII install path.</summary>
    private static string FindOnPath(string name)
    {
        var names = System.IO.Path.HasExtension(name) ? new[] { name } : new[] { name + ".cmd", name + ".exe", name + ".bat" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var n in names)
            {
                try
                {
                    var candidate = System.IO.Path.Combine(dir.Trim('"'), n);
                    if (System.IO.File.Exists(candidate)) return candidate;
                }
                catch { /* a malformed PATH entry — skip it */ }
            }
        return null;
    }

    /// <summary>
    /// Drop what a redeploy from an agent's shell hands the exe and a login start never does — VS Code's runtime
    /// variables (ELECTRON_RUN_AS_NODE, VSCODE_*) and the launching Claude session's (CLAUDECODE, CLAUDE_*, its
    /// messaging token among them) — so every child the exe starts (the editor's CLI, cmd, netsh) sees one environment
    /// however the exe was started; under ELECTRON_RUN_AS_NODE an editor executable starts as bare Node and opens
    /// nothing. A variable the user set for good — in their user or machine environment — stays.
    /// </summary>
    private static void DropSessionEnvironment()
    {
        foreach (string name in Environment.GetEnvironmentVariables().Keys)   // a snapshot — safe to change the live set
        {
            bool session = string.Equals(name, "ELECTRON_RUN_AS_NODE", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase);
            if (session
                && Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) == null
                && Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) == null)
                Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>The tray icon — Mastermind's cobweb (mastermind.ico, embedded; make_mastermind_icon.py draws it), at the
    /// size Windows gives a small icon at this DPI. Every size in the file is drawn on its own pixel grid, so the
    /// threads stay crisp where scaled ones blur away. Falls back to the drawn indigo tile if the resource
    /// can't be read, so the tray always has an icon.</summary>
    private static Icon MakeIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("Mastermind.mastermind.ico");
            if (stream is not null) return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
        }
        catch { /* fall through to the drawn icon */ }
        return DrawFallbackIcon();
    }

    /// <summary>Draw a tray icon at runtime — a rounded tile in the cobweb's indigo with a spark. Fallback only.</summary>
    private static Icon DrawFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var grad = new LinearGradientBrush(
                new Rectangle(0, 0, 32, 32),
                ColorTranslator.FromHtml("#8280FF"),
                ColorTranslator.FromHtml("#5A57D1"),
                LinearGradientMode.ForwardDiagonal);
            using (var path = Rounded(new Rectangle(2, 2, 28, 28), 8))
                g.FillPath(grad, path);

            // a little white "activity" dot
            using var dot = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
            g.FillEllipse(dot, 13, 13, 7, 7);
            using var ring = new Pen(Color.FromArgb(110, 255, 255, 255), 2f);
            g.DrawEllipse(ring, 8.5f, 8.5f, 16, 16);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>The web with its marks, at the web's own size — the size Windows gives a small icon at this DPI:
    /// <paramref name="text"/> on a disc over its upper-left corner, which the web leaves empty, when there is a count,
    /// and VS Code health's orange triangle over its lower-left when <paramref name="health"/>; the web's anchor in the
    /// lower-right stays uncovered. Grayscale
    /// antialiasing: ClearType on a transparent bitmap fringes the digit.</summary>
    private static Icon DrawMarks(Icon web, string text, bool permission, bool health)
    {
        int size = web.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.DrawIcon(web, new Rectangle(0, 0, size, size));
            if (text != null)
            {
                float d = size * 0.7f;
                var disc = new RectangleF(0, 0, d, d);
                // Amber takes a dark digit: white on it reads at about 2:1.
                using var fill = new SolidBrush(ColorTranslator.FromHtml(permission ? "#E0A458" : "#0078D4"));
                g.FillEllipse(fill, disc);
                using var font = new Font("Segoe UI", d * (text.Length > 1 ? 0.52f : 0.72f), System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                using var ink = new SolidBrush(permission ? ColorTranslator.FromHtml("#101015") : Color.White);
                using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, ink, disc, center);
            }
            if (health)
            {
                // Drawn last, so the disc never hides it; the dark edge keeps it readable on a light and a dark taskbar.
                float t = size * 0.5f, o = size - t;
                var tri = new[] { new PointF(t / 2, o + 0.5f), new PointF(t - 0.5f, o + t - 0.5f), new PointF(0.5f, o + t - 0.5f) };
                using var orange = new SolidBrush(ColorTranslator.FromHtml("#F7630C"));
                using var edge = new Pen(Color.FromArgb(230, 32, 32, 32), Math.Max(1f, size / 16f));
                g.FillPolygon(orange, tri);
                g.DrawPolygon(edge, tri);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The shell's settings were written to config, which is read once: start a fresh run that waits for this
    /// one's lock, then exit — carrying the keys it moved, --background when this run had it, and --hidden when the
    /// panel is hidden to the tray. True while a fresh run is starting; false when none could start (warned), so the
    /// next settings message tries again.</summary>
    internal bool Restart(IReadOnlyList<string> keys)
    {
        var args = "--restart:" + string.Join(",", keys)
                   + (_background ? " --background" : "")
                   + (_win != null && !_win.IsVisible ? " --hidden" : "");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, args)
                { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            _log.Add(LogKind.Warn, $"config · restart failed ({ex.Message}) — the change takes effect at the next start");
            return false;
        }
        ExitApp();
        return true;
    }

    internal void ExitApp()
    {
        try { _rebind?.Stop(); } catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
        try { _hub?.Dispose(); } catch { }
        try { _win?.PrepareExit(); } catch { }
        Shutdown();
    }
}
