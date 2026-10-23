using System.Collections.ObjectModel;
using System.Linq;

namespace Mastermind;

/// <summary>
/// The session-card collection plus every mutation/diff rule that used to live tangled inside
/// <see cref="MainWindow"/>. Plain (no Window, no disk), so the merge rules — above all the
/// never-downgrade-a-live-card invariant in <see cref="AddMissing"/> — can be exercised without a UI
/// thread or a real <c>~/.claude</c>. It does mutate the bound <see cref="Events"/> collection, so every
/// call must be on the UI/dispatcher thread (the same assumption MainWindow already held). Raises
/// <see cref="Changed"/> after any mutation so the view can refresh counts + the empty/degraded banners.
/// </summary>
internal sealed class SessionStore
{
    public ObservableCollection<AgentEvent> Events { get; } = new();

    /// <summary>Fired after any mutation that changed the collection.</summary>
    public event Action Changed;

    private const int MaxCards = 60;   // trim cap: drops from the tail, sparing needs-you cards (see AddEvent)

    // Last tool action per session (a PreToolUse phrase), replayed onto a detail-less working ping so a WORKING card
    // keeps showing what the agent is doing; a new prompt clears it. In-memory; RemoveSession clears it.
    private readonly Dictionary<string, string> _lastCommand = new(StringComparer.Ordinal);

    // Your last prompt per session — every card of the session shows it in its you block (AgentEvent.YouText). Off
    // UserPromptSubmit live, seeded off the transcript by the scan (SeedPrompt). In-memory; RemoveSession clears it.
    private readonly Dictionary<string, string> _lastPrompt = new(StringComparer.Ordinal);

    // The model each session's last PostModelSwitch hook named — newer than any transcript read, which runs a turn
    // behind after an automatic or resume switch, so a card of that session takes it and the meta reads leave it be.
    // Forget clears it.
    private readonly Dictionary<string, string> _hookModel = new(StringComparer.Ordinal);

    /// <summary>True once a PostModelSwitch hook named this session's model.</summary>
    private bool HasHookModel(string sessionId) => !string.IsNullOrEmpty(sessionId) && _hookModel.ContainsKey(sessionId);

    /// <summary>Copy a transcript read onto a live card (<see cref="AgentEvent.ApplyMeta"/>), the model left alone once a
    /// PostModelSwitch hook named the session's — every meta read goes through here. Returns ApplyMeta's
    /// first-compaction flag.</summary>
    public bool ApplyMeta(AgentEvent card, SessionMeta meta) => card.ApplyMeta(meta, keepModel: HasHookModel(card.SessionId));

    /// <summary>Drop a session from every per-session map — RemoveSession and the hard-kill backstop share it.</summary>
    private void Forget(string sessionId)
    {
        _lastCommand.Remove(sessionId);
        _lastPrompt.Remove(sessionId);
        _hookModel.Remove(sessionId);
        _heldStops.Remove(sessionId);
    }

    // Stops held while the session's own Stop hooks still run: Mastermind's entry fires as they START, the registry
    // keeps the session busy until the last one ends, and one can still block and continue the turn. Each card shows
    // WORKING ("finishing") meanwhile, and ReleaseHeldStops hands the Stop back once the registry reads idle.
    // In-memory: a restart mid-hold leaves the card WORKING, and the registry correction moves it on.
    private readonly Dictionary<string, AgentEvent> _heldStops = new(StringComparer.Ordinal);

    /// <summary>True while any Stop waits on its session's Stop hooks.</summary>
    public bool HasHeldStops => _heldStops.Count > 0;

    /// <summary>Hold a Stop the registry still reads busy: its card shows WORKING, finishing, until
    /// <see cref="ReleaseHeldStops"/> returns it. A newer Stop of the session replaces it. UI thread.</summary>
    public void HoldStop(AgentEvent stop)
    {
        if (string.IsNullOrEmpty(stop.SessionId)) return;
        _heldStops[stop.SessionId] = stop;
        var card = Events.FirstOrDefault(e => e.SessionId == stop.SessionId && e.Status == AgentStatus.Working);
        if (card != null) card.Detail = AgentEvent.FinishingNote;   // in place, and never into _lastCommand — it is no command
        else AddEvent(AgentEvent.LocalStatus(stop.SessionId, stop.Cwd, AgentStatus.Working, AgentEvent.FinishingNote));
    }

    /// <summary>Drop a session's held Stop — a new prompt, a permission ask, a question or a background wait means its
    /// turn did not end with it. UI thread.</summary>
    public void DropHeldStop(string sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId)) _heldStops.Remove(sessionId);
    }

    /// <summary>The held Stops whose session the registry reads idle, or no longer lists, taken out of the hold for the
    /// caller to deliver as it delivers any Stop. A held session the registry reads busy or waiting stays held — an
    /// event of its own moves it on. UI thread.</summary>
    public List<AgentEvent> ReleaseHeldStops(IReadOnlyDictionary<string, RunState> states)
    {
        var done = new List<AgentEvent>();
        foreach (var (sid, stop) in _heldStops.ToList())
        {
            if (states.TryGetValue(sid, out var st) && st.Status != "idle") continue;
            _heldStops.Remove(sid);
            done.Add(stop);
        }
        return done;
    }

    /// <summary>Record a PostModelSwitch hook's model and show it on the session's card. False when either is empty.</summary>
    public bool SetHookModel(string sessionId, string model)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(model)) return false;
        _hookModel[sessionId] = model;
        foreach (var e in Events) if (e.SessionId == sessionId) e.Model = model;
        return true;
    }

    private void ApplyHookModel(AgentEvent e)
    {
        if (!string.IsNullOrEmpty(e.SessionId) && _hookModel.TryGetValue(e.SessionId, out var m)) e.Model = m;
    }

    /// <summary>True when the session's card came from a Stop received within <paramref name="window"/> — every hook event
    /// rides its own curl, so the turn's last tool ping can land after its Stop, and the dispatcher drops it.</summary>
    public bool StoppedWithin(string sessionId, TimeSpan window)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        if (_heldStops.TryGetValue(sessionId, out var held)) return DateTime.Now - held.When < window;
        foreach (var e in Events)
            if (e.SessionId == sessionId && e.Kind is RawKind.Stop or RawKind.StopWaiting)
                return DateTime.Now - e.When < window;
        return false;
    }

    /// <summary>A subagent's tool ping: a heartbeat for the parent's card, never a status change — the stall clock of
    /// a WAITING card moves, a WORKING card is touched as <see cref="TouchWorking"/> touches it, any other card is left
    /// as it is.</summary>
    public void TouchSubagent(string sessionId, DateTime when, string detail)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        foreach (var e in Events)
            if (e.SessionId == sessionId && e.Status == AgentStatus.AwaitingTask) e.When = when;
        TouchWorking(sessionId, when, detail);
    }

    // The one hide test (config's projects[].hide), applied at every way a card enters: the seed, a refresh, a hook
    // event and a registry correction (both through AddEvent), a scan pull and a placeholder. The project order comes in
    // the same way: both read config and the manifest, and the store stays plain (no disk).
    private readonly Func<string, bool> _isHidden;
    private readonly Func<string, int> _order;
    public SessionStore(Func<string, bool> isHidden, Func<string, int> order) { _isHidden = isHidden; _order = order; }
    private bool Hidden(AgentEvent e) => _isHidden(e.Cwd);

    public int Count => Events.Count;
    public bool IsEmpty => Events.Count == 0;

    /// <summary>How many cards currently need the user (permission / waiting for you / finished — see
    /// <see cref="AgentEvent.NeedsYou"/>) — drives badge + tray text.</summary>
    public int NeedCount { get { int n = 0; foreach (var e in Events) if (e.NeedsYou) n++; return n; } }

    /// <summary>Open, idle session cards in a given project (by full-path key) — the send-to-existing-agent
    /// candidates. Excludes placeholders (no session), WORKING agents (never inject mid-turn) and WAITING agents
    /// (a background task's completion will wake them — a handoff injected now interleaves with that wake).</summary>
    public List<AgentEvent> OpenAgentsForProject(string projectKey)
    {
        var outp = new List<AgentEvent>();
        foreach (var e in Events)
            if (!string.IsNullOrEmpty(e.SessionId) && e.Status is not (AgentStatus.Working or AgentStatus.AwaitingTask)
                && AgentEvent.ProjectKey(e.Cwd) == projectKey) outp.Add(e);
        return outp;
    }

    /// <summary>The other live sessions of a card's project, nearest first: the cards below it in list order,
    /// then the ones above it, closest first — the sessions an archive may move that project's sidebar onto.</summary>
    public List<string> NeighbourIds(string sessionId)
    {
        var mine = Events.FirstOrDefault(e => e.SessionId == sessionId);
        if (mine is null) return new List<string>();
        var key = AgentEvent.ProjectKey(mine.Cwd);
        var same = Events.Where(e => !string.IsNullOrEmpty(e.SessionId) && AgentEvent.ProjectKey(e.Cwd) == key).ToList();
        int at = same.IndexOf(mine);
        return same.Skip(at + 1).Concat(same.Take(at).Reverse()).Select(e => e.SessionId).ToList();
    }

    /// <summary>The session card's display title (resolved name, else project), or "" if it has no card yet.
    /// Used to label log lines with the session name instead of its hash.</summary>
    public string TitleFor(string sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
            foreach (var e in Events) if (e.SessionId == sessionId) return e.DisplayTitle;
        return "";
    }

    /// <summary>The session card's current status, or null if it has no card yet — lets the dispatcher skip a
    /// no-op working-ping (a PostToolUse while already WORKING) instead of churning the card.</summary>
    public AgentStatus? CurrentStatus(string sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
            foreach (var e in Events) if (e.SessionId == sessionId) return e.Status;
        return null;
    }

    /// <summary>Append rows without clearing (startup seed).</summary>
    public void AddRange(IEnumerable<AgentEvent> items)
    {
        foreach (var e in items) if (!Hidden(e)) { SeedPrompt(e); Events.Add(e); }
        Fire();
    }

    /// <summary>A scanned card arrives with your last prompt off its transcript; remember it, so the session's next
    /// live card keeps its you block — the map is in-memory, empty after a restart. A prompt the live path already
    /// holds wins.</summary>
    private void SeedPrompt(AgentEvent e)
    {
        if (!string.IsNullOrEmpty(e.SessionId) && !string.IsNullOrEmpty(e.YouText))
            _lastPrompt.TryAdd(e.SessionId, e.YouText);
    }

    /// <summary>
    /// Reconcile the list to <paramref name="scanned"/> in place for a manual refresh — WITHOUT the hard
    /// Clear()+re-add that <see cref="Reload"/> did. A full teardown recreates every item container, so each
    /// card replays the DataTemplate's Loaded entry animation at once (the refresh "flicker"). Instead: keep
    /// the existing card for a session whose status is unchanged (just patch its title/context via the
    /// notifying setters — no container churn), swap it only when the status changed, insert genuinely-new
    /// sessions, drop ones the scan no longer lists. Only changed/new cards animate, as on the live path.
    /// </summary>
    public void Sync(IEnumerable<AgentEvent> scanned)
    {
        // Card identity: real sessions by id, placeholder rows by their project key.
        static string Key(AgentEvent e) => string.IsNullOrEmpty(e.SessionId)
            ? "::" + AgentEvent.ProjectKey(e.Cwd) : e.SessionId;

        var existing = new Dictionary<string, AgentEvent>(StringComparer.Ordinal);
        foreach (var e in Events) existing[Key(e)] = e;

        var wantKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in scanned)
        {
            if (Hidden(s)) continue;
            var k = Key(s);
            if (!wantKeys.Add(k)) continue;                 // de-dup a doubled scan row
            if (existing.TryGetValue(k, out var cur) && cur.Status == s.Status)
            {
                // Same session, same status → keep the live instance (and its container); refresh the
                // mutable bits the scan may have advanced. These are notifying setters: in-place, no reload.
                cur.ContextCompacted = s.ContextCompacted;   // scan is fresh truth in BOTH directions
                if (s.ContextTokens > 0 && cur.ContextTokens != s.ContextTokens) cur.ContextTokens = s.ContextTokens;
                if (!string.IsNullOrEmpty(s.Title) && cur.Title != s.Title) cur.Title = s.Title;
                if (!string.IsNullOrEmpty(s.Model) && cur.Model != s.Model && !HasHookModel(cur.SessionId)) cur.Model = s.Model;
                if (!string.IsNullOrEmpty(s.YouText) && cur.YouText != s.YouText)
                {
                    cur.YouText = s.YouText;
                    _lastPrompt[cur.SessionId] = s.YouText;
                }
            }
            else
            {
                if (cur != null) { InheritResolved(s, cur); Events.Remove(cur); }  // status swap → keep title/ctx
                ApplyHookModel(s);
                SeedPrompt(s);
                Events.Insert(0, s);                        // new / changed card (this one animates)
            }
        }

        for (int i = Events.Count - 1; i >= 0; i--)         // drop what the scan no longer lists
            if (!wantKeys.Contains(Key(Events[i]))) Events.RemoveAt(i);

        ResortInternal();
        Fire();
    }

    /// <summary>
    /// A live hook event: drop this session's prior card (or its project's empty placeholder), insert at
    /// the top, re-sort by urgency, trim the least-urgent tail.
    /// </summary>
    public void AddEvent(AgentEvent ev)
    {
        if (Hidden(ev)) return;
        // Your last prompt rides EVERY card of the session, in its you block: UserPromptSubmit carries it, the map
        // keeps it, and a new prompt clears the last tool action — a new turn's line starts empty, not stale. The
        // detail keeps the agent's side: PostToolUse (the per-tool firehose, and the only signal after you GRANT a
        // permission) carries no phrase of its own, so the remembered tool action is replayed onto a detail-less
        // ping and the card never blanks when a permission interruption swapped it out (the in-turn repeats are
        // dropped upstream while already working). In-memory; the scan seeds the prompts after a restart (SeedPrompt).
        if (!string.IsNullOrEmpty(ev.SessionId))
        {
            if (ev.Kind == RawKind.UserPromptSubmit && !string.IsNullOrEmpty(ev.YouText))
            {
                _lastPrompt[ev.SessionId] = ev.YouText;
                _lastCommand.Remove(ev.SessionId);
            }
            else if (ev.Kind == RawKind.PostToolUse || ev.Kind == RawKind.PreToolUse)
            {
                if (!string.IsNullOrEmpty(ev.Detail)) _lastCommand[ev.SessionId] = ev.Detail;
                else if (_lastCommand.TryGetValue(ev.SessionId, out var cmd)) ev.Detail = cmd;
            }
            if (string.IsNullOrEmpty(ev.YouText) && _lastPrompt.TryGetValue(ev.SessionId, out var you)) ev.YouText = you;
        }

        var projKey = AgentEvent.ProjectKey(ev.Cwd);
        for (int i = Events.Count - 1; i >= 0; i--)
        {
            var cur = Events[i];
            // Replace this exact session (a prior live event OR its baseline row).
            bool sameSession = !string.IsNullOrEmpty(ev.SessionId) && cur.SessionId == ev.SessionId;
            // Collapse the project's empty placeholder (no-session-yet) row once a real event lands.
            bool emptyPlaceholder = cur.IsBaseline && string.IsNullOrEmpty(cur.SessionId)
                                    && AgentEvent.ProjectKey(cur.Cwd) == projKey;
            if (sameSession || emptyPlaceholder)
            {
                if (sameSession) InheritResolved(ev, cur);   // keep the resolved title/context across the swap
                Events.RemoveAt(i);
            }
        }

        ApplyHookModel(ev);
        Events.Insert(0, ev);
        ResortInternal();
        // Trim at the cap — but NEVER a card that needs the user. The sort is project-order (no urgency
        // term), so the tail is just the alphabetically-last project; blindly dropping it could silently
        // eat a NEEDS PERMISSION card while pinned OPEN rows survive. Walk from the tail for the first
        // non-needy card; only if every card is needy does the actual tail go.
        while (Events.Count > MaxCards)
        {
            int drop = Events.Count - 1;
            for (int i = Events.Count - 1; i >= 0; i--)
                if (!Events[i].NeedsYou) { drop = i; break; }
            Events.RemoveAt(drop);
        }
        Fire();
    }

    /// <summary>
    /// Heartbeat an already-WORKING card without churning it: advance its last-activity time (feeds the stall
    /// detector — #1) and, when the incoming tool ping carries a live action phrase (a meaningful PreToolUse —
    /// #2), update its detail line in place. A read/grep ping carries no phrase, so it only bumps the time
    /// (keeps a long exploration "busy" without spamming the line). No re-insert / no re-sort — the card stays
    /// put; the notifying setters refresh just the time + detail bindings. Called instead of a full AddEvent
    /// for a per-tool ping the dispatcher would otherwise drop as a no-op.
    /// </summary>
    public void TouchWorking(string sessionId, DateTime when, string detail, string you = null)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        // A prompt sent while the agent is still working (queued mid-turn) moves the you block in place.
        if (!string.IsNullOrEmpty(you)) _lastPrompt[sessionId] = you;
        foreach (var e in Events)
            if (e.SessionId == sessionId && e.Status == AgentStatus.Working)
            {
                e.When = when;
                if (!string.IsNullOrEmpty(you)) e.YouText = you;
                if (!string.IsNullOrEmpty(detail))
                {
                    e.Detail = detail;
                    _lastCommand[sessionId] = detail;   // so a later blank ping replays this, not a stale command
                }
            }
    }

    // How long a registry state must have held before it corrects a card. Hooks stay the primary signal: a Stop
    // still in flight (its hook waits up to a second for the reply to flush) always lands well inside this.
    private static readonly TimeSpan RunStateGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Correct cards off each live agent's own busy / idle state (<see cref="LiveSessions.RunStates"/>) — the CLI's
    /// truth about whether a turn is running, for the transitions Claude Code fires no hook for: a turn stopped in
    /// VS Code ends with no Stop, and a background-task wake can start one with no UserPromptSubmit. A state counts
    /// only when it is NEWER than the card's last event and has held for <see cref="RunStateGrace"/>:
    ///   busy    on a card that is not mid-turn (FINISHED / WAITING FOR YOU / WAITING / OPEN) → WORKING
    ///   idle    on a WORKING card                                                         → WAITING FOR YOU
    ///   waiting (blocked on the user) on a WORKING card                                   → WAITING FOR YOU
    /// A card whose Stop is held is never corrected here — its release decides it.
    /// NEEDS PERMISSION is never corrected: the approved tool's PostToolUse already clears it, and a registry that
    /// re-stamped "busy" or reported "idle" while the dialog is open would strip a live amber of its allow/deny.
    /// An absent status (an engine older than the field) corrects nothing. Returns each replacement card with the
    /// raw event name that restores its status, so the caller can persist + log it (this class touches no disk).
    /// </summary>
    public List<(AgentEvent Fresh, string RawEvent)> ApplyRunStates(IReadOnlyDictionary<string, RunState> states, DateTime now)
    {
        var fixes = new List<(AgentEvent Fresh, string RawEvent)>();
        foreach (var cur in Events.ToList())
        {
            if (string.IsNullOrEmpty(cur.SessionId) || !states.TryGetValue(cur.SessionId, out var st) || st.At <= 0) continue;
            if (_heldStops.ContainsKey(cur.SessionId)) continue;   // its held Stop decides it (ReleaseHeldStops)
            var at = DateTimeOffset.FromUnixTimeMilliseconds(st.At).LocalDateTime;
            if (at <= cur.When || now - at < RunStateGrace) continue;   // a hook is as new, or the state too fresh
            AgentStatus? to = (st.Status, cur.Status) switch
            {
                ("busy", AgentStatus.Finished or AgentStatus.WaitingForYou or AgentStatus.AwaitingTask or AgentStatus.Open)
                    => AgentStatus.Working,
                ("idle", AgentStatus.Working) => AgentStatus.WaitingForYou,
                ("waiting", AgentStatus.Working) => AgentStatus.WaitingForYou,
                _ => null,
            };
            if (to == null) continue;
            string detail = to == AgentStatus.Working
                ? (_lastCommand.TryGetValue(cur.SessionId, out var cmd) ? cmd : "")
                : st.Status == "idle" ? "stopped — your turn" : "waiting on you in VS Code";
            var fresh = AgentEvent.LocalStatus(cur.SessionId, cur.Cwd, to.Value, detail);
            AddEvent(fresh);
            fixes.Add((fresh, to == AgentStatus.Working ? "PostToolUse" : "Notification"));
        }
        return fixes;
    }

    /// <summary>Set every card's auto-allow to the store as it stands. A card seeds it only at creation, so a value set
    /// elsewhere reaches a live card this way: a new-session dispatch turning it ON (else the card shows OFF while
    /// actually auto-allowing). Reflected, never written.
    /// UI thread.</summary>
    public void SyncAutoAllow()
    {
        foreach (var e in Events)
            if (!string.IsNullOrEmpty(e.SessionId)) e.ReflectAutoAllow(AutoAllowStore.IsOn(e.SessionId));
    }

    /// <summary>Drop a session's card (pushed by the SessionEnd hook).</summary>
    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        Forget(sessionId);
        bool any = false;
        for (int i = Events.Count - 1; i >= 0; i--)
            if (Events[i].SessionId == sessionId) { Events.RemoveAt(i); any = true; }
        if (any) Fire();
    }

    /// <summary>
    /// Insert scanned rows not already shown — by session id, or by project for placeholders. It NEVER
    /// replaces an existing card, so a live FINISHED / NEEDS-PERMISSION card is never clobbered by a
    /// scanned baseline row. This is the load-bearing merge invariant; keep it.
    /// </summary>
    public void AddMissing(IEnumerable<AgentEvent> scanned)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>();   // canonical project keys (full paths), lower-cased
        foreach (var e in Events)
        {
            if (!string.IsNullOrEmpty(e.SessionId)) ids.Add(e.SessionId);
            keys.Add(AgentEvent.ProjectKey(e.Cwd));
        }
        bool any = false;
        foreach (var s in scanned)
        {
            if (Hidden(s)) continue;
            if (!string.IsNullOrEmpty(s.SessionId))
            {
                if (ids.Add(s.SessionId)) { ApplyHookModel(s); SeedPrompt(s); Events.Insert(0, s); keys.Add(AgentEvent.ProjectKey(s.Cwd)); any = true; }
            }
            else
            {
                var key = AgentEvent.ProjectKey(s.Cwd);
                if (keys.Add(key)) { Events.Insert(0, s); any = true; }
            }
        }
        if (any) Fire();
    }

    /// <summary>
    /// Drop cards that no longer belong: a session-bearing card survives only while its agent is live
    /// (<paramref name="liveIds"/>) — a backstop for a missed SessionEnd hook / a hard-killed agent; a
    /// placeholder row survives only while its project window is open (<paramref name="openKeys"/>).
    /// Returns true if anything went.
    /// </summary>
    public bool PruneToLive(HashSet<string> liveIds, HashSet<string> openKeys)
    {
        bool any = false;
        for (int i = Events.Count - 1; i >= 0; i--)
        {
            var e = Events[i];
            bool keep = !string.IsNullOrEmpty(e.SessionId)
                ? liveIds.Contains(e.SessionId)
                : openKeys.Contains(AgentEvent.ProjectKey(e.Cwd));
            if (!keep)
            {
                // The hard-kill backstop path skips RemoveSession, so clear the remembered command here
                // too — otherwise dead sessions' entries accumulate for the app's whole lifetime.
                if (!string.IsNullOrEmpty(e.SessionId)) Forget(e.SessionId);
                Events.RemoveAt(i); any = true;
            }
        }
        if (any) Fire();
        return any;
    }

    /// <summary>
    /// Make the placeholder rows exactly match "open windows with no live agent": drop a placeholder once
    /// its project gains a real session card (collapse) or its window closed, and add one for an open
    /// project that has no card yet. The single owner of the placeholder invariant — call it on every
    /// reconcile so a mid-session agent-end re-surfaces its window's placeholder (the incremental add/prune
    /// paths only touch live cards). A placeholder's cwd IS the full-path project key, so focus + ProjectKey
    /// both resolve. Returns true if anything changed.
    /// </summary>
    public bool ReconcilePlaceholders(HashSet<string> openKeys)
    {
        var covered = new HashSet<string>();        // project keys with a real (session-bearing) card
        var placeheld = new HashSet<string>();      // project keys that already have a placeholder
        foreach (var e in Events)
        {
            var key = AgentEvent.ProjectKey(e.Cwd);
            if (!string.IsNullOrEmpty(e.SessionId)) covered.Add(key);
            else placeheld.Add(key);
        }

        bool any = false;
        // Drop a placeholder whose project now has a real card, or whose window is gone.
        for (int i = Events.Count - 1; i >= 0; i--)
        {
            var e = Events[i];
            if (!string.IsNullOrEmpty(e.SessionId)) continue;
            var key = AgentEvent.ProjectKey(e.Cwd);
            if (covered.Contains(key) || !openKeys.Contains(key)) { Events.RemoveAt(i); any = true; }
        }
        // Add a placeholder for an open project with neither a real card nor an existing placeholder.
        foreach (var key in openKeys)
            if (!covered.Contains(key) && !placeheld.Contains(key) && !_isHidden(key))
            {
                Events.Insert(0, AgentEvent.Baseline(key, "", "", 0, DateTime.Now));
                any = true;
            }
        if (any) Fire();
        return any;
    }

    /// <summary>
    /// Carry the already-resolved display fields (Title, ContextTokens) from a card being replaced onto the
    /// fresh event taking its place. <see cref="AgentEvent.FromHook"/> leaves both empty (they're filled off
    /// the dispatcher), so without this a trigger — above all PostToolUse, which deliberately skips the async
    /// re-read — would blank the card until the next full resolve. The ONE place every replace path
    /// (<see cref="AddEvent"/> + <see cref="Sync"/>'s status-swap) reuses, so cards stay intact identically.
    /// </summary>
    private static void InheritResolved(AgentEvent fresh, AgentEvent prior)
    {
        if (string.IsNullOrEmpty(fresh.Title) && !string.IsNullOrEmpty(prior.Title)) fresh.Title = prior.Title;
        // A fresh card with NO context of its own (FromHook leaves it 0) inherits prior's number AND prior's
        // compacted state — otherwise a status swap on a compacted card resurfaces the stale token number
        // until the next meta read re-derives the flag. A fresh card that RESOLVED its own context (scan-built)
        // is newer truth on both counts, so it inherits neither.
        if (fresh.ContextTokens <= 0 && !fresh.ContextCompacted)
        {
            if (prior.ContextTokens > 0) fresh.ContextTokens = prior.ContextTokens;
            if (prior.ContextCompacted) fresh.ContextCompacted = true;
        }
        // Carry the TODO list across a status swap (e.g. amber→WORKING, which skips the async re-read) so the
        // progress block doesn't blank until the next 15s meta sweep repopulates it.
        if ((fresh.Todos == null || fresh.Todos.Count == 0) && prior.Todos != null && prior.Todos.Count > 0)
            fresh.Todos = prior.Todos;
        // And the model — a status swap must never drop the heavy-model state off the model toggle. FromHook knows no
        // model but a PostModelSwitch's (ApplyHookModel adds a hook model), so without this every live event would
        // un-red a heavy session until the next meta read landed.
        if (fresh.Model.Length == 0 && prior.Model.Length > 0)
            fresh.Model = prior.Model;
        // And your last prompt — the you block never blanks on a status swap.
        if (string.IsNullOrEmpty(fresh.YouText) && !string.IsNullOrEmpty(prior.YouText))
            fresh.YouText = prior.YouText;
    }

    /// <summary>
    /// The ONE card order, shared by this store and the startup scan (<see cref="Sessions.ScanActive"/>). Group by
    /// PROJECT — the project list's order first (passed in — ProjectList.Order), then the rest alphabetically by name — and within a project
    /// by session TITLE, untitled cards last, session id breaking a duplicate-name tie. NO status / urgency /
    /// recency term: any status-based ordering moves a card the instant its status changes (the whole "cards keep
    /// jumping" complaint); a title changes only on a rename or a first resolve, so a card moves only then.
    /// </summary>
    internal static List<AgentEvent> CardOrder(IEnumerable<AgentEvent> cards, Func<string, int> order) => cards
        .OrderBy(e => order(e.ProjectName))
        .ThenBy(e => e.ProjectName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(e => string.IsNullOrWhiteSpace(e.Title))
        .ThenBy(e => e.Title ?? "", StringComparer.OrdinalIgnoreCase)
        .ThenBy(e => e.SessionId, StringComparer.Ordinal)
        .ToList();

    /// <summary>Reorder by <see cref="CardOrder"/>, preserving card identity (Move, no rebuild). Also the call after
    /// patching a card's Title off-thread — a resolved or renamed title moves the card and may form a duplicate.</summary>
    public void Resort() { ResortInternal(); Fire(); }

    private void ResortInternal()
    {
        var sorted = CardOrder(Events, _order);
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Events.IndexOf(sorted[i]);
            if (cur != i) Events.Move(cur, i);
        }
    }

    /// <summary>The one mutation epilogue: recompute the duplicate-name flags, then raise <see cref="Changed"/>.
    /// Every collection mutation routes through here.</summary>
    private void Fire()
    {
        RecomputeDuplicates();
        Changed?.Invoke();
    }

    /// <summary>
    /// Flag every card that shares its (non-empty) title with another live card. CDP actions are keyed on the
    /// session id so the APP can't confuse them — but the USER can: two identically-named cards are how a
    /// prompt meant for one agent gets typed into the other, so the pill prompts a rename. O(n) over a tiny
    /// list; safe to run on every mutation.
    /// </summary>
    private void RecomputeDuplicates()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Events)
            if (!string.IsNullOrWhiteSpace(e.Title))
                counts[e.Title] = counts.TryGetValue(e.Title, out var c) ? c + 1 : 1;

        foreach (var e in Events)
            e.DuplicateName = !string.IsNullOrWhiteSpace(e.Title)
                && counts.TryGetValue(e.Title, out var c) && c > 1;
    }
}
