using System.IO;

namespace Mastermind;

/// <summary>The ONE place a prompt file moves or a CDP dispatch fires — so "resolve target first, then act once"
/// and the fail-safe move rules (no-clobber, vanished-source, atomic-guard) live in one spot.
/// Pure file moves + delegations to CdpInjector; the view calls these, never the store or the scan.</summary>
internal sealed class PromptDispatcher
{
    private readonly LogStore _log;
    private readonly Action<string> _onNewSessionAutoAllowed;   // patch the live card's auto-allow checkbox after a new-session dispatch (race guard)
    public PromptDispatcher(LogStore log, Action<string> onNewSessionAutoAllowed = null) { _log = log; _onNewSessionAutoAllowed = onNewSessionAutoAllowed; }

    /// <summary>The windows' shells, set by App once built — the port-off dispatch types through them.</summary>
    internal ClientHub Clients { get; set; }

    /// <summary>source outbox/{file} → dest inbox/{file} (config's handoff folders). Atomic same-volume File.Move is the
    /// race guard. Refuses to clobber, and refuses a destination project whose folder is gone — Move would recreate an
    /// inbox there that nothing reads, so the handoff stays in the outbox. Tolerates a vanished source. Returns the new
    /// path or null.</summary>
    public string Transport(PromptItem it)
    {
        if (string.IsNullOrEmpty(it.DestRoot)) return null;
        if (!Directory.Exists(it.DestRoot))
        { _log.Add(LogKind.Warn, $"prompt transport refused · {it.FileName} — {it.DestRoot} no longer exists"); return null; }
        return Move(it, PromptRoute.InboxDir(it.DestRoot), "transport");
    }

    private string Move(PromptItem it, string destDir, string what)
    {
        try
        {
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, it.FileName);
            // The pre-check must ask the question in the entry's OWN shape: File.Exists on a directory path
            // returns false, so a bundle collision would slip past this and surface as a raw IO warning further
            // down instead of the collision line the operator is meant to read.
            if (it.IsDirectory ? Directory.Exists(dest) : File.Exists(dest))
            { _log.Add(LogKind.Warn, $"prompt {what} refused · {it.FileName} — a same-named entry already waits (collision)"); return null; }
            // 2-arg File.Move (NO overwrite) is load-bearing for no-clobber: if a same-named file appears between the
            // Exists check above and here (TOCTOU), File.Move THROWS IOException rather than clobbering. Never
            // switch to the 3-arg File.Move(src, dest, overwrite: true) — it silently replaces an unconsumed prompt.
            // Directory.Move carries the same contract: onto an existing directory it throws IOException — it never
            // nests the source inside, as a shell `mv` or `Move-Item` would. It also refuses outright ACROSS VOLUMES,
            // where File.Move falls back to copy-then-delete: a partial copy shows at the destination meanwhile, and a
            // delete that fails — the source held open — still reports success and leaves the source behind. Every
            // monitored root sits on one drive today, so a rename transport holds.
            if (it.IsDirectory) Directory.Move(it.FullPath, dest);
            else File.Move(it.FullPath, dest);   // same-volume atomic rename; throws if dest exists (the real no-clobber guard)
            _log.Add(LogKind.Info, $"prompt {what} · {it.RouteText} — {it.FileName}");
            return dest;
        }
        // Vanished source: a source deleted by hand may remove the file OR its parent dir → tolerate both silently.
        // UnauthorizedAccessException does NOT derive from IOException, so it must be caught explicitly (finding G).
        catch (FileNotFoundException) { _log.Add(LogKind.Drop, $"prompt {what} skipped · {it.FileName} — source gone (withdrawn)"); return null; }
        catch (DirectoryNotFoundException) { _log.Add(LogKind.Drop, $"prompt {what} skipped · {it.FileName} — source dir gone (withdrawn)"); return null; }
        catch (UnauthorizedAccessException ex) { _log.Add(LogKind.Warn, $"prompt {what} failed · {it.FileName} — access denied: {ex.Message}"); return null; }
        catch (IOException ex) { _log.Add(LogKind.Warn, $"prompt {what} failed · {it.FileName} — {ex.Message}"); return null; }
    }

    /// <summary>Dispatch to a NEW agent: move inbox→processing (so the injected command points at the final
    /// path), fire CDP, and ROLL BACK to the inbox on any non-Sent outcome so the row returns to Waiting and can
    /// be retried (finding A — PortOff is a common, expected condition and must not strand the prompt). With the port
    /// off and the windows' shells connected it types the text into a new session instead (TypeIntoNew).</summary>
    public async Task DispatchNew(PromptItem it)
    {
        var moved = Move(it, PromptRoute.ProcessingDir(it.DestRoot), "dispatch");
        if (moved == null) return;
        var key = Paths.Key(moved);
        PromptOwnerStore.SetPending(key);   // protect the row (no delete) until the new session's id resolves or rollback clears it
        var cmd = PromptRoute.DispatchText(it.FileName);   // config's dispatch text — by default the processor skill, which a hand-typed draft may precede
        var (r, sid) = await CdpInjector.TryCreateAndSend(Paths.Key(it.DestRoot), cmd);
        if (r == CdpInjector.CreateResult.PortOff && Clients != null) { await TypeIntoNew(it, moved, key, cmd); return; }
        if (r == CdpInjector.CreateResult.Sent)
        {
            if (!string.IsNullOrEmpty(sid)) Own(key, sid, it.DestRoot, typed: false);
            // else leave PENDING (id never landed): GC clears the owner entry when the file goes, and auto-allow
            // can't be set without the id (the new agent prompts normally until its first turn, unavoidable here)
        }
        else RollBack(moved, it);   // back to Waiting → no owner
        LogCdp(it, r, "new agent");
    }

    /// <summary>A new session's owner link — typed when the dispatch only typed its text (B20) — and config's
    /// newDispatchAutoAllow: a deliberately-dispatched agent runs unattended — started auto-allow ON, never into a hidden
    /// project (AutoAllowStore.Arm); a live card reflects it now, else its card seeds ON at creation.</summary>
    private void Own(string key, string sid, string destRoot, bool typed)
    {
        PromptOwnerStore.Set(key, sid, typed, spawned: true);
        if (Config.Current.NewDispatchAutoAllow && AutoAllowStore.Arm(sid, destRoot))
            _onNewSessionAutoAllowed?.Invoke(sid);
    }

    /// <summary>A tick handing a typed `new` dispatch to a session other than the one the 20 s search took — the search
    /// found none, or took another fresh session in that window (PromptStore): config's newDispatchAutoAllow moves to
    /// the session that sent it, off the one taken. UI thread.</summary>
    public void MoveAutoAllow(string from, string to, string destRoot)
    {
        if (!Config.Current.NewDispatchAutoAllow) return;
        if (from != PromptOwnerStore.Pending) AutoAllowStore.Set(from, false);
        AutoAllowStore.Arm(to, destRoot);
        _onNewSessionAutoAllowed?.Invoke(to);
    }

    private static readonly TimeSpan NewSessionWait = TimeSpan.FromSeconds(20);
    private static readonly HashSet<string> _typingInto = new(StringComparer.Ordinal);   // projects a port-off `new` is resolving — UI thread

    /// <summary>The debug port off: a new session opened in the one window whose first folder is the receiving
    /// project, the dispatch text typed in, unsent, and that window raised — the person presses Enter (user decision).
    /// The record is typed (B20), so the row offers back to inbox until that session sends it. Its owner is the one new
    /// session <see cref="NewSessionIn"/> finds — the editor.open answer carries no id; one that never shows leaves the
    /// typed PENDING record, which a later tick hands to the session whose message names the file (PromptStore). One such
    /// dispatch per project at a time, so two cannot claim one new session. No window,
    /// or one that refused, rolls back; one that did not answer in time may still type it, so the file stays.</summary>
    private async Task TypeIntoNew(PromptItem it, string moved, string key, string cmd)
    {
        var project = Paths.Key(it.DestRoot);
        int host = Clients.HostFor(it.DestRoot);
        var refused = host == 0 ? $"no single VS Code window has {Paths.Leaf(it.DestRoot)} as its first folder"
            : !_typingInto.Add(project) ? $"a port-off dispatch to {Paths.Leaf(it.DestRoot)} is still finding its session — retry in a moment"
            : null;
        if (refused != null) { RestorePortOff(moved, it, refused); return; }
        try
        {
            PromptOwnerStore.Set(key, PromptOwnerStore.Pending, typed: true, spawned: true);
            var before = LiveSessions.RunningIn(it.DestRoot).Select(r => r.SessionId).ToHashSet();
            Platform.EditorWindows.GrantForeground();   // the panel's click holds the foreground — the raise below needs it
            var open = Clients.OpenNew(host, cmd);
            var answer = open == null ? "its window disconnected" : await open;
            if (ClientHub.Refused(answer)) { RestorePortOff(moved, it, answer); return; }
            Raise(it.DestRoot);
            // A tick takes the record off PENDING once the session that sent it shows — that one has a transcript, so the
            // search can only find another fresh session after it — and a roll-back or back to inbox drops the record.
            bool StillPending() => PromptOwnerStore.Find(key)?.Sid == PromptOwnerStore.Pending;
            string sid = null;
            var end = DateTime.UtcNow + NewSessionWait;
            while (sid == null && DateTime.UtcNow < end && StillPending())
            {
                await Task.Delay(500);
                sid = await Task.Run(() => NewSessionIn(it.DestRoot, host, before));
            }
            if (!StillPending()) return;
            if (sid != null)
            {
                Own(key, sid, it.DestRoot, typed: true);
                _log.Add(LogKind.Info, $"prompt {it.FileName} : typed into a new {Paths.Leaf(it.DestRoot)} session — press Enter there to send");
            }
            else _log.Add(LogKind.Warn, $"prompt {it.FileName} : typed into a new {Paths.Leaf(it.DestRoot)} session whose id never showed — back to inbox if you did not send it");
        }
        finally { _typingInto.Remove(project); }
    }

    /// <summary>A port-off dispatch its window refused, or no window could take: the file back to waiting, and why in
    /// the log.</summary>
    private void RestorePortOff(string moved, PromptItem it, string why)
    {
        RollBack(moved, it);
        _log.Add(LogKind.Warn, $"prompt {it.FileName} : debug port off, and {why} — restored to waiting");
    }

    /// <summary>The port-off `new` dispatch's session: the one running claude in the project that was not there before,
    /// started anywhere below that window's extension host — a process wrapper between them hides none — with no
    /// transcript yet — a session reopened by restore, or one already chatting, has one (B12 measures that a new one has
    /// none). Null while none, or several, qualify.</summary>
    private static string NewSessionIn(string root, int host, HashSet<string> before)
    {
        Dictionary<string, int> underHost;
        try { underHost = HealthReader.SessionHosts(new[] { host }).HostBySession; }
        catch { return null; }   // the window gone, or a failed read: none this round
        var fresh = LiveSessions.RunningIn(root)
            .Where(r => !before.Contains(r.SessionId) && underHost.ContainsKey(r.SessionId) && !Sessions.HasTranscript(r.SessionId, root))
            .Select(r => r.SessionId).ToList();
        return fresh.Count == 1 ? fresh[0] : null;
    }

    /// <summary>The receiving window raised after a port-off dispatch — the person presses Enter there — by its
    /// project's title, as the health check's open falls back to; a raise that misses says so in the log.</summary>
    private void Raise(string root)
    {
        var r = Platform.EditorWindows.RaiseProject(root);
        if (r != RaiseResult.Raised) _log.Add(LogKind.Warn, MainWindow.RaiseNote(r, Paths.Leaf(root)));
    }

    /// <summary>Dispatch to an OPEN agent: move inbox→processing, inject via the existing TrySend, roll back
    /// on any non-Sent outcome (finding A). The owner link is written BEFORE the await — TrySend can take
    /// seconds (webview iteration at CDP timeouts), and a poll tick landing in that window would otherwise see
    /// a processing file with NO owner ⇒ flash it ORPHAN and offer `delete` on a file mid-dispatch (the store's
    /// "no entry means nobody's" invariant only holds if every writer links before it can be observed).</summary>
    public async Task DispatchTo(PromptItem it, string sessionId, string sessionTitle)
    {
        var moved = Move(it, PromptRoute.ProcessingDir(it.DestRoot), "dispatch");
        if (moved == null) return;
        var key = Paths.Key(moved);
        PromptOwnerStore.Set(key, sessionId);   // id in hand — link BEFORE the await (no orphan flash mid-dispatch)
        var cmd = PromptRoute.DispatchText(it.FileName);   // config's dispatch text — by default the processor skill, which a hand-typed draft may precede
        var r = await CdpInjector.TrySend(sessionId, sessionTitle, cmd, cwd: it.DestRoot);   // dest project narrows the sidebar probe
        if (r == CdpInjector.Result.PortOff && Clients?.PasteInto(sessionId, cmd, sessionTitle) is { } paste)
        {
            // The debug port off: the session's own window switches to it with nothing typed — a prefill would replace
            // the person's unsent draft (B12) — puts the dispatch text on the clipboard with a notice, and is raised;
            // the person pastes it and presses Enter (user decision). The record turns typed (B20), so the row offers
            // back to inbox until that session sends it. Only a refusal rolls back: a window that did not answer in time
            // may still do it.
            PromptOwnerStore.Set(key, sessionId, typed: true);
            Platform.EditorWindows.GrantForeground();
            var error = await paste;
            if (ClientHub.Refused(error)) RestorePortOff(moved, it, error);
            else
            {
                Raise(it.DestRoot);
                _log.Add(LogKind.Info, $"prompt {it.FileName} : on the clipboard for {sessionTitle} — paste it there and press Enter");
            }
            return;
        }
        if (r != CdpInjector.Result.Sent) RollBack(moved, it);   // back to Waiting → no owner
        LogCdp(it, r == CdpInjector.Result.Sent ? CdpInjector.CreateResult.Sent
                 : r == CdpInjector.Result.PortOff ? CdpInjector.CreateResult.PortOff
                 : CdpInjector.CreateResult.NotReached, "send");
    }

    /// <summary>Dispatch didn't reach an agent → move the file BACK processing→inbox so the row returns to
    /// Waiting (finding A), and drop its owner record. Best-effort; the physical file is the state. If the roll-back
    /// itself fails the file stays in processing and the `delete` button is the fallback.</summary>
    private void RollBack(string inprocessPath, PromptItem it)
    {
        try
        {
            var back = Path.Combine(PromptRoute.InboxDir(it.DestRoot), it.FileName);
            // Both halves of the pair take the entry's own shape. Asking File.Exists about a bundle answers
            // false for BOTH operands, which reads as "nothing there and nothing to move" — a roll-back that
            // silently does nothing and strands the row in processing.
            if (it.IsDirectory)
            { if (!Directory.Exists(back) && Directory.Exists(inprocessPath)) Directory.Move(inprocessPath, back); }
            else if (!File.Exists(back) && File.Exists(inprocessPath)) File.Move(inprocessPath, back);
        }
        catch { /* leave it in processing; delete is the fallback */ }
        PromptOwnerStore.Remove(Paths.Key(inprocessPath));
    }

    /// <summary>Manual delete of a stuck processing entry — to the RECYCLE BIN where Windows can (WinRecycleBin). A bundle takes
    /// DeleteDirectory, which recycles the whole tree; the file call would throw on a directory path and the
    /// row would sit there undeletable. A consumed bundle left behind costs real disk, unlike a prompt file,
    /// so this is the escape hatch that actually gets used.</summary>
    public void DeleteInProcess(PromptItem it)
    {
        try { Platform.RecycleBin.Delete(it.FullPath, it.IsDirectory);
              _log.Add(LogKind.Info, $"prompt deleted · {it.FileName}"); }
        catch (Exception ex) { _log.Add(LogKind.Warn, $"prompt delete failed · {it.FileName} — {ex.Message}"); }
    }

    private static readonly TimeSpan StagingAge = TimeSpan.FromDays(3);   // user decision
    // One level at a time, so a link is never descended — its target is not the draft's. Every entry counts, hidden
    // and system included; an unreadable folder throws, so the entry holding it is kept.
    private static readonly EnumerationOptions OneLevel = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    /// <summary>The start-time staging clean, once per run (user decision): in each address-book project on a fixed
    /// drive, every top-level entry of <see cref="PromptRoute.StagingDir"/> untouched for <see cref="StagingAge"/> is
    /// deleted silently, to the recycle bin where Windows can — a folder judged by the newest write anywhere inside it,
    /// since Windows leaves a folder's own time alone when a file inside changes. A link is never touched, and a staging
    /// folder that is itself a link, or sits in a linked .mastermind/, is skipped: what it reaches is not this
    /// project's. A link is told by its target, never the reparse-point attribute, which cloud-sync placeholders can
    /// carry. A failure goes to the crash log and waits for the next start; one activity line per project cleaned. An
    /// address book that read short skips this run's clean rather than running it later.</summary>
    public void CleanStaging(IReadOnlyList<ProjectEntry> projects, bool listFailed)
    {
        if (listFailed) { _log.Add(LogKind.Warn, "staging · not cleaned this run — the project list could not be read"); return; }
        var cutoff = DateTime.UtcNow - StagingAge;
        foreach (var proj in projects)
        {
            var root = proj?.Root;
            if (string.IsNullOrEmpty(root)) continue;
            int cleaned = 0;
            try
            {
                var dir = PromptRoute.StagingDir(root);
                if (!Directory.Exists(dir) || !OnFixedDrive(dir) || IsLink(dir) || IsLink(Path.GetDirectoryName(dir)!)) continue;
                foreach (var entry in new DirectoryInfo(dir).GetFileSystemInfos())   // eager — the loop deletes as it goes
                {
                    try
                    {
                        entry.Refresh();   // the snapshot's times predate the walks and deletes before this entry
                        if (entry.LinkTarget != null || NewestWrite(entry) >= cutoff) continue;
                        Platform.RecycleBin.DeleteSilently(entry.FullName);
                        cleaned++;
                    }
                    catch (Exception ex) { Diag.Warn("staging clean", ex); }
                }
            }
            catch (Exception ex) { Diag.Warn("staging clean", ex); }
            if (cleaned > 0)
                _log.Add(LogKind.Info, $"staging · {Paths.Leaf(root)}: {cleaned} untouched for three days, deleted (to the recycle bin where Windows can)");
        }
    }

    private static DateTime NewestWrite(FileSystemInfo entry)
    {
        var newest = entry.LastWriteTimeUtc;
        if (entry is DirectoryInfo d && d.LinkTarget == null)
            foreach (var inner in d.EnumerateFileSystemInfos("*", OneLevel))
            {
                var t = NewestWrite(inner);
                if (t > newest) newest = t;
            }
        return newest;
    }

    private static bool IsLink(string dir) => new DirectoryInfo(dir).LinkTarget != null;

    private static bool OnFixedDrive(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed; }
        catch { return false; }   // a UNC root is no drive letter
    }

    /// <summary>Back to inbox: a processing entry with no owner record — a file another machine dispatched, brought
    /// here by git — moves into the inbox of the project it sits in, to wait for a dispatch here. Re-checked on the
    /// click, since the row is up to a tick old. The project's own inbox, never the DST's: an entry whose token stopped
    /// resolving still goes home. Unlike RollBack — the silent, best-effort undo inside a failed dispatch — a click is
    /// answered in the log, collision included.</summary>
    public void BackToInbox(PromptItem it)
    {
        var owner = PromptOwnerStore.Find(it.Key);
        bool typed = owner?.Typed == true;   // a typed dispatch nobody sent goes back too
        if (owner != null && !typed) { _log.Add(LogKind.Warn, $"prompt back to inbox refused · {it.FileName} — an agent holds it"); return; }
        if (Move(it, PromptRoute.InboxDir(it.SourceRoot), "back to inbox") != null && typed)
            _log.Add(LogKind.Info, $"prompt {it.FileName} : if its text sits unsent in that session, clear it there");
    }

    private void LogCdp(PromptItem it, CdpInjector.CreateResult r, string via)
    {
        // On any non-Sent the file was rolled back to the inbox, so the row is Waiting again and the affordance
        // to retry IS present — the message says so instead of pointing at a row that no longer exists (finding J).
        var (k, note) = r switch
        {
            CdpInjector.CreateResult.Sent       => (LogKind.Info, $"dispatched → {it.Dst} ({via})"),
            CdpInjector.CreateResult.PortOff    => (LogKind.Warn, "debug port off — restored to waiting; enable the CDP port and retry"),
            CdpInjector.CreateResult.NotReached => (LogKind.Warn, "couldn't reach a Claude sidebar for the destination — restored to waiting; open its window + Claude panel and retry"),
            _                                   => (LogKind.Warn, "not dispatched — restored to waiting"),
        };
        _log.Add(k, $"prompt {it.FileName} : {note}");
    }
}
