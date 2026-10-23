using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using System.Windows.Threading;

namespace Mastermind;

public partial class MainWindow : Window
{
    private readonly SessionStore _store;
    private readonly LogStore _log;
    private readonly WindowReconciler _reconciler;
    private readonly PromptStore _prompts;
    private readonly PromptDispatcher _dispatcher;
    /// <summary>The VS Code windows' shells — App sets it once the listener exists.</summary>
    internal ClientHub Clients { get; set; }
    private readonly DispatcherTimer _ticker;
    private DispatcherTimer _saveTimer;
    private DispatcherTimer _pollTimer;
    private bool _restored;   // suppress saves until the startup restore has finished
    private bool _allowClose;
    private int _lastNeed;    // the big count pops only on a rise
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _toasts = new();   // transient Warn banners

    internal MainWindow(SessionStore store, WindowReconciler reconciler, LogStore log, PromptStore prompts, PromptDispatcher dispatcher)
    {
        _store = store;
        _reconciler = reconciler;
        _log = log;
        _prompts = prompts;
        _dispatcher = dispatcher;

        InitializeComponent();
        // The prompt inbox is the FIRST card in the SAME wrap flow as the session cards: a CompositeCollection
        // puts the PromptStore singleton ahead of the live Events. Two type-keyed DataTemplates (MainWindow.xaml
        // EventList.Resources) render each — the amber inbox card vs the session card. The inbox binds its inner
        // list to Items and the checkbox TwoWay to AutoMove, so no x:Name wiring is needed here.
        EventList.ItemsSource = new System.Windows.Data.CompositeCollection
        {
            _prompts,
            new System.Windows.Data.CollectionContainer { Collection = _store.Events },
        };
        ApplyListLayout();                     // normal = vertical column; fullscreen = column-major (set on toggle)
        LogList.ItemsSource = _log.Lines;
        _log.Added += () => LogScroller.ScrollToEnd();   // keep the newest line in view
        ToastHost.ItemsSource = _toasts;
        _log.Toast += ShowToast;                         // Warn lines also pop a transient banner

        // The view no longer owns the collection/reconcile logic — it just reacts to the store + reconciler.
        _store.Changed += UpdateCount;                        // counts + empty/degraded banner choice
        _reconciler.DegradedChanged += UpdateCount;           // blind-spot banner toggle
        // A card the registry's busy / idle state corrected: persist the status it now carries (a rescan restores
        // it) and say so in the log. Quiet on purpose — a turn YOU stopped in VS Code is no reason to pop the panel.
        _reconciler.RunStateCorrected += fixes =>
        {
            foreach (var (fresh, raw) in fixes)
            {
                StatusCache.Record(fresh.SessionId, raw);
                _log.Add(LogKind.Event, $"registry · {fresh.DisplayTitle} → {fresh.StatusText.ToLowerInvariant()}");
            }
        };

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _ticker.Tick += (_, _) => { foreach (var e in _store.Events) e.RefreshTime(); RefreshWorkingMeta(); };
        _ticker.Start();

        // A prompts.json save that moves heavy_match re-lights and relabels every card's model toggle — raised on the UI thread.
        PromptPresets.HeavyMatchChanged += () => { foreach (var e in _store.Events) e.RefreshHeavy(); };

        // Debounced placement save: dragging/resizing fires a burst of events; persist once it settles.
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveWindowState(); };
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();

        // The panel's floor, once laid out: its fixed rows — the band under the list counted at its full height whether
        // the big count or the log shows, since both can start hidden — and one whole card in the list, which keeps the
        // design height in normal mode. A window shorter than that grows upward, so one parked in a corner keeps its
        // bottom edge on the screen — up to its monitor's work-area top, past which it grows down, so one saved near the
        // top keeps its header, the drag handle, on the screen.
        Loaded += (_, _) =>
        {
            MinHeight = TitleBar.ActualHeight + ListScroller.Padding.Top + CardDesignHeight + CardGap + ListScroller.Padding.Bottom
                        + Math.Max(BigCount.Height, LogPanel.Height) + Footer.ActualHeight;
            if (!_full && Height < MinHeight)
            {
                Top = Math.Max(Top - (MinHeight - Height), CurrentWorkArea().Top);
                Height = MinHeight;
            }
        };

        UpdateChrome();
    }

    /// <summary>Pop a transient Warn banner (top of the LogStore funnel). Auto-dismisses after a few seconds and
    /// caps the stack so a burst can't pile up. Marshals to the UI thread in case a Warn is ever logged off it.</summary>
    private void ShowToast(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ShowToast(text)); return; }
        _toasts.Add(text);
        while (_toasts.Count > 4) _toasts.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); _toasts.Remove(text); };
        timer.Start();
    }

    // ---- DWM: dark mode + Win11 acrylic + rounded corners ------------------
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        int on = 1, round = 2, acrylic = 3;
        Native.DwmSetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
        Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        Native.DwmSetWindowAttribute(h, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, 4);
    }

    // ---- background reconcile (window lifecycle) ---------------------------
    /// <summary>
    /// Start the poll: every few seconds the <see cref="WindowReconciler"/> diffs the live-agent set
    /// (<c>~/.claude/sessions</c>) + the open VS Code window set, pruning what left and pulling what
    /// arrived. The view just owns the timer; the diff→store logic lives in the reconciler. Session
    /// create/end is also pushed by hooks — this poll is the backstop for a hard-killed agent / a window
    /// that closed without a SessionEnd.
    /// </summary>
    public void StartAutoRefresh()
    {
        _reconciler.Seed();   // seed last-seen sets + initial degraded so the first tick is a clean diff
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        // RAW live ids for the prompt scan's owner-alive check — the filtered ActiveIds excludes hidden
        // sessions, and a trashed-but-running owner must NOT orphan its in-flight prompt (see RawActiveIds).
        _pollTimer.Tick += (_, _) => { _reconciler.Tick(); _prompts.Tick(VsCode.OpenProjectKeys(), LiveSessions.RawActiveIds()); };
        _pollTimer.Start();
    }

    public void StopAutoRefresh() { try { _pollTimer?.Stop(); } catch { } }

    /// <summary>
    /// Re-read the transcript meta for every WORKING card (off the UI thread) so its TODO list (#3) and context
    /// bar stay fresh DURING a turn — the per-tool Pre/PostToolUse pings deliberately
    /// skip the transcript read (too hot), so this 15s sweep is where a working card's progress updates. Cheap:
    /// ReadMeta is a head+tail read cached on (path, mtime, length), and only working cards are swept.
    /// </summary>
    private void RefreshWorkingMeta()
    {
        var working = new List<AgentEvent>();
        foreach (var e in _store.Events)
            if (e.Status == AgentStatus.Working && !string.IsNullOrEmpty(e.SessionId)) working.Add(e);

        foreach (var ev in working)
        {
            string sid = ev.SessionId, cwd = ev.Cwd;
            Task.Run(() =>
            {
                SessionMeta m;
                try { m = Sessions.MetaForSession(sid, cwd); } catch { return; }
                Dispatcher.Invoke(() =>
                {
                    if (_store.ApplyMeta(ev, m))
                        _log.Add(LogKind.Warn, $"context compacted · {ev.DisplayTitle} — marker observed live");
                    _store.Resort();
                });
            });
        }
    }

    /// <summary>Re-scan the live agents + open VS Code windows (manual refresh). Scan off the UI
    /// thread, then apply on it.</summary>
    public void RefreshBaseline()
    {
        SetStatus("refreshing…", true);
        VsCodeState.InvalidateHidden();   // pick up a just-hidden / un-hidden session now, not after the TTL
        Task.Run(() =>
        {
            try { return Sessions.ScanActive(); } catch { return new List<AgentEvent>(); }
        }).ContinueWith(t =>
        {
            var items = t.Result;
            Dispatcher.Invoke(() =>
            {
                _store.Sync(items);            // in-place reconcile (no full teardown) -> fires Changed
                _reconciler.Seed();            // reseed last-sets + degraded after a full rescan
                SetStatus($"listening on 127.0.0.1:{EventHub.Port}", true);
            });
        });
    }

    private void UpdateCount()
    {
        bool any = !_store.IsEmpty;
        // Empty has two meanings: genuine quiet vs. "VS Code is open but unreadable". Never show the
        // cheerful "all quiet" for the blind case — that's the confident-but-wrong panel we're killing.
        bool blind = !any && _reconciler.Degraded;
        EmptyState.Visibility    = (!any && !blind) ? Visibility.Visible : Visibility.Collapsed;
        DegradedState.Visibility = blind ? Visibility.Visible : Visibility.Collapsed;

        // The badge counts who NEEDS you (permission / waiting for you / finished) — not working or idle rows.
        // Hidden when nobody does.
        int need = _store.NeedCount;
        CountBadge.Visibility = need > 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = need.ToString();

        // The badge and the big count where the hidden log sits: amber while a session waits on a permission (the
        // NEEDS PERMISSION pill's colour, and the tray disc's), else blue. Zero fades the big one out holding its last
        // number; a rise pops it.
        bool permission = _store.Events.Any(e => e.Status == AgentStatus.NeedsPermission);
        var ink = (System.Windows.Media.SolidColorBrush)FindResource(permission ? "Amber" : "Blue");
        CountText.Foreground = ink;
        CountBadge.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x22, ink.Color.R, ink.Color.G, ink.Color.B));
        if (need > 0) BigCountText.Text = need.ToString();
        BigCountText.Foreground = ink;
        BigCountBody.BeginAnimation(OpacityProperty, new DoubleAnimation(need > 0 ? 1 : 0, TimeSpan.FromMilliseconds(180)));
        if (need > _lastNeed)
        {
            var pop = new DoubleAnimation(1.18, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            BigCountScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pop);
            BigCountScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pop);
        }
        _lastNeed = need;
    }

    public void SetStatus(string text, bool ok)
    {
        StatusLine.Text = text;
        StatusLine.Foreground = ok ? (Brush)FindResource("TextLo") : (Brush)FindResource("Amber");
    }

    // ---- show / position ---------------------------------------------------
    // Named ShowPanel (not ShowAtCorner): placement is owned by RestoreWindowState + the user's drag, so
    // showing no longer corners the window — PlaceAtCorner is only the off-screen fallback.
    // force: a deliberate user action (tray click / menu Show) — raise to the front even in fullscreen.
    // Without force, an incoming hook event won't steal focus off a fullscreen dashboard (the _full guard).
    public void ShowPanel(bool bringToFront = true, bool force = false)
    {
        // Position is owned by RestoreWindowState / the user's own drag — we no longer
        // snap to the corner on every event (that fought "remember where I left it").
        // Show() activates a hidden window unless told not to, and once the foreground lock times out (a few idle
        // minutes) Windows lets that through — so a show that must not take the focus never activates.
        ShowActivated = bringToFront;
        Show();
        if (!bringToFront) return;       // startup: appear without stealing focus
        if (_full && !force) return;     // fullscreen dashboard: incoming events don't steal focus
        Activate();
        var h = new WindowInteropHelper(this).Handle;
        Native.ForceForeground(h);
    }

    /// <summary>Default placement (first run, or when the saved spot is off-screen): bottom-right corner.</summary>
    private void PlaceAtCorner()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 12;
        Top = wa.Bottom - Height - 12;
    }

    /// <summary>True if enough of the window's top strip lands inside the virtual desktop to be reachable.</summary>
    private bool IsOnAnyScreen()
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        bool xOk = Left + Width - 80 > vl && Left + 80 < vl + vw;
        bool yOk = Top + 40 > vt && Top + 20 < vt + vh;
        return xOk && yOk;
    }

    /// <summary>
    /// Restore the last placement (normal bounds + pin + fullscreen-on-its-monitor). Called once
    /// at startup before the window is shown. Falls back to the corner on first run or if the saved
    /// rect would land off-screen (e.g. a monitor was unplugged).
    /// </summary>
    public void RestoreWindowState()
    {
        var st = WindowStateStore.Load();
        // Log strip: shown unless a saved state explicitly hid it (null = pre-existing state / first run = shown).
        ApplyLogVisible(st?.LogVisible != false);
        if (st == null) { PlaceAtCorner(); _restored = true; return; }

        if (st.Width > 120 && st.Height > 160) { Width = st.Width; Height = st.Height; }
        Left = st.Left; Top = st.Top;
        Topmost = st.Topmost;
        UpdateChrome();
        if (!IsOnAnyScreen()) PlaceAtCorner();

        if (st.Full)
        {
            // Re-enter fullscreen after the window is shown (needs an hwnd + PresentationSource).
            double fl = st.FullLeft, ft = st.FullTop;
            var normal = new Rect(st.Left, st.Top, Width, Height);
            bool ntop = st.Topmost;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Left = fl; Top = ft;                                   // land on the saved monitor
                // A start that keeps the panel hidden (--background, --hidden) has no hwnd yet, and
                // FillCurrentMonitor's Screen.FromHandle reads a missing handle as the PRIMARY monitor — so the
                // dashboard refilled the wrong screen. Create the handle here, on the saved monitor, first.
                new WindowInteropHelper(this).EnsureHandle();
                if (!_full) Fullscreen_Click(this, new RoutedEventArgs());
                _restoreBounds = normal; _restoreTopmost = ntop;       // correct the Exit-fullscreen target
                _restored = true;
            }), DispatcherPriority.Loaded);
        }
        else _restored = true;
    }

    private void ScheduleSave()
    {
        if (!_restored) return;          // ignore the placement churn during restore
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveWindowState()
    {
        if (!_restored) return;
        // While fullscreen, the live bounds ARE the monitor fill — persist the stashed normal
        // bounds as the restore target and the fill origin as the monitor hint.
        var normal = _full ? _restoreBounds : new Rect(Left, Top, Width, Height);
        WindowStateStore.Save(new WindowState
        {
            Left = normal.X, Top = normal.Y, Width = normal.Width, Height = normal.Height,
            Topmost = _full ? _restoreTopmost : Topmost,
            Full = _full,
            FullLeft = _full ? Left : 0,
            FullTop = _full ? Top : 0,
            LogVisible = LogPanel.Visibility == Visibility.Visible,
        });
    }

    // ---- chrome / buttons --------------------------------------------------
    private void UpdateChrome()
    {
        PinButton.Content = Topmost ? "Unpin" : "Pin on top";
        ResizeGrips.Visibility = _full ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>An edge or corner thumb's drag (MainWindow.xaml RESIZE GRIPS): a left or top edge moves the window as
    /// it resizes, so the opposite edge stays put; never below MinWidth / MinHeight. SizeChanged saves the placement.</summary>
    private void Grip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        string edge = (string)((FrameworkElement)sender).Tag;
        if (edge.Contains("Left"))
        {
            double w = Math.Max(MinWidth, Width - e.HorizontalChange);
            Left += Width - w;
            Width = w;
        }
        else if (edge.Contains("Right")) Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        if (edge.Contains("Top"))
        {
            double h = Math.Max(MinHeight, Height - e.VerticalChange);
            Top += Height - h;
            Height = h;
        }
        else if (edge.Contains("Bottom")) Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    // Fullscreen is pinned to its monitor's work area: a header drag there only knocks the dashboard off its fill
    // (a stray hand on the mouse), and the way back was exit + re-enter fullscreen.
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_full && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>
    /// Bring the panel onto the PRIMARY monitor: fullscreen refills its work area (and Exit-fullscreen then lands
    /// in its corner), normal mode parks in its bottom-right corner — the first-run spot. Driven from the TRAY menu,
    /// never a panel button: the case it exists for is a panel sitting on a monitor you can't see. A two-step
    /// move: hop onto the primary first, then lay out once WPF has settled the hop, because crossing to a monitor
    /// with a different scale re-scales the window and the fill must be measured on the monitor it now sits on.
    /// </summary>
    public void MoveToPrimary()
    {
        var wa = SystemParameters.WorkArea;   // the PRIMARY monitor's work area, in DIPs
        Left = wa.Left + 40; Top = wa.Top + 40;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_full)
            {
                FillCurrentMonitor();
                var pw = SystemParameters.WorkArea;
                _restoreBounds = new Rect(pw.Right - _restoreBounds.Width - 12, pw.Bottom - _restoreBounds.Height - 12,
                                          _restoreBounds.Width, _restoreBounds.Height);
            }
            else PlaceAtCorner();
            SaveWindowState();
        }), DispatcherPriority.Background);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => Hide();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshBaseline();

    // ---- activity log ------------------------------------------------------
    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        ApplyLogVisible(LogPanel.Visibility != Visibility.Visible);
        SaveWindowState();   // persist the choice
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    /// <summary>Show/hide the log strip + sync the footer button label. No save here — callers decide
    /// (the toggle persists; restore/seed just apply the loaded state).</summary>
    private void ApplyLogVisible(bool visible)
    {
        LogPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BigCount.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        LogButton.Content = visible ? "Hide log" : "Log";
    }

    /// <summary>
    /// Delete every hidden ("done with this one") Claude session from disk + clear the hidden set. Invoked
    /// from the tray menu (no on-panel button). The safety gate lives here, not in visibility: hiddenSessionIds
    /// written while an editor runs is overwritten on its quit, so it refuses while any IDE lock is live or any
    /// editor process runs (IProcesses.RunningEditors — the configured name and the known look-alikes), naming them.
    /// </summary>
    public void CleanHiddenSessions()
    {
        // Named in the box: a windowless leftover (the editor's CLI, a language server) blocks it with no window to
        // close, and so does another IDE's Claude lock.
        var editors = Platform.Processes.RunningEditors();
        var liveLock = IdeRegistry.LiveLock();
        if (liveLock != null || editors.Count > 0)
        {
            MastermindDialog.Notice(this, "Mastermind — clean hidden sessions",
                "Close every editor window first — the archived list can't be safely rewritten while an editor runs."
                + (editors.Count > 0 ? $"\n\nStill running: {string.Join(", ", editors)}" : "")
                + (liveLock != null ? $"\n\nLive IDE lock: {liveLock}" : ""));
            return;
        }

        HashSet<string> hidden;
        try { hidden = VsCodeState.HiddenSessionIds(); } catch { return; }
        if (hidden.Count == 0)
        {
            MastermindDialog.Notice(this, "Mastermind — clean hidden sessions", "No hidden sessions to clean.");
            return;
        }

        if (!MastermindDialog.Ask(this, "Mastermind — clean hidden sessions",
                $"Permanently delete {hidden.Count} hidden (stale) Claude sessions from disk and clear the list?\n\nThis cannot be undone.",
                "Delete")) return;

        int deleted = 0;
        try { deleted = Sessions.DeleteSessions(hidden); } catch { }
        try { VsCodeState.ClearHiddenSessionIds(hidden); } catch { }
        SetStatus($"deleted {deleted} stale sessions", true);
    }

    /// <summary>
    /// Reap TRASHED-but-still-running Claude agents whose turn has ended / gone idle (working ones are spared
    /// — see <see cref="Sessions.LooksWorking"/>). Kill-only — transcripts kept, so a reaped session still
    /// opens if un-trashed. Writes no state.vscdb, so it runs with VS Code open. Reaped sessions are hidden
    /// (no cards) — the confirm dialog + log + status line are the feedback. Invoked from the tray menu.
    /// </summary>
    public void ReapBackgroundAgents()
    {
        SetStatus("scanning for trashed agents…", true);
        Task.Run(() =>
        {
            var reap = new List<(int Pid, string SessionId, string Cwd, string FilePath)>();
            int spared = 0;
            foreach (var t in LiveSessions.HiddenLive())
                if (Sessions.LooksWorking(t.SessionId, t.Cwd)) spared++; else reap.Add(t);
            return (reap, spared);
        }).ContinueWith(scan =>
        {
            Dispatcher.Invoke(() =>
            {
                SetStatus($"listening on 127.0.0.1:{EventHub.Port}", true);
                if (scan.IsFaulted)   // observe the fault (the scan body is best-effort, so this is defensive)
                {
                    _log.Add(LogKind.Warn, "reap scan failed: " + scan.Exception?.GetBaseException().Message);
                    return;
                }
                var (reap, spared) = scan.Result;
                if (reap.Count == 0)
                {
                    MastermindDialog.Notice(this, "Mastermind — reap background agents",
                        spared > 0
                            ? $"Nothing idle to reap — {spared} trashed agent(s) are still working and were spared."
                            : "No trashed-but-running Claude agents to reap.");
                    return;
                }
                var msg = $"Kill {reap.Count} idle/finished trashed Claude agent(s)?"
                        + (spared > 0 ? $"\n\n{spared} still-working trashed agent(s) will be SPARED." : "")
                        + "\n\nTheir processes (and the RAM they hold) are freed now. Session history is KEPT — "
                        + "a reaped session still opens if you un-trash it.";
                if (MastermindDialog.Ask(this, "Mastermind — reap background agents", msg, "Reap")) KillReaped(reap);
            });
        });
    }

    /// <summary>Off-thread kill loop for the confirmed reap set: re-verify each pid is still a trashed claude,
    /// tree-kill it, sweep its registry file, and do the SessionEnd cleanup a hard kill skips
    /// (StatusCache + AutoAllow — both thread-safe/locked). Per-reap and per-skip lines to the log.</summary>
    private void KillReaped(List<(int Pid, string SessionId, string Cwd, string FilePath)> reap)
    {
        SetStatus($"reaping {reap.Count} background agents…", true);
        Task.Run(() =>
        {
            var hidden = VsCodeState.HiddenSessionIds();   // fresh, for the kill-time re-verify
            int killed = 0, skipped = 0;
            foreach (var t in reap)
            {
                if (!LiveSessions.StillTrashed(t.Pid, t.FilePath, hidden))
                {
                    skipped++;
                    Dispatcher.Invoke(() => _log.Add(LogKind.Drop, $"reap skipped · pid {t.Pid} — no longer a trashed claude"));
                    continue;
                }
                var r = Platform.Processes.KillClaude(t.Pid);
                if (r == Proc.KillResult.Killed)
                {
                    killed++;
                    try { System.IO.File.Delete(t.FilePath); } catch { }
                    // Hard kill fires NO SessionEnd — do its cleanup here, else auto-allow resurrects
                    // safety-off (and a stale status shows a false "needs you") when the session is un-trashed.
                    StatusCache.Remove(t.SessionId);
                    AutoAllowStore.Remove(t.SessionId);
                    string who = Paths.Leaf(t.Cwd);
                    string label = string.IsNullOrEmpty(who) ? t.SessionId : who;
                    Dispatcher.Invoke(() => _log.Add(LogKind.Info, $"reaped · {label} (pid {t.Pid})"));
                }
                else
                {
                    skipped++;
                    string note = r switch
                    {
                        Proc.KillResult.AccessDenied => "access denied (VS Code elevated?)",
                        Proc.KillResult.NotClaude    => "pid no longer a claude",
                        _                            => "already gone",
                    };
                    Dispatcher.Invoke(() => _log.Add(LogKind.Warn, $"reap skipped · pid {t.Pid} — {note}"));
                }
            }
            Dispatcher.Invoke(() => SetStatus(
                $"reaped {killed} background agents" + (skipped > 0 ? $" ({skipped} skipped)" : ""), true));
        });
    }

    /// <summary>
    /// The card's archive: put the session away through the extension's own archive (<see cref="CdpInjector.TryArchive"/>),
    /// then stop its process, which archiving alone leaves running. Two-click guarded (<see cref="ArmOrFire"/>).
    /// All or nothing on idleness — the card's status can lag, so the click re-checks the registry's busy / waiting
    /// state and the reaper's transcript test, and a busy session is neither archived nor stopped. Neighbour titles
    /// are captured here, on the UI thread, for the log line.
    /// </summary>
    private void Archive_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not System.Windows.Controls.Button b || b.DataContext is not AgentEvent ev
            || string.IsNullOrEmpty(ev.SessionId) || !ArmOrFire(b)) return;
        string sid = ev.SessionId, cwd = ev.Cwd, label = ev.DisplayTitle;
        var neighbours = _store.NeighbourIds(sid);
        var names = neighbours.ToDictionary(id => id, id => _store.TitleFor(id));
        _log.Add(LogKind.Info, $"archive → {label} …");
        Task.Run(() => ArchiveAndStop(sid, cwd, neighbours, names)).ContinueWith(t =>
        {
            var (archived, kind, note, why, next) = t.IsFaulted
                ? (false, LogKind.Warn, "failed — " + t.Exception?.GetBaseException().Message,
                   "Something went wrong: " + t.Exception?.GetBaseException().Message, "Try again; Mastermind's crash log has the details.")
                : t.Result;
            Dispatcher.Invoke(() =>
            {
                _log.Add(kind, $"archive → {label} : {note}");
                if (why != null) Failed("archive", label, note, why, next);
                if (!archived) return;
                VsCodeState.InvalidateHidden();
                _store.RemoveSession(sid);
            });
        });
    }

    /// <summary>Off-thread body of <see cref="Archive_Click"/>. The kill waits until the archive is readable in
    /// <c>state.vscdb</c> and then runs the reaper's own guards (<see cref="LiveSessions.StillTrashed"/>, a last
    /// <see cref="Sessions.LooksWorking"/>, <see cref="IProcesses.KillClaude"/>), so a session the extension did not
    /// archive is never killed. A hard kill fires no SessionEnd, so its cleanup runs here, as in the reaper.</summary>
    private static async Task<(bool Archived, LogKind Kind, string Note, string Why, string Next)> ArchiveAndStop(
        string sid, string cwd, List<string> neighbours, Dictionary<string, string> names)
    {
        var held = LiveSessions.Find(sid);
        if ((held?.Run.Status ?? "") is "busy" or "waiting" || Sessions.LooksWorking(sid, cwd))
            return (false, LogKind.Drop, "still working — not archived",
                "The session is working, and an archived session's work runs on unseen.", "Archive it once it finishes.");

        var o = await CdpInjector.TryArchive(sid, cwd, neighbours);
        switch (o.Result)
        {
            case CdpInjector.ArchiveResult.PortOff:      return (false, LogKind.Warn, "debug port off — can't archive",
                PortOffWhy("archive"), PortOffNext);
            case CdpInjector.ArchiveResult.NotFound:     return (false, LogKind.Drop, "session not found in any sidebar — is its project window open?",
                "No open Claude panel lists that session.", "Open its project window in VS Code, then archive again.");
            case CdpInjector.ArchiveResult.NoButton:     return (false, LogKind.Drop, "archive button not found (selector drift?)",
                "Claude's panel changed, and Mastermind can't find the archive button.", "Archive it from the panel's session list until Mastermind is updated.");
            case CdpInjector.ArchiveResult.SwitchFailed: return (false, LogKind.Drop, "couldn't move the sidebar off it — not archived",
                "The panel showing it could not be moved to another session first, and archiving the session a panel shows opens a blank one there.",
                "Switch that panel to another session, then archive again.");
        }

        string moved = o.SwitchedTo is null ? "" : $" · sidebar moved to {names.GetValueOrDefault(o.SwitchedTo, "another session")}";
        if (held is not { } h) return (true, LogKind.Info, "archived" + moved, null, null);

        HashSet<string> hidden = new();
        for (int i = 0; i < 20; i++)
        {
            try { hidden = VsCodeState.HiddenSessionIdsAny(); } catch { }
            if (hidden.Contains(sid)) break;
            await Task.Delay(250);
        }
        if (!LiveSessions.StillTrashed(h.Pid, h.FilePath, hidden))
            return (true, LogKind.Warn, "archived, not on disk yet — process left running (Reap background agents stops it)" + moved,
                "The archive had not reached VS Code's state file in time, so the process was left running rather than stop a session VS Code may still show.",
                "Reap background agents, in the tray menu, stops it later.");
        if (Sessions.LooksWorking(sid, cwd))
            return (true, LogKind.Warn, "archived, but it started working — process left running" + moved,
                "The session started working after the archive, so its process was left running.",
                "Reap background agents stops it once it is idle.");

        var r = Platform.Processes.KillClaude(h.Pid);
        if (r != Proc.KillResult.Killed)
        {
            string cause = r switch
            {
                Proc.KillResult.AccessDenied => "access denied (VS Code elevated?)",
                Proc.KillResult.NotClaude    => "pid no longer a claude",
                _                            => "already gone",
            };
            return (true, LogKind.Warn, $"archived · process not stopped — {cause}" + moved,
                $"Its process could not be stopped — {cause}.", "Close the session in VS Code, or run Reap background agents later.");
        }
        try { System.IO.File.Delete(h.FilePath); } catch { }
        StatusCache.Remove(sid);
        AutoAllowStore.Remove(sid);
        return (true, LogKind.Info, "archived · process stopped" + moved, null, null);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdateChrome();
        SaveWindowState();
    }

    // ---- fullscreen (fills the current monitor's work area, unpins) --------
    private bool _full;
    private Rect _restoreBounds;
    private bool _restoreTopmost;

    // (The old IsFullscreenMode DependencyProperty + ReserveInFullscreenConverter pair is gone: card rows no
    // longer reserve blank space per mode — the card's bottom-anchored grid pins the control rows to the
    // bottom edge and lets the * detail row absorb whatever the optional rows don't use, in BOTH modes.)

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (!_full)
        {
            _restoreBounds = new Rect(Left, Top, Width, Height);
            _restoreTopmost = Topmost;
            FillCurrentMonitor();
            Topmost = false;                 // unpin so it can sit on a second monitor
            _full = true;
            FullscreenButton.Content = ""; // back-to-window glyph
            FullscreenButton.ToolTip = "Exit fullscreen";
        }
        else
        {
            Left = _restoreBounds.X; Top = _restoreBounds.Y;
            Width = _restoreBounds.Width; Height = _restoreBounds.Height;
            Topmost = _restoreTopmost;
            _full = false;
            FullscreenButton.Content = "";
            FullscreenButton.ToolTip = "Fullscreen";
        }
        ApplyListLayout();   // switch the card flow to match the new mode
        UpdateChrome();
        SaveWindowState();   // persist immediately — a toggle is a deliberate placement choice
    }

    /// <summary>Size the window to the work area of the monitor it sits on.</summary>
    private void FillCurrentMonitor()
    {
        var wa = CurrentWorkArea();
        Left = wa.Left; Top = wa.Top;
        Width = wa.Width; Height = wa.Height;
    }

    /// <summary>The work area of the monitor the window sits on, in DIPs (device px through this window's own
    /// transform, so a scaled monitor measures exactly).</summary>
    private Rect CurrentWorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea; // device px
        double dx = 1, dy = 1;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            dx = src.CompositionTarget.TransformFromDevice.M11;
            dy = src.CompositionTarget.TransformFromDevice.M22;
        }
        return new Rect(wa.Left * dx, wa.Top * dy, wa.Width * dx, wa.Height * dy);
    }

    // Card design height (App.xaml CardHeight), the card's bottom margin, and the horizontal scrollbar's band —
    // RESERVED always, so the bar appearing or vanishing can never re-trigger the height it depends on.
    private const double CardDesignHeight = 372;
    private const double CardDesignWidth = 344;   // App.xaml CardWidth
    private const double CardGap = 6;
    private const double CardGapX = 8;            // EventCard's right margin
    private const double HScrollBand = 14;

    /// <summary>
    /// Fullscreen cards always fit TWO per column: when two design-height rows don't fit the list's height (a
    /// 768px laptop, or the log strip open), every card shrinks to half the space — only the * detail row gives up
    /// height, the pinned rows stay put. A screen that fits two at design height keeps them; normal mode (one
    /// column that scrolls down) always uses the design height. Across, fullscreen fits WHOLE columns: as many
    /// design-width cards as the width holds, each widened to share what is left, so no visible column is cut
    /// through. Driven off the list's own size, so it follows fullscreen toggles, the log strip and monitor moves alike.
    /// </summary>
    private void UpdateCardHeight()
    {
        double h = CardDesignHeight, w = CardDesignWidth;
        if (_full)
        {
            double avail = ListScroller.ActualHeight - ListScroller.Padding.Top - ListScroller.Padding.Bottom - HScrollBand;
            if (avail > 0 && avail < 2 * (CardDesignHeight + CardGap)) h = Math.Floor(avail / 2 - CardGap);
            double across = ListScroller.ActualWidth - ListScroller.Padding.Left - ListScroller.Padding.Right;
            int columns = (int)(across / (CardDesignWidth + CardGapX));
            if (columns >= 1) w = Math.Floor(across / columns - CardGapX);
        }
        if (!(Resources["CardHeight"] is double cur && cur == h)) Resources["CardHeight"] = h;
        if (!(Resources["CardWidth"] is double curW && curW == w)) Resources["CardWidth"] = w;
    }

    private void ListScroller_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardHeight();

    /// <summary>
    /// The card flow depends on the mode. Normal: a single vertical column (WrapPanel Horizontal in a narrow
    /// window) that scrolls down. Fullscreen: column-major (WrapPanel Vertical) — cards fill top→bottom then
    /// spill into the next column, the panel bounded to the viewport height, scrolling sideways. Paired with
    /// the urgency sort this packs the cards that need you into the top-left first.
    /// </summary>
    private void ApplyListLayout()
    {
        bool cols = _full;
        var panel = new FrameworkElementFactory(typeof(WrapPanel));
        panel.SetValue(WrapPanel.OrientationProperty,
            cols ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal);
        EventList.ItemsPanel = new ItemsPanelTemplate(panel);
        ListScroller.VerticalScrollBarVisibility   = cols ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ListScroller.HorizontalScrollBarVisibility = cols ? ScrollBarVisibility.Auto      : ScrollBarVisibility.Disabled;
        // The viewer draws its bars outside its Padding, so the sideways bar ran window edge to edge while the
        // cards sit inset; its ends take the list's own left and right padding, lining up with the cards.
        ListScroller.ApplyTemplate();
        if (ListScroller.Template.FindName("PART_HorizontalScrollBar", ListScroller)
            is System.Windows.Controls.Primitives.ScrollBar bar)
            bar.Margin = new Thickness(ListScroller.Padding.Left, 0, ListScroller.Padding.Right, 0);
        UpdateCardHeight();
    }

    /// <summary>
    /// Wheel → SIDEWAYS scroll in fullscreen's column-major layout. Normal (single-column) mode is left alone:
    /// it scrolls down, where the default wheel handling is already correct. The why, the tunnelling
    /// requirement and the nested-scroller carve-out all live in <see cref="WheelScroll"/>, which owns the
    /// logic; this is just the wiring.
    /// </summary>
    private void ListScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ListScroller.HorizontalScrollBarVisibility != ScrollBarVisibility.Auto) return;   // normal mode
        if (e.OriginalSource is DependencyObject src
            && WheelScroll.HasScrollableInnerScroller(src, ListScroller)) return;
        ListScroller.ScrollToHorizontalOffset(
            WheelScroll.NextHorizontalOffset(ListScroller.HorizontalOffset, e.Delta));
        e.Handled = true;
    }

    /// <summary>
    /// Navigate to a session: focus its VS Code window + CDP-switch the sidebar. Wired to the card's TITLE
    /// (and the project-name line as the fallback for title-less / placeholder cards) — NOT the whole card,
    /// so reaching into the detail/checkbox/dropdown can't teleport you. e.Handled stops the click from
    /// also reaching anything underneath.
    /// </summary>
    private void Navigate_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentEvent ev) return;
        e.Handled = true;
        // We DON'T clear the status here — clicking only sends you there; it clears on a real reply (→ WORKING).

        // We're foreground right now (you just clicked the card), so we're the only one allowed to lift the
        // foreground lock. Grant it to every process BEFORE the async raise: the CDP `Page.bringToFront` runs
        // inside Code.exe, which is NOT foreground while you're in another app, so without this it can only
        // flash the taskbar. The grant stays valid until the foreground changes — long enough for the switch
        // (a few hundred ms out) to land. Covers the title-match fallback path below too.
        Platform.EditorWindows.GrantForeground();

        // Primary path (CDP): match THIS exact session's sidebar, raise its EXACT host window
        // (webview parentId → page target → Page.bringToFront — session-precise, fixes the
        // wrong-same-project-window bug), and switch the sidebar to it. Only when CDP can't see the session
        // (port off, window not open, or an untitled/placeholder row) do we fall back to title-match focus —
        // which is project-precise at best and never deep-links the session as an editor tab.
        if (!string.IsNullOrEmpty(ev.SessionId))
        {
            string sid = ev.SessionId, title = ev.Title, cwd = ev.Cwd, label = ev.DisplayTitle;
            // The session's own window switches its sidebar first — no debug port needed — and the CDP path waits for
            // its answer, at most 2 s (OpenSession's deadline): after a switch it finds the sidebar already there and
            // only raises the window (exact with the port on, by title without it); after none it switches as it
            // always has.
            var host = Clients?.OpenSession(sid);
            var answer = host ?? Task.FromResult<string>(null);
            answer.ContinueWith(a =>
            {
                bool switched = host != null && a.Result == null;
                if (host != null && !switched)
                    Dispatcher.Invoke(() => _log.Add(LogKind.Warn, $"sidebar → {label} : its window did not switch ({a.Result})"));
                CdpSwitcher.TrySwitch(sid, title, focusWindow: true, cwd: cwd).ContinueWith(t =>
                {
                    var o = t.IsFaulted ? new CdpSwitcher.Outcome(CdpSwitcher.Result.NotFound, false) : t.Result;
                    // CDP didn't reach the session (port off / no matching sidebar) → best-effort title focus.
                    // The raise marshals to the UI thread itself; only the log line needs the dispatcher.
                    if (o.Result is CdpSwitcher.Result.PortOff or CdpSwitcher.Result.NotFound)
                    {
                        var raise = Platform.EditorWindows.RaiseProject(cwd);
                        if (raise != RaiseResult.Raised) Dispatcher.Invoke(() => _log.Add(LogKind.Warn, RaiseNote(raise, label)));
                    }
                    var kind = o.Result switch
                    {
                        CdpSwitcher.Result.Switched or CdpSwitcher.Result.AlreadyThere => LogKind.Info,
                        CdpSwitcher.Result.PortOff => switched ? LogKind.Info : LogKind.Warn,
                        _ => LogKind.Drop,
                    };
                    var note = o.Result == CdpSwitcher.Result.PortOff
                        ? (switched ? "switched by its window — raised by title" : "debug port off — focus only")
                        : o.Result.ToString();
                    Dispatcher.Invoke(() =>
                    {
                        _log.Add(kind, $"sidebar → {label} : {note}");
                        LogTitleFallback(o, label);
                    });
                });
            });
        }
        else
        {
            var raise = Platform.EditorWindows.RaiseProject(ev.Cwd);   // untitled / placeholder card — title focus is all we have
            if (raise != RaiseResult.Raised) _log.Add(LogKind.Warn, RaiseNote(raise, ev.DisplayTitle));
        }
    }

    /// <summary>The log line for a window raise that did not land: none matched, or Windows kept it back.</summary>
    internal static string RaiseNote(RaiseResult raise, string label) => raise == RaiseResult.Refused
        ? $"Windows kept the VS Code window back (taskbar flash) · {label}"
        : $"no VS Code window matched · {label}";

    /// <summary>
    /// A session was identified by NAME instead of by id — meaning the React fiber path
    /// (<see cref="CdpJs.SidOf"/>) resolved nothing, i.e. a Claude update moved it. Name matching still works
    /// but is neither rename-proof nor duplicate-proof, so this must never be silent. Logged from the navigate
    /// path only (the most-used action); <see cref="SelectorSelfCheck"/> catches the same drift at startup, so the
    /// write-side actions don't each re-report it.
    /// </summary>
    private void LogTitleFallback(CdpSwitcher.Outcome outcome, string label)
    {
        if (!outcome.ViaTitle) return;
        _log.Add(LogKind.Warn, $"sidebar → {label} : matched by NAME, not session id — "
                             + "Claude Code changed its panel — update Mastermind");
    }

    /// <summary>
    /// The card's "send a prompt" button → the canned-prompt PICKER (<see cref="PickerWindow"/>): one click only
    /// selects, Send (or Enter) acts, any click outside dismisses — so a slipped click never fires a prompt. The
    /// chosen text is injected into the session's composer and submitted over CDP — no window focus (reply to an
    /// agent without leaving where you are), a hand-typed draft kept and appended to. The session's own
    /// UserPromptSubmit flips the card to WORKING. Session fields are captured at the click, not at the pick.
    /// </summary>
    private void SendMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.DataContext is not AgentEvent ev || string.IsNullOrEmpty(ev.SessionId)) return;

        string sid = ev.SessionId, title = ev.Title, label = ev.DisplayTitle, cwd = ev.Cwd;
        var picker = new PickerWindow($"Send to {label}", PromptPresets.All, "Send", preset =>
        {
            _log.Add(LogKind.Info, $"send → {label} : \"{preset.Label}\"…");
            CdpInjector.TrySend(sid, title, preset.Text, cwd: cwd).ContinueWith(t =>
            {
                var (kind, note, why, next) = SendOutcome(t.IsFaulted ? CdpInjector.Result.NotFound : t.Result, "send");
                Dispatcher.Invoke(() =>
                {
                    _log.Add(kind, $"send → {label} : {note}");
                    if (why != null) Failed("send", label, note, why, next);
                });
            });
        }) { Owner = this };
        picker.PlaceNear(fe);
        picker.Show();
    }

    // ---- incoming-prompt inbox-card handlers ------------------------------
    private void PromptMove_Click(object s, RoutedEventArgs e) { if (Ctx(s) is {} it) _dispatcher.Transport(it); }
    // Multi-window-same-project: DispatchNew → TryCreateAndSend acts on the FIRST cwd-matching sidebar and logs
    // it. Single window is the normal case. A confirm guards it: the receiving agent reads the file and deletes
    // it, so a slipped click is not undoable.
    private void PromptNew_Click(object s, RoutedEventArgs e)
    {
        if (Ctx(s) is not {} it) return;
        if (!MastermindDialog.Ask(this, "Mastermind — dispatch to a new agent",
                $"Start a new {Paths.Leaf(it.DestRoot)} session and hand it\n\n{it.FileName} ?", "Dispatch")) return;
        _log.Add(LogKind.Info, $"dispatch new · {it.FileName} …");
        Fire(_dispatcher.DispatchNew(it), it);
    }

    // Fire-and-forget a dispatch but OBSERVE faults (finding F): the dispatcher logs its own outcomes; this only
    // surfaces an unexpected throw so it never goes silently unobserved, matching the .ContinueWith pattern the
    // other CDP handlers (SendMenu_Click, Go_Click, …) already use.
    private void Fire(Task t, PromptItem it) => t.ContinueWith(x =>
        { if (x.IsFaulted) Dispatcher.Invoke(() => _log.Add(LogKind.Warn, $"dispatch faulted · {it.FileName} — {x.Exception?.GetBaseException().Message}")); },
        TaskScheduler.Default);
    private void PromptDelete_Click(object s, RoutedEventArgs e) { if (Ctx(s) is {} it) _dispatcher.DeleteInProcess(it); }
    private void PromptBack_Click(object s, RoutedEventArgs e) { if (Ctx(s) is {} it) _dispatcher.BackToInbox(it); }

    /// <summary>Pick one of the dest project's idle agents (not WORKING, not WAITING) in the confirm-before-act
    /// <see cref="PickerWindow"/>, then dispatch to it — the same slip guard as the prompt picker, since the agent
    /// reads the file and deletes it.</summary>
    private void PromptSendMenu_Click(object s, RoutedEventArgs e)
    {
        if (Ctx(s) is not {} it || s is not FrameworkElement fe) return;
        var agents = _store.OpenAgentsForProject(Paths.Key(it.DestRoot));
        if (agents.Count == 0) { _log.Add(LogKind.Drop, $"dispatch · {it.FileName} — no idle agent in {Paths.Leaf(it.DestRoot)}"); return; }

        // One row per agent, matched back by REFERENCE (rows[i] ↔ agents[i]) — never by title, which two agents can
        // share. The preview says what the pick does; the session id is never displayed.
        var rows = agents.Select(x => new PromptPreset(x.DisplayTitle,
            $"{x.DisplayTitle}\n{x.ProjectName} · {x.StatusText.ToLowerInvariant()} · {x.TimeText}\n\n"
            + $"receives: {PromptRoute.DispatchText(it.FileName)}")).ToList();
        var picker = new PickerWindow($"Hand {it.FileName} to…", rows, "Dispatch", pick =>
        {
            int i = rows.FindIndex(r => ReferenceEquals(r, pick));
            if (i < 0) return;
            var a = agents[i];
            _log.Add(LogKind.Info, $"dispatch → {a.DisplayTitle} · {it.FileName} …");
            Fire(_dispatcher.DispatchTo(it, a.SessionId, a.Title), it);
        }) { Owner = this };
        picker.PlaceNear(fe);
        picker.Show();
    }

    private static PromptItem Ctx(object s) => (s as FrameworkElement)?.DataContext as PromptItem;

    /// <summary>
    /// The card's quick replies — `go` (prompts.json <c>go</c>) and `rec` (<c>rec</c>, "as you recommend") — on a card
    /// whose agent has handed you the turn. Two-click guarded (<see cref="ArmOrFire"/>): a slipped click only arms
    /// the button. Sent WHOLE (appendable:false): a hand-typed draft in that composer refuses the send rather than
    /// going out with the reply tacked onto it. The session's own UserPromptSubmit flips the card to WORKING.
    /// </summary>
    private void Go_Click(object sender, RoutedEventArgs e) => SendReply(sender, e, "go", PromptPresets.Go);
    private void Rec_Click(object sender, RoutedEventArgs e) => SendReply(sender, e, "rec", PromptPresets.Rec);

    private void SendReply(object sender, RoutedEventArgs e, string name, string text)
    {
        e.Handled = true;
        if (sender is not System.Windows.Controls.Button b || b.DataContext is not AgentEvent ev
            || string.IsNullOrEmpty(ev.SessionId) || !ArmOrFire(b)) return;
        string sid = ev.SessionId, title = ev.Title, label = ev.DisplayTitle;
        _log.Add(LogKind.Info, $"{name} → {label} …");
        CdpInjector.TrySend(sid, title, text, appendable: false, cwd: ev.Cwd).ContinueWith(t =>
        {
            var (kind, note, why, next) = SendOutcome(t.IsFaulted ? CdpInjector.Result.NotFound : t.Result, "send");
            Dispatcher.Invoke(() =>
            {
                _log.Add(kind, $"{name} → {label} : {note}");
                if (why != null) Failed(name, label, note, why, next);
            });
        });
    }

    // An armed quick-reply button: its disarm timer plus the label + colour to restore — the colour as its LOCAL value,
    // unset when the style supplies it, so a restore never pins a colour over the style's hover.
    private sealed record Armed(DispatcherTimer Timer, object Content, object Foreground);

    /// <summary>
    /// The two-click guard on the one-click senders: the FIRST click arms the button (bright, "go?") for a few seconds
    /// and sends nothing; only a second click on the armed button returns true (fire). Left alone it disarms itself.
    /// Inline rather than a dialog — a deliberate send stays two quick clicks on the same spot, a slip sends nothing.
    /// State lives on the button (<c>Tag</c>), so a card swapped out by a status change simply starts disarmed.
    /// </summary>
    private bool ArmOrFire(System.Windows.Controls.Button b)
    {
        if (b.Tag is Armed armed) { Disarm(b, armed); return true; }
        var a = new Armed(new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }, b.Content,
            b.ReadLocalValue(System.Windows.Controls.Control.ForegroundProperty));
        a.Timer.Tick += (_, _) => Disarm(b, a);
        b.Tag = a;
        b.Content = $"{a.Content}?";
        b.Foreground = (Brush)FindResource("TextHi");
        a.Timer.Start();
        return false;
    }

    private static void Disarm(System.Windows.Controls.Button b, Armed a)
    {
        a.Timer.Stop();
        if (!ReferenceEquals(b.Tag, a)) return;
        b.Tag = null;
        b.Content = a.Content;
        if (a.Foreground == DependencyProperty.UnsetValue) b.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        else b.SetValue(System.Windows.Controls.Control.ForegroundProperty, a.Foreground);
    }

    /// <summary>The log line for a composer send — shared by every card action that types into a session (prompt,
    /// go, model switch) — and, for one that did not go, why and what to do next, which the dialog shows.
    /// <paramref name="verb"/> names what a closed debug port stopped.</summary>
    private static (LogKind kind, string note, string why, string next) SendOutcome(CdpInjector.Result r, string verb) => r switch
    {
        CdpInjector.Result.Sent         => (LogKind.Info, "sent", null, null),
        CdpInjector.Result.PortOff      => (LogKind.Warn, $"debug port off — can't {verb}", PortOffWhy(verb), PortOffNext),
        CdpInjector.Result.SwitchFailed => (LogKind.Drop, "session not found in any sidebar",
            "No open Claude panel shows that session.", "Open the session's project window in VS Code, then try again."),
        CdpInjector.Result.NoInput      => (LogKind.Drop, "composer not found (selector drift?)",
            "Claude's panel changed, and Mastermind can't find its message box.", "Type it in the panel yourself until Mastermind is updated."),
        CdpInjector.Result.DraftInWay   => (LogKind.Warn, "unsent text in that composer — send or clear it first",
            "Something is typed in that session's message box, and Mastermind never overwrites it.", "Send or clear what is typed there, then try again."),
        _                                => (LogKind.Drop, "not sent",
            "The send did not go through.", "Try again; if it keeps failing, open the session in VS Code."),
    };

    private static string PortOffWhy(string verb) => $"Mastermind needs VS Code's debug port to {verb}, and it is off.";
    private const string PortOffNext =
        "Turn it on: run the walkthrough Set up Agent Mastermind in VS Code, or set Mastermind: Debug Port, then restart VS Code.";
    private const string NoSessionWhy = "That card has no session id yet.";
    private const string NoSessionNext = "Wait for the session's first event, then try again.";

    /// <summary>A click refused or failed, on screen until read; its log line went first. Queued behind the rest
    /// of the click's work, so a card update lands first and no worker thread waits on the dialog.</summary>
    private void Failed(string action, string label, string note, string why, string next) =>
        Dispatcher.InvokeAsync(() => MastermindDialog.Tell(this, $"{action} → {label}: {note}", why, next));

    /// <summary>#5 — answer one pending permission from the card. allow-once / deny over CDP.</summary>
    private void Allow_Click(object sender, RoutedEventArgs e) => AnswerPermission(sender, e, allow: true);
    private void Deny_Click(object sender, RoutedEventArgs e) => AnswerPermission(sender, e, allow: false);

    /// <summary>
    /// Click the dialog's allow-once (first) or deny (last) button over CDP. A backgrounded permission session
    /// isn't in CDP's target list (see the draft constraint) → fall back to OS-focusing its window so you can
    /// answer it there. Fire-and-forget; outcome to the log. The card flips on its own via the resulting hook
    /// (PostToolUse on allow → WORKING; the next turn on deny).
    /// </summary>
    private void AnswerPermission(object sender, RoutedEventArgs e, bool allow)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not AgentEvent ev) return;
        AnswerPermissionFor(ev, allow);
    }

    /// <summary>Answer a card's pending permission over CDP (shared by the allow/deny buttons and the
    /// auto-allow toggle). No-ops with a log line when the card has no resolved title to match the sidebar by.</summary>
    private void AnswerPermissionFor(AgentEvent ev, bool allow, bool quiet = false)
    {
        if (string.IsNullOrEmpty(ev.SessionId)) { _log.Add(LogKind.Warn, $"can't answer — no session · {ev.ProjectName}"); if (!quiet) Failed(allow ? "allow" : "deny", ev.ProjectName, "no session", NoSessionWhy, NoSessionNext); return; }

        string sid = ev.SessionId, title = ev.Title, cwd = ev.Cwd, label = ev.DisplayTitle, verb = allow ? "allow" : "deny";
        _log.Add(LogKind.Info, $"{verb} → {label} …");
        CdpInjector.TryAnswerPermission(sid, title, allow, cwd).ContinueWith(t =>
        {
            var r = t.IsFaulted ? CdpInjector.PermissionResult.NoDialog : t.Result;
            var (kind, note, why, next) = r switch
            {
                CdpInjector.PermissionResult.Answered     => (LogKind.Info, allow ? "allowed once" : "denied", (string)null, (string)null),
                CdpInjector.PermissionResult.PortOff      => (LogKind.Warn, "debug port off — can't answer", PortOffWhy("answer a permission"), PortOffNext),
                // Log-only: the raised window is the on-screen signal, and a topmost dialog would take the foreground back.
                CdpInjector.PermissionResult.SwitchFailed => (LogKind.Drop, "session not reachable (window backgrounded?) — raising it", (string)null, (string)null),
                _                                          => (LogKind.Drop, "no pending dialog found",
                    "No permission dialog is open in that session — it was answered or closed already.", "Nothing to do; the card catches up on the session's next event."),
            };
            Dispatcher.Invoke(() =>
            {
                if (r == CdpInjector.PermissionResult.SwitchFailed) Platform.EditorWindows.RaiseProject(cwd);
                _log.Add(kind, $"{verb} → {label} : {note}");
                if (why != null && !quiet) Failed(verb, label, note, why, next);
            });
        });
    }

    /// <summary>
    /// The card's auto-allow CheckBox was toggled. The two-way binding has already flipped
    /// <see cref="AgentEvent.AutoAllow"/> (whose setter persisted it to <see cref="AutoAllowStore"/>) by the
    /// time this fires, so the real work here is logging the new state (ON as a warn, since it's a safety-off
    /// switch) plus the already-pending-dialog catch-up below. The <c>e.Handled</c> is the house pattern every
    /// card handler follows, NOT a live guard: navigation moved off the card root onto the title / chip /
    /// project-name elements, none of which is an ancestor of this checkbox, so nothing bubbles anywhere.
    /// </summary>
    private void AutoAllow_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not AgentEvent ev) return;
        _log.Add(ev.AutoAllow ? LogKind.Warn : LogKind.Info,
            $"auto-allow {(ev.AutoAllow ? "ON" : "off")} · {ev.DisplayTitle}");
        // Turning auto-allow ON while a permission is ALREADY pending: the hook can't retro-answer a dialog
        // that's already open, and the manual allow/deny buttons are hidden once auto-allow is ON — so clear
        // the pending one over CDP here, or the card sticks at NEEDS PERMISSION with no way to act from the panel.
        if (ev.AutoAllow && ev.Status == AgentStatus.NeedsPermission)
            AnswerPermissionFor(ev, allow: true, quiet: true);   // the toggle succeeded — a catch-up that cannot answer stays a log line
    }

    /// <summary>
    /// The card's model toggle: inject <c>/model …</c> into this session's composer over CDP — onto the heavy model,
    /// or back to the default. The direction is decided by the model the card shows, so one button is a genuine
    /// two-state control instead of two competing ones. The switch fires PostModelSwitch, whose <c>to_model</c> moves
    /// the card (App.OnEvent → SessionStore.SetHookModel); nothing here flips it, so a send that silently did nothing
    /// leaves the card as it was.
    /// </summary>
    private void ModelToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not AgentEvent ev) return;
        if (string.IsNullOrEmpty(ev.SessionId))
        { _log.Add(LogKind.Warn, $"can't switch model — no session · {ev.ProjectName}"); Failed("model", ev.ProjectName, "no session", NoSessionWhy, NoSessionNext); return; }

        bool toHeavy = !ev.IsHeavy;
        string cmd = toHeavy ? PromptPresets.ModelOn : PromptPresets.ModelOff;
        string sid = ev.SessionId, title = ev.Title, label = ev.DisplayTitle;
        // Going heavy is logged as a Warn so it also pops a toast — it costs real money and is easy to forget.
        _log.Add(toHeavy ? LogKind.Warn : LogKind.Info, $"model → {label} : {cmd} …");

        // appendable:false — /model is a BUILT-IN client command with no prose form (the agent can't switch its
        // own model), so it must go as the whole message: a hand-typed draft in the composer refuses the send
        // (DraftInWay) instead of being overwritten or turning the command into prose.
        CdpInjector.TrySend(sid, title, cmd, appendable: false, cwd: ev.Cwd).ContinueWith(t =>
        {
            var r = t.IsFaulted ? CdpInjector.Result.NotFound : t.Result;
            var (kind, note, why, next) = SendOutcome(r, "switch model");
            Dispatcher.Invoke(() =>
            {
                _log.Add(kind, $"model → {label} : {note}");
                if (why != null) Failed("model", label, note, why, next);
            });
        });
    }

    // ---- close => hide, unless the app is really exiting -------------------
    public void PrepareExit() { _allowClose = true; StopAutoRefresh(); SaveWindowState(); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
