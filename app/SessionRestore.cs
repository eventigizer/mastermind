using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Recorded = (string Id, string Cwd, bool AutoAllow);

namespace Mastermind;

/// <summary>
/// Session restore (phase 3d): which sessions each VS Code window had, so a window that comes back — a reload, a VS
/// Code restart, a reboot — gets them back. The record is keyed by the window's folder set, since a reload or a
/// restart brings the window back under a new extension host, and lives in <see cref="ClaudePaths.RestoreJson"/>, so
/// it outlives a Mastermind restart and a reboot. Every 8 s <see cref="Track"/> lists each connected window's
/// sessions (<see cref="HealthReader.SessionHosts"/>). A session's auto-allow is taken when it is first recorded and
/// then follows <see cref="AutoAllowStore.Toggled"/> — never a SessionEnd's clear or the startup sweep. A session
/// leaves the record when it stopped running while its own window — the same host — was still connected a tick
/// later: a reload's process deaths can land on a tick before the window's socket closes. A window that comes back
/// (<see cref="Connected"/>), or reloads its panels (<see cref="HealthReloaded"/>), gets its sessions reopened by
/// <see cref="Restore"/>. UI thread; the process and file reads run off it.
/// </summary>
internal sealed class SessionRestore
{
    /// <summary>One recorded session: its id, its launch folder (the transcript lookup's hint) and its auto-allow.
    /// Host, Misses and LateArm are this run's alone — the host it last ran under, the ticks it has been missing from
    /// that host while the host stayed connected, and whether a reopen still starting after its wait owes its
    /// auto-allow.</summary>
    internal sealed class RecordedSession
    {
        public string Id { get; set; } = "";
        public string Cwd { get; set; } = "";
        public bool AutoAllow { get; set; }
        [JsonIgnore] public int Host { get; set; }
        [JsonIgnore] public int Misses { get; set; }
        [JsonIgnore] public bool LateArm { get; set; }
    }

    /// <summary>One window: its folders as its shell reported them, the extension host it last ran under — its pid and
    /// its start, in UTC, so a hello from that same host is told from a window that came back — and its
    /// sessions.</summary>
    internal sealed class WindowEntry
    {
        public List<string> Folders { get; set; } = new();
        public int HostPid { get; set; }
        public DateTime? HostStarted { get; set; }
        public List<RecordedSession> Sessions { get; set; } = new();
    }

    /// <summary>What a restore found once its window settled: the sessions to open, the ones that came back on their
    /// own under the window, the ones that leave the record, or why nothing may open.</summary>
    private sealed record Plan(List<Recorded> Open, List<Recorded> Back, HashSet<string> Drop, string Error);

    // How long a window is left to settle before its sessions open: the one its sidebar showed comes back on its own
    // under a new process, and an open landing first only switches to it. Kept over 13 s: check_restore.js
    // reads the window 13 s past its logged connect, before any open.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);
    // How long an opened session is given to show in the registry before it counts as reopened and gets its
    // auto-allow back; one still starting then waits for a tick to find it.
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(30);

    private readonly ClientHub _clients;
    private readonly Action _syncAutoAllow;
    private readonly Action<LogKind, string> _log;
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, WindowEntry> _record;
    private readonly bool _frozen;                        // restore.json exists and did not read: never written over this run
    private readonly HashSet<int> _holding = new();       // hosts with a restore in flight — nothing drops meanwhile
    private string _saved;                                // the record as last written
    private bool _tracking;                               // a tick's process read still running skips the next

    public SessionRestore(ClientHub clients, Action syncAutoAllow, Action<LogKind, string> log, Dispatcher ui)
    {
        _clients = clients;
        _syncAutoAllow = syncAutoAllow;
        _log = log;
        _ui = ui;
        bool exists = File.Exists(ClaudePaths.RestoreJson);
        var loaded = Json.Load<Dictionary<string, WindowEntry>>(ClaudePaths.RestoreJson);
        if (exists && loaded == null)
        {
            _frozen = true;
            _log(LogKind.Warn, "restore · restore.json could not be read — it is kept as it is, and nothing is saved over it this run");
        }
        loaded ??= new();
        _saved = JsonSerializer.Serialize(loaded);
        // A window whose every folder is gone from a drive that is there never opens again — a check's throwaway
        // folder once /close reaps it, a deleted checkout. A drive not mounted yet keeps its windows.
        _record = loaded.Where(w => w.Value?.Folders is { Count: > 0 } && w.Value.Sessions != null && !w.Value.Folders.All(Gone))
                        .ToDictionary(w => w.Key, w => w.Value);
        Save();
        AutoAllowStore.Toggled += (id, on) => _ui.BeginInvoke(new Action(() => OnToggled(id, on)));
    }

    private static bool Gone(string folder)
    {
        var root = Path.GetPathRoot(folder);
        return !string.IsNullOrEmpty(root) && Directory.Exists(root) && !Directory.Exists(folder);
    }

    /// <summary>A window's identity across extension hosts: its folders' keys, sorted.</summary>
    private static string KeyOf(IEnumerable<string> folders) =>
        string.Join("|", folders.Select(Paths.Key).Where(k => k.Length > 0).OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>The 8 s tick: each connected window's sessions and its host's start into the record —
    /// <paramref name="windows"/> is <see cref="ClientHub.Windows"/>, the windows with a local folder. UI thread; the
    /// reads run off it.</summary>
    public void Track(List<(int HostPid, string Name, List<string> Folders)> windows)
    {
        if (_tracking || windows.Count == 0) return;
        _tracking = true;
        var hosts = windows.Select(w => w.HostPid).Distinct().ToList();
        _ = Task.Run(() =>
        {
            Dictionary<string, int> hostBySession = null;
            var starts = new Dictionary<int, DateTime?>();
            try
            {
                hostBySession = HealthReader.SessionHosts(hosts).HostBySession;
                foreach (int h in hosts) starts[h] = Platform.Processes.StartedAt(h);
            }
            catch (Exception ex) { Diag.Warn("restore track", ex); }
            _ui.BeginInvoke(new Action(() =>
            {
                _tracking = false;
                if (hostBySession != null) Apply(windows, hostBySession, starts);
            }));
        });
    }

    private void Apply(List<(int HostPid, string Name, List<string> Folders)> windows, Dictionary<string, int> hostBySession,
                       Dictionary<int, DateTime?> starts)
    {
        bool armed = false;
        foreach (var (host, _, folders) in windows)
        {
            string key = KeyOf(folders);
            var here = hostBySession.Where(s => s.Value == host).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            if (!_record.TryGetValue(key, out var window))
            {
                if (here.Count == 0) continue;
                _record[key] = window = new WindowEntry();
            }
            window.Folders = folders.ToList();
            window.HostPid = host;                                   // the host a later hello is matched against
            window.HostStarted = starts.GetValueOrDefault(host);
            foreach (var id in here)
            {
                var s = window.Sessions.FirstOrDefault(x => x.Id == id);
                if (s == null) window.Sessions.Add(s = new RecordedSession { Id = id, AutoAllow = AutoAllowStore.IsOn(id) });
                else if (s.LateArm)
                {
                    // Reopened, and started after its wait: its auto-allow comes back now.
                    s.LateArm = false;
                    armed |= s.AutoAllow && AutoAllowStore.Arm(s.Id, s.Cwd);
                }
                s.Cwd = LiveSessions.CwdOf(id) ?? s.Cwd;
                s.Host = host;
                s.Misses = 0;
            }
            // Gone from its own window while that window stays connected: dropped on the second tick — never
            // while a restore of the window is in flight.
            if (!_holding.Contains(host))
                foreach (var gone in window.Sessions.Where(x => x.Host == host && !here.Contains(x.Id))) gone.Misses++;
            window.Sessions.RemoveAll(x => x.Misses >= 2);
            if (window.Sessions.Count == 0) _record.Remove(key);
        }
        if (armed) _syncAutoAllow();
        Save();
    }

    /// <summary>A person's or a dispatch's auto-allow change, into every entry holding the session. UI thread.</summary>
    private void OnToggled(string sessionId, bool on)
    {
        foreach (var s in _record.Values.SelectMany(w => w.Sessions).Where(s => s.Id == sessionId)) s.AutoAllow = on;
        Save();
    }

    /// <summary>A window's hello (<see cref="ClientHub.WindowConnected"/>). A host other than the one the record last
    /// saw the window under is a window that came back — a reload, a VS Code restart, a reboot, whichever of
    /// Mastermind and VS Code started first — and restores. The same host is a window Mastermind's own restart, or
    /// a shell's reconnect, found running: nothing opens, and each recorded session takes that host, so one not
    /// running under it drops on the tick rule. UI thread.</summary>
    public void Connected(int hostPid, List<string> folders, string name)
    {
        try
        {
            if (!_record.TryGetValue(KeyOf(folders), out var window)) return;
            if (!SameHost(window, hostPid, Platform.Processes.StartedAt(hostPid)))
            {
                _ = Restore(hostPid, folders, name);
                return;
            }
            foreach (var s in window.Sessions) s.Host = hostPid;
        }
        catch (Exception ex) { Diag.Warn("restore connected", ex); }
    }

    /// <summary>Whether a hello's host is the one the record last saw the window under: its pid, and its start within
    /// 1 s — the pid alone when neither start reads. Never a clock comparison: the fall-back hour and a clock step
    /// flip one.</summary>
    private static bool SameHost(WindowEntry window, int hostPid, DateTime? started) =>
        window.HostPid == hostPid && (started, window.HostStarted) switch
        {
            (null, null) => true,
            ({ } now, { } then) => Math.Abs((now - then).TotalSeconds) <= 1,
            _ => false,
        };

    /// <summary>A VS Code health reload in a window (<see cref="ClientHub.HealthReloaded"/>): Reload panels ends the
    /// window's sessions with its host still connected, so it restores like a reload (user decision); a Reload window
    /// finds its old host gone once it settles, and the new host's hello restores instead. UI thread.</summary>
    public void HealthReloaded(int hostPid, List<string> folders, string name) => _ = Restore(hostPid, folders, name);

    /// <summary>One window's restore, its sessions taken as the trigger came. Once the window settles, each that is not
    /// running anywhere, not archived (a strict fresh read — nothing opens when it fails), still has its transcript
    /// and was active within Claude Code's auto-archive window (user decision) opens by id in that window, one at a
    /// time, where Claude Code's preferred location puts it, each waited on until it starts; each that had
    /// auto-allow gets it back, never in a hidden project — one still starting after the wait gets it when a tick
    /// finds it running, and drops on the tick rule if it never does. The one session that came back on its own under
    /// the window then opens last, so the sidebar lands where it was. Archived, inactive and transcript-less sessions,
    /// and any the window refused to open, leave the record. One activity-log line when any reopened, one Warn — a
    /// toast — naming the refused; a window gone meanwhile restores nothing more. One restore per host at a time.
    /// Catches from its first line: its callers discard the task, which would keep a throw unlogged. UI
    /// thread.</summary>
    private async Task Restore(int hostPid, List<string> folders, string name)
    {
        bool held = false;
        try
        {
            string key = KeyOf(folders);
            if (!_record.TryGetValue(key, out var window) || window.Sessions.Count == 0 || !(held = _holding.Add(hostPid))) return;
            var recorded = window.Sessions.Select(s => (s.Id, s.Cwd, s.AutoAllow)).ToList();
            await Task.Delay(Settle);
            if (!_clients.IsConnected(hostPid)) return;
            var plan = await Task.Run(() => PlanFor(hostPid, recorded));
            if (plan.Error != null)
            {
                _log(LogKind.Warn, $"restore · {name}: {plan.Error} — nothing reopened");
                return;
            }
            Drop(key, plan.Drop);
            int opened = 0;
            bool ran = false, armed = false, gone = false;
            var failed = new List<string>();
            foreach (var s in plan.Open)
            {
                var answer = _clients.OpenSessionIn(hostPid, s.Id);
                if (answer == null) { gone = true; break; }
                string error = await answer;
                if (!_clients.IsConnected(hostPid)) { gone = true; break; }
                if (ClientHub.Refused(error))
                {
                    // The window refused it: it leaves the record, named in the one Warn. A window that did not answer in
                    // time may still be opening it, so that one goes on as an open still starting.
                    failed.Add($"{Short(s.Id)} ({error})");
                    Drop(key, new[] { s.Id });
                    continue;
                }
                ran = true;
                if (await Started(s.Id))
                {
                    opened++;
                    armed |= s.AutoAllow && AutoAllowStore.Arm(s.Id, s.Cwd);
                    continue;
                }
                // Still starting: it stays in the record under this host, so a tick that finds it running gives its
                // auto-allow back and one that never starts drops on the tick rule.
                if (_record.TryGetValue(key, out var entry) && entry.Sessions.FirstOrDefault(x => x.Id == s.Id) is { } rec)
                {
                    rec.Host = hostPid;
                    rec.LateArm = s.AutoAllow;
                }
            }
            if (!gone)
            {
                // The reload's SessionEnd cleared the auto-allow of the sessions that came back on their own too.
                foreach (var s in plan.Back) armed |= s.AutoAllow && AutoAllowStore.Arm(s.Id, s.Cwd);
                // Every open the window ran moved its sidebar, a session still starting included.
                if (ran && plan.Back.Count == 1 && _clients.OpenSessionIn(hostPid, plan.Back[0].Id) is { } last) await last;
            }
            if (armed) _syncAutoAllow();
            if (opened > 0) _log(LogKind.Info, $"restore · {name}: reopened {opened}");
            if (failed.Count > 0) _log(LogKind.Warn, $"restore · {name}: {failed.Count} did not reopen — {string.Join(", ", failed)}");
        }
        catch (Exception ex)
        {
            _log(LogKind.Warn, $"restore · {name} failed ({ex.Message})");
            Diag.Warn("restore", ex);
        }
        finally
        {
            if (held) _holding.Remove(hostPid);
        }
    }

    /// <summary>The restore's reads, off the UI thread: the sessions under this host and every running one, off one
    /// registry read — a session running in another window or a terminal is neither opened here nor dropped — then,
    /// only when a recorded session runs nowhere, the projects folder, the archived set (a state.vscdb copy per DB, so
    /// a window whose sessions all run costs none), the auto-archive line and each transcript's newest message.</summary>
    private static Plan PlanFor(int hostPid, List<Recorded> recorded)
    {
        var (underHost, live) = HealthReader.SessionHosts(new[] { hostPid });
        var back = recorded.Where(s => underHost.ContainsKey(s.Id)).ToList();
        var open = new List<Recorded>();
        var drop = new HashSet<string>(StringComparer.Ordinal);
        var candidates = recorded.Where(s => !live.Contains(s.Id)).ToList();
        if (candidates.Count == 0) return new Plan(open, back, drop, null);
        if (!Directory.Exists(ClaudePaths.Projects)) return new Plan(open, back, drop, "Claude Code's projects folder is missing");
        var archived = VsCodeState.HiddenSessionIdsStrict(hostPid);
        if (archived == null) return new Plan(open, back, drop, "the archived list could not be read");
        int days = VsCodeState.ArchiveInactiveDays(hostPid);
        foreach (var s in candidates)
        {
            var last = Sessions.LastMessageAt(s.Id, s.Cwd);
            bool inactive = days > 0 && last is { } at && DateTime.UtcNow - at > TimeSpan.FromDays(days);
            if (archived.Contains(s.Id) || last == null || inactive) drop.Add(s.Id);
            else open.Add(s);
        }
        return new Plan(open, back, drop, null);
    }

    /// <summary>Whether an opened session shows in the registry within <see cref="StartWait"/> — its answer only said
    /// the window ran the command. Uses the UTC clock, which no fall-back hour moves.</summary>
    private static async Task<bool> Started(string sessionId)
    {
        var until = DateTime.UtcNow + StartWait;
        while (await Task.Run(() => LiveSessions.Find(sessionId)) == null)
        {
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(500);
        }
        return true;
    }

    private void Drop(string key, IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0 || !_record.TryGetValue(key, out var window)) return;
        window.Sessions.RemoveAll(s => set.Contains(s.Id));
        if (window.Sessions.Count == 0) _record.Remove(key);
        Save();
    }

    private static string Short(string id) => id[..Math.Min(8, id.Length)];

    /// <summary>The record into restore.json when it changed since the last write — never over a file that did not
    /// read.</summary>
    private void Save()
    {
        if (_frozen) return;
        string text = JsonSerializer.Serialize(_record);
        if (text != _saved && Json.WriteAtomic(ClaudePaths.RestoreJson, text)) _saved = text;
    }
}
