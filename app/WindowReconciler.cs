namespace Mastermind;

/// <summary>The "what sessions exist to show" source. Real impl reads the live-agent registry
/// (<c>~/.claude/sessions</c>) + open VS Code windows.</summary>
internal interface ISessionScanner
{
    List<AgentEvent> ScanActive();
}

/// <summary>The "what is running / open" source: live agent session ids + their busy / idle state, open
/// workspaces, any-window-up.</summary>
internal interface IVsCodeProbe
{
    HashSet<string> OpenProjectKeys();
    bool AnyVsCodeOpen();
    HashSet<string> LiveSessionIds();
    IReadOnlyDictionary<string, RunState> RunStates();
}

internal sealed class SessionScanner : ISessionScanner
{
    public List<AgentEvent> ScanActive() => Sessions.ScanActive();
}

internal sealed class VsCodeProbe : IVsCodeProbe
{
    public HashSet<string> OpenProjectKeys() => VsCode.OpenProjectKeys();
    public bool AnyVsCodeOpen() => VsCode.AnyVsCodeOpen();
    public HashSet<string> LiveSessionIds() => LiveSessions.ActiveIds();
    public IReadOnlyDictionary<string, RunState> RunStates() => LiveSessions.RunStates();
}

/// <summary>
/// Turns environment signals (live Claude agents + open VS Code windows) into <see cref="SessionStore"/>
/// mutations. Pulled out of <see cref="MainWindow"/> and put behind <see cref="IVsCodeProbe"/> /
/// <see cref="ISessionScanner"/> seams so the prune, the new-session/new-window pull, and the
/// degraded-state detection are testable without VS Code or a real <c>~/.claude</c>. Every method assumes
/// the UI/dispatcher thread (it mutates the store's bound collection) — the same assumption MainWindow's
/// DispatcherTimer already satisfied.
/// </summary>
internal sealed class WindowReconciler
{
    private readonly IVsCodeProbe _probe;
    private readonly ISessionScanner _scanner;
    private readonly SessionStore _store;

    private HashSet<string> _lastLiveIds = new(StringComparer.Ordinal);   // live agent session ids
    private HashSet<string> _lastOpenKeys = new();                        // full-path project keys

    public WindowReconciler(IVsCodeProbe probe, ISessionScanner scanner, SessionStore store)
    {
        _probe = probe;
        _scanner = scanner;
        _store = store;
    }

    /// <summary>VS Code is running but none of its workspaces are readable -> an empty list is a blind spot,
    /// not genuine quiet. The view shows the degraded banner instead of "All quiet".</summary>
    public bool Degraded { get; private set; }
    public event Action DegradedChanged;

    /// <summary>Cards a tick corrected off the registry's busy / idle state, each with the raw event name that
    /// restores its status — the view persists (StatusCache) and logs them; this class touches no disk.</summary>
    public event Action<List<(AgentEvent Fresh, string RawEvent)>> RunStateCorrected;

    /// <summary>Seed the last-seen sets so the first diff is a no-op, and set the initial degraded flag.</summary>
    public void Seed()
    {
        try { _lastLiveIds = _probe.LiveSessionIds(); } catch { }
        try { _lastOpenKeys = _probe.OpenProjectKeys(); } catch { }
        RecomputeDegraded();
    }

    /// <summary>One poll tick: diff the live-agent set + the open-window set, prune what left, pull what
    /// arrived. Both reads are light (a process enum + small files); the live-agent set already excludes
    /// sessions the user hid (LiveSessions filters them via a cached state.vscdb read), so a hidden session
    /// is pruned here on the tick after it's hidden.</summary>
    public void Tick() => Reconcile();

    /// <summary>Recompute the blind-spot flag from a fresh probe (after a manual refresh / baseline).</summary>
    public void RecomputeDegraded()
    {
        bool degraded;
        try { degraded = _probe.LiveSessionIds().Count == 0 && _probe.OpenProjectKeys().Count == 0 && _probe.AnyVsCodeOpen(); }
        catch { degraded = false; }
        SetDegraded(degraded);
    }

    private void SetDegraded(bool value)
    {
        if (value == Degraded) return;
        Degraded = value;
        DegradedChanged?.Invoke();
    }

    private void Reconcile()
    {
        HashSet<string> liveIds, openKeys;
        try { liveIds = _probe.LiveSessionIds(); } catch { return; }
        try { openKeys = _probe.OpenProjectKeys(); } catch { openKeys = new(); }

        // Blind-spot check runs EVERY tick, before the unchanged-set early-return: VS Code open but we can
        // see neither a live agent nor an open workspace = a blind spot, not genuine quiet.
        try { SetDegraded(liveIds.Count == 0 && openKeys.Count == 0 && _probe.AnyVsCodeOpen()); } catch { }

        // Busy / idle corrections run EVERY tick too: a turn stopped in VS Code changes no set, it only flips a
        // status no hook reports.
        try
        {
            var fixes = _store.ApplyRunStates(_probe.RunStates(), DateTime.Now);
            if (fixes.Count > 0) RunStateCorrected?.Invoke(fixes);
        }
        catch (Exception ex) { Diag.Warn("runStates", ex); }

        bool liveChanged = !liveIds.SetEquals(_lastLiveIds);
        bool windowChanged = !openKeys.SetEquals(_lastOpenKeys);
        if (!liveChanged && !windowChanged) return;    // nothing moved -> no card work

        bool appeared = liveChanged && liveIds.Any(id => !_lastLiveIds.Contains(id));
        bool windowOpened = windowChanged && openKeys.Any(k => !_lastOpenKeys.Contains(k));
        _lastLiveIds = liveIds;
        _lastOpenKeys = openKeys;

        // An agent that died / a window that closed -> drop it (never clobbers a live card).
        _store.PruneToLive(liveIds, openKeys);
        if (appeared || windowOpened)                  // a new agent / window -> pull it (never clobber)
        {
            List<AgentEvent> scanned;
            try { scanned = _scanner.ScanActive(); } catch { scanned = new(); }
            _store.AddMissing(scanned);
        }
        // Placeholders = open windows with no live agent. Reconciled every changed tick (not just on a
        // pull) so a mid-session agent-end re-surfaces its window's placeholder and a new agent collapses
        // the project's stale placeholder.
        _store.ReconcilePlaceholders(openKeys);
        _store.Resort();
    }
}
