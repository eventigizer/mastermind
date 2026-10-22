using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Windows.Threading;

namespace Mastermind;

/// <summary>
/// The VS Code windows' shells, each connected over a WebSocket on the hook listener's <c>/client</c>
/// (<see cref="EventHub.ClientRequested"/>) — one entry per window: its extension host's pid, its workspace folders, the
/// shell's version and the proxy variables of its environment. Messages are JSON objects carrying <c>type</c>.
/// In: <c>hello {hostPid, folders, version, env, editor}</c> — editor {processName, userDataDir, cli}, the window's own
/// (EditorIdentities), <c>folders {folders}</c>, <c>show</c>, <c>settings {values,
/// connect}</c> — ConfigWriter's keys, sent on a window's connect (connect true) and on a change, then a restart when
/// one was written; <c>result {id, ok, error}</c>, a call's or a paste's answer; <c>hooks.propose {id, root, remove}</c> and
/// <c>hooks.apply {id, proposalId}</c>, the watch commands; <c>handoffs.install {id, root, code, relay}</c> and
/// <c>handoffs.uninstall {id, root}</c>, "Use handoffs in this project" and its undo, answered by <c>reply {id, ok,
/// error, needCode, notes, restart, relay}</c> — relay true when the install wrote handoffs.enabled.
/// <c>projects.get {id}</c> and <c>projects.save {id, projects}</c>, the projects page, answered by <c>reply {id, ok,
/// projects}</c> and <c>reply {id, ok, error, restart}</c>.
/// Out: <c>needs {count, items: [{title, project, status, state}]}</c> — the who-needs-you count across every window,
/// on hello and after store changes settle, to each window only when it differs from what that window was last sent;
/// an item's state is <c>permission</c>, <c>waiting</c> or <c>finished</c>, which the shell's badge colours on.
/// <c>call {id, command, args}</c> — a command for the window to run, an unused argument carried as
/// <c>{"$undefined": true}</c>, which the shell rebuilds. <c>paste {id, sessionId, text, title}</c> — a port-off `send`:
/// the window switches to the session with nothing typed and puts the text on the clipboard with a notice; never sent
/// to a shell older than <see cref="PasteSince"/>, which has no handler for it.
/// <c>reply {id, ok, error}</c> answers a hooks message — a
/// proposal's carrying proposalId, root, before, after and warnings. <c>watched {folders: [{root, state}]}</c> — state
/// <c>manifest</c>, <c>watched</c> or <c>off</c>, on hello and folders and whenever it changes.
/// In: hello's <c>health</c> (the shell's switch), <c>health.switch {on}</c>, <c>health.level {level, item, text,
/// share}</c> — ok, warning or error, the worst item's tray line and its share of its ceiling — <c>health.reloaded</c>,
/// sent before a panel reload, and <c>health.busy {id}</c>, the shell's fresh check before a reload or an automatic
/// restart, answered by <c>reply {id, ok, busy, why}</c>. Out: <c>health {servers: [{kind, pid, mb, limitMb}], panel:
/// {state, mb, limitMb, error}, busy, why, error}</c> — every 8 s to each window with health on, the panel's state off,
/// none, ok, stale or error, only when it differs from what that window was last sent.
/// Each socket receives on its own task — the listener takes one request at a time — and sends through its own queue,
/// so messages leave in the order they were made; every inbound message is handled on the UI thread.
/// </summary>
internal sealed class ClientHub
{
    private sealed class Client
    {
        public WebSocket Socket;
        public readonly Channel<byte[]> Out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        public int HostPid;
        public List<string> Folders = new();
        public string Version = "";
        public Dictionary<string, string> Env = new(StringComparer.OrdinalIgnoreCase);
        public string NeedsSent = "";   // the last needs message sent — UI thread
        public string WatchedSent = "";   // the last watched message sent — UI thread
        public bool HealthOn;                    // the shell's health switch — hello, then health.switch; UI thread
        public string HealthSent = "";           // the last health message sent — UI thread
        public string Level = "ok";              // the shell's reported level, its tray line and share — UI thread
        public string LevelText = "";
        public double LevelShare;
        public string PanelState;                // the panel's last state, null before the first read or after a reload — a tick finding one reads the panels; UI thread
        public long PanelMb = -1;                // its last reading, kept as stale for one panel read that misses it
        public long PanelRead = -1;              // the last raw heap read — the reading is the lower of it and the next
        public int PanelMisses;                  // panel reads in a row that missed this window's panel — UI thread
        public string PanelError;
        public string PanelIsolate;              // the V8 isolate its reading came from, null when none — panels of several windows can share one; UI thread
    }

    private const int MaxMessage = 256 * 1024;   // a shell's messages are a few hundred bytes; a bigger one is not a shell
    private static readonly Version PasteSince = new(0, 3, 0);   // the first shell with a paste handler — an older one never answers a paste

    private readonly ConcurrentDictionary<Client, byte> _clients = new();
    private readonly SessionStore _store;
    private readonly Action<LogKind, string> _log;
    private readonly Action _showPanel;
    private readonly Func<IReadOnlyList<string>, bool> _restart;
    // The keys the change that started this run moved, held against connect-time sends for its first minute: a window
    // connecting with a different value disagrees with the window that moved it (two windows' CLAUDE_CONFIG_DIR, VS
    // Code beside a look-alike), and writing it would restart Mastermind back and forth for good. A change the user makes
    // is never held.
    private readonly HashSet<string> _restartedFor;
    private readonly DateTime _startedAt = DateTime.Now;
    private static readonly TimeSpan Hold = TimeSpan.FromMinutes(1);
    private readonly HashSet<string> _warned = new();   // a settings warning is logged once a run, not once a window
    private bool _restartAsked;   // a fresh run is starting — every later settings message waits for it
    private readonly Dictionary<int, (Client Client, TaskCompletionSource<string> Done)> _calls = new();   // UI thread
    private int _nextCall;
    private readonly Dictionary<string, HookEntries.Proposal> _proposals = new();   // UI thread; a restart drops them, and Apply then answers no such proposal
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _needsDebounce;
    private int _healthTick;
    private bool _healthReading;   // a read still running skips the tick — UI thread
    private bool _driftSeen;       // the last panel read found a Claude panel it could not place — UI thread

    /// <summary>Built on the UI thread, whose dispatcher runs the debounce and every inbound message.</summary>
    public ClientHub(SessionStore store, Action<LogKind, string> log, Action showPanel,
                     Func<IReadOnlyList<string>, bool> restart, IReadOnlyList<string> restartedFor, Dispatcher ui)
    {
        _store = store;
        _log = log;
        _showPanel = showPanel;
        _restart = restart;
        _restartedFor = new HashSet<string>(restartedFor);
        _ui = ui;
        _needsDebounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(250) };
        _needsDebounce.Tick += (_, _) =>
        {
            _needsDebounce.Stop();
            var needs = Needs();
            foreach (var c in _clients.Keys) SendChanged(c, needs, ref c.NeedsSent);
        };
    }

    /// <summary>One upgrade, accepted on its own task — EventHub's listener thread hands it over and moves on.</summary>
    public void Accept(HttpListenerContext ctx) => _ = Task.Run(() => RunAsync(ctx));

    /// <summary>A store change: the count goes out once the changes settle. UI thread.</summary>
    public void NeedsChanged()
    {
        if (_clients.IsEmpty) return;
        _needsDebounce.Stop();
        _needsDebounce.Start();
    }

    /// <summary>
    /// Ask the window holding a session to switch its own sidebar to it — <c>claude-vscode.editor.open</c> run in that
    /// window's extension host, which needs no debug port. The window is found by pid: a session's <c>claude.exe</c> sits
    /// below its window's extension host. Null when no window is connected, or none
    /// holds the session — a CLI session in the integrated terminal runs under its shell and takes the CDP path, as
    /// today; else a task ending in null once the window switched, or in why it did not. UI thread.
    /// </summary>
    public Task<string> OpenSession(string sessionId)
    {
        var c = HolderOf(sessionId);
        return c == null ? null : OpenIn(c, sessionId, null, 2000);
    }

    /// <summary>A port-off `send`: the window holding the session switches its sidebar to it with nothing typed — a
    /// prefill replaces whatever its composer holds — puts <paramref name="text"/> on the
    /// clipboard and says so in a notice naming <paramref name="title"/>; the person pastes it and presses Enter. Waited
    /// on 10 s, as <see cref="OpenNew"/> is. Null when no window holds the session; else a task ending in null once the
    /// window did it, or in why it did not. A window whose shell predates <see cref="PasteSince"/> is refused unsent,
    /// naming the reinstall — it would never answer, and the timeout reads as may still open. UI thread.</summary>
    public Task<string> PasteInto(string sessionId, string text, string title)
    {
        var c = HolderOf(sessionId);
        if (c == null) return null;
        if (!Version.TryParse(c.Version, out var shell) || shell < PasteSince)
            return Task.FromResult($"its window's shell {c.Version} predates paste — update the Agent Mastermind extension, then restart VS Code");
        return Request(c, new JsonObject { ["type"] = "paste", ["sessionId"] = sessionId, ["text"] = text, ["title"] = title }, 10000);
    }

    /// <summary>The connected window whose extension host runs the session's <c>claude.exe</c>, or null — found
    /// anywhere below the host, since <c>claudeCode.claudeProcessWrapper</c> puts its wrapper between them (the extension
    /// starts the wrapper with claude.exe's path as its first argument).</summary>
    private Client HolderOf(string sessionId)
    {
        if (_clients.IsEmpty) return null;
        Dictionary<string, int> hosts;
        try { hosts = HealthReader.SessionHosts(_clients.Keys.Select(c => c.HostPid).Distinct().ToList()).HostBySession; }
        catch { return null; }   // a failed process or registry read finds no window
        return hosts.TryGetValue(sessionId, out int host) ? _clients.Keys.FirstOrDefault(x => x.HostPid == host) : null;
    }

    /// <summary>Open a NEW sidebar session in one window with <paramref name="prompt"/> typed in, unsent — the same
    /// command with no session id. The answer says the window ran the command, never that
    /// the session showed: the sidebar loads after it, and the extension drops the prompt when the sidebar has not
    /// connected in 15 s. Waited on 10 s — a hot window's extension host can be slow to run it at all. Null when that
    /// window is no longer connected; else a task ending in null once the window ran it, or in why it did not. UI
    /// thread.</summary>
    public Task<string> OpenNew(int hostPid, string prompt) => OpenInHost(hostPid, null, prompt);

    /// <summary>One session by id in one window — session restore's open: <c>editor.open</c> starts its
    /// <c>claude.exe</c> with nothing typed, or only switches to it when it runs. Waited
    /// on 10 s, as <see cref="OpenNew"/> is; the answer says the window ran it, never that the session started. Null
    /// when that window is no longer connected; else a task ending in null once the window ran it, or in why it did
    /// not. UI thread.</summary>
    public Task<string> OpenSessionIn(int hostPid, string sessionId) => OpenInHost(hostPid, sessionId, null);

    private Task<string> OpenInHost(int hostPid, string sessionId, string prompt)
    {
        var c = _clients.Keys.FirstOrDefault(x => x.HostPid == hostPid);
        return c == null ? null : OpenIn(c, sessionId, prompt, 10000);
    }

    /// <summary>A window with an extension host and a local folder — the only kind <see cref="Windows"/> lists and
    /// session restore records.</summary>
    private static bool IsLocal(Client c) => c.HostPid > 0 && c.Folders.Count > 0;

    /// <summary>Whether a window with a local folder is connected under this extension host. UI thread.</summary>
    public bool IsConnected(int hostPid) => _clients.Keys.Any(c => c.HostPid == hostPid && IsLocal(c));

    /// <summary>The extension host of the one connected window whose first folder is <paramref name="root"/>, else 0 — a
    /// port-off `new` dispatch opens its session there. A no-id editor.open names no folder, so a window holding the
    /// project as a later folder could start the session in another project; none, or two such windows, is refused, as
    /// HealthReader's window-for-a-folder rule refuses several. UI thread.</summary>
    public int HostFor(string root)
    {
        var key = Paths.Key(root);
        var hosts = _clients.Keys.Where(c => IsLocal(c) && Paths.Key(c.Folders[0]) == key).Select(c => c.HostPid).Distinct().ToList();
        return hosts.Count == 1 ? hosts[0] : 0;
    }

    /// <summary>An open's answer that refused: neither null (the window ran it) nor <see cref="Request"/>'s deadline, which
    /// says only that the window did not answer in time — it may still run the command, so a dispatch keeps its file
    /// rather than rolling back.</summary>
    public static bool Refused(string answer) => answer != null && !answer.StartsWith("no answer in ", StringComparison.Ordinal);

    /// <summary>The connected windows a new session can open in — each with its extension host's pid, its menu name and
    /// its folders — by name. Only a window with a folder: the shell reports local (file-scheme) folders alone, so a
    /// remote or an empty window carries none. A name two windows share shows each one's first folder's full path, so
    /// no two rows read alike. UI thread.</summary>
    public List<(int HostPid, string Name, List<string> Folders)> Windows()
    {
        var open = _clients.Keys.Where(IsLocal).ToList();
        var shared = open.GroupBy(WindowName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                         .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return open.Select(c => (c.HostPid, Name: shared.Contains(WindowName(c)) ? c.Folders[0] : WindowName(c), c.Folders))
                   .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary><c>claude-vscode.editor.open</c> in one window: a session id switches its sidebar to that session, none
    /// opens a new one; a prompt lands in the composer, unsent. An unused position must arrive as undefined — a null
    /// there throws inside the extension, and JSON has no undefined, so each travels as a marker — and
    /// <c>programmatic</c> always goes, or the extension moves the user's preferred location to the panel. Past
    /// <paramref name="deadlineMs"/> the late answer is dropped while the command still runs there, so the timeout
    /// says the window may still open it.</summary>
    private Task<string> OpenIn(Client c, string sessionId, string prompt, int deadlineMs)
    {
        JsonNode Arg(string value) => value != null ? JsonValue.Create(value) : new JsonObject { ["$undefined"] = true };
        return Request(c, new JsonObject
        {
            ["type"] = "call",
            ["command"] = "claude-vscode.editor.open",
            ["args"] = new JsonArray(Arg(sessionId), Arg(prompt), Arg(null), Arg(null), Arg(null),
                new JsonObject { ["programmatic"] = "honor-preferred-location" }),
        }, deadlineMs);
    }

    /// <summary>One message the window answers with a <c>result</c> carrying its id — a <c>call</c> or a
    /// <c>paste</c>. Past <paramref name="deadlineMs"/> the late answer is dropped while the window may still act on
    /// it.</summary>
    private Task<string> Request(Client c, JsonObject message, int deadlineMs)
    {
        int id = ++_nextCall;
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = (c, done);
        message["id"] = id;
        Send(c, message);
        _ = Task.Delay(deadlineMs).ContinueWith(_ =>
            Post(() => Finish(id, $"no answer in {deadlineMs / 1000} s — the window may still open it")));
        return done.Task;
    }

    private void Finish(int id, string error)
    {
        if (_calls.Remove(id, out var call)) call.Done.TrySetResult(error);
    }

    /// <summary>Each window's folder states re-sent where they changed — after a write, and on the 8 s health tick,
    /// which catches a hand edit or a manifest rewrite. One manifest read for every window, then a settings read per
    /// folder; a tick whose manifest read fails sends nothing, so no window shows a manifest project as unwatched. UI
    /// thread.</summary>
    public void RefreshWatched()
    {
        if (_clients.IsEmpty) return;
        var (manifest, failed) = ProjectList.ReadManifest();
        if (failed) return;
        foreach (var c in _clients.Keys) SendWatched(c, manifest);
    }

    /// <summary>Raised on the UI thread when a window's reported error level changes, or a window holding one goes.</summary>
    public event Action HealthChanged;

    /// <summary>Raised on the UI thread when a window says hello with a local folder — its extension host's pid, its
    /// folders and its name — so session restore can give it back its sessions.</summary>
    public event Action<int, List<string>, string> WindowConnected;

    /// <summary>Raised on the UI thread when a window's shell says it is about to reload its panels or itself for VS
    /// Code health (<c>health.reloaded</c>) — either ends the window's Claude sessions; the panel reload keeps the
    /// window connected.</summary>
    public event Action<int, List<string>, string> HealthReloaded;

    /// <summary>Each window in its error band as its tray line, the largest share of its ceiling first. UI thread.</summary>
    public List<string> HealthErrors() =>
        _clients.Keys.Where(c => c.HealthOn && c.Level == "error").OrderByDescending(c => c.LevelShare)
                     .Select(c => c.LevelText).ToList();

    /// <summary>The 8 s tick: each window with health on gets its readings — the servers every tick, the Claude panels
    /// every fourth and on any tick a window has no panel state (it just connected, or its panels just reloaded), while
    /// the debug port is set. The reads run off the UI thread; the sends come back to it. UI thread.</summary>
    public void RefreshHealth()
    {
        var on = _clients.Keys.Where(c => c.HealthOn && c.HostPid > 0).ToList();
        if (on.Count == 0 || _healthReading) return;
        _healthReading = true;
        bool panels = (_healthTick++ % 4 == 0 || on.Any(c => c.PanelState == null)) && CdpClient.Port != 0;
        var hosts = on.Select(c => c.HostPid).Distinct().ToList();
        var folders = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var c in on) folders[c.HostPid] = c.Folders.ToList();
        var awaiting = AwaitingIds();
        _ = Task.Run(async () =>
        {
            HealthReader.ServerRead servers = null;
            HealthReader.Panels panel = null;
            string failed = null, panelFailed = null;
            try { servers = HealthReader.ReadServers(hosts, awaiting); }
            catch (Exception ex) { failed = $"the server read failed: {ex.Message}"; }
            if (servers != null && panels)
            {
                // A throw is the panel's error, never off — only a port of 0 reads off.
                try { panel = await HealthReader.ReadPanels(servers, folders); }
                catch (Exception ex)
                {
                    panelFailed = $"the panel read failed: {ex.Message}";
                    panel = new HealthReader.Panels(false, new Dictionary<int, long>(), new Dictionary<int, string>(),
                                                    new Dictionary<int, string>(), panelFailed);
                }
            }
            Post(() =>
            {
                _healthReading = false;
                if (failed != null)
                {
                    Warn($"health · {failed}");
                    // Every window hears it: no servers, busy — nothing restarts on a read that failed — and the error.
                    var windows = hosts.ToDictionary(h => h, _ => new HealthReader.WindowReading(
                        new List<HealthReader.Server>(), true, "its processes could not be read", HealthReader.CageMb, failed));
                    servers = new HealthReader.ServerRead(windows, new Dictionary<string, int>(), new HashSet<string>());
                }
                if (panelFailed != null) Warn($"health · {panelFailed}");
                // A Claude panel the probe cannot place may only be loading — just reloaded, or signed out — so its drift
                // error shows once the next panel read finds one too.
                if (panel is { Answered: true, Error: not null })
                {
                    if (!_driftSeen) panel = panel with { Error = null };
                    _driftSeen = true;
                }
                else if (panel != null) _driftSeen = false;
                ApplyHealth(servers.Windows, panels && failed == null, panel);
            });
        });
    }

    private void ApplyHealth(Dictionary<int, HealthReader.WindowReading> servers, bool panelsRead, HealthReader.Panels panel)
    {
        var read = new List<(Client C, HealthReader.WindowReading W)>();
        foreach (var c in _clients.Keys.Where(x => x.HealthOn && x.HostPid > 0))
        {
            // A window that turned health on after this read began waits for the next tick — never an empty reading.
            if (!servers.TryGetValue(c.HostPid, out var w)) continue;
            // Only a port of 0 reads off; every failure past it is the panel's error.
            if (CdpClient.Port == 0) { c.PanelState = "off"; c.PanelMb = -1; c.PanelRead = -1; c.PanelMisses = 0; c.PanelError = null; c.PanelIsolate = null; }
            else if (panelsRead && panel != null)
            {
                panel.Errors.TryGetValue(c.HostPid, out var failed);
                if (!panel.Answered) PanelFailed(c, panel.Error);
                else if (panel.Mb.TryGetValue(c.HostPid, out var mb))
                {
                    // A heap read counts garbage until the next collection, so one read flaps: the reading is the lower
                    // of the last two.
                    c.PanelMb = c.PanelRead >= 0 ? Math.Min(mb, c.PanelRead) : mb;
                    c.PanelRead = mb;
                    c.PanelState = "ok";
                    c.PanelMisses = 0;
                    c.PanelError = failed;
                    c.PanelIsolate = panel.Isolates.TryGetValue(c.HostPid, out var isolate) ? isolate : null;
                }
                else if (failed != null) PanelFailed(c, failed);
                // A panel out of the list keeps its last value as stale for one read; the second miss drops it.
                else if (c.PanelMb >= 0 && ++c.PanelMisses < 2) { c.PanelState = "stale"; c.PanelError = panel.Error; }
                else { c.PanelMb = -1; c.PanelRead = -1; c.PanelMisses = 0; c.PanelError = panel.Error; c.PanelState = panel.Error != null ? "error" : "none"; c.PanelIsolate = null; }
            }
            c.PanelState ??= "none";
            read.Add((c, w));
        }
        // A panel's heap is its isolate's, and panels of several windows can share one: each window names the others
        // its reading also covers, since its own reload frees only its share.
        foreach (var (c, w) in read)
        {
            var shared = c.PanelIsolate == null ? new List<string>()
                : read.Where(o => o.C.HostPid != c.HostPid && o.C.PanelIsolate == c.PanelIsolate && o.C.PanelState is "ok" or "stale")
                      .Select(o => WindowName(o.C)).ToList();
            var list = new JsonArray();
            foreach (var s in w.Servers)
                list.Add(new JsonObject { ["kind"] = s.Kind, ["pid"] = s.Pid, ["mb"] = s.Mb, ["limitMb"] = s.LimitMb });
            SendChanged(c, new JsonObject
            {
                ["type"] = "health",
                ["servers"] = list,
                ["panel"] = new JsonObject
                {
                    ["state"] = c.PanelState, ["mb"] = c.PanelMb, ["limitMb"] = w.PanelLimitMb, ["error"] = c.PanelError,
                    ["sharedWith"] = new JsonArray(shared.Select(n => (JsonNode)n).ToArray()),
                },
                ["busy"] = w.Busy,
                ["why"] = w.Why,
                ["error"] = w.Error,
            }, ref c.HealthSent);
        }
    }

    /// <summary>A panel read that failed for one window: its last value kept as stale beside the error, else the error
    /// alone — never off, which only a port of 0 reads.</summary>
    private static void PanelFailed(Client c, string error)
    {
        c.PanelError = error;
        c.PanelState = c.PanelMb >= 0 ? "stale" : "error";
        if (c.PanelState == "error") c.PanelIsolate = null;
    }

    /// <summary>A window by its folders' names — in another window's tooltip, the log and the health check's submenu.
    /// " + ", never ", ": the shell joins a panel's sharedWith names with ", ", so a comma would split one window into
    /// two there.</summary>
    private static string WindowName(Client c) =>
        c.Folders.Count > 0 ? string.Join(" + ", c.Folders.Select(f => Paths.Leaf(f))) : $"window {c.HostPid}";

    /// <summary>The session ids holding the WAITING card — the store is the UI thread's. UI thread.</summary>
    private HashSet<string> AwaitingIds() =>
        _store.Events.Where(e => e.Status == AgentStatus.AwaitingTask && !string.IsNullOrEmpty(e.SessionId))
                     .Select(e => e.SessionId).ToHashSet();

    /// <summary>The shell's fresh busy check for its own window, read off the UI thread and answered on it; a read that
    /// throws answers busy with the error, so nothing reloads on a guess.</summary>
    private void AnswerBusy(Client c, int id)
    {
        int host = c.HostPid;
        var awaiting = AwaitingIds();
        _ = Task.Run(() =>
        {
            string why;
            try { why = host > 0 ? HealthReader.BusyWhy(host, awaiting) : "the window named no extension host"; }
            catch (Exception ex) { why = $"its sessions could not be read ({ex.Message})"; }
            Post(() => Send(c, new JsonObject { ["type"] = "reply", ["id"] = id, ["ok"] = true, ["busy"] = why != null, ["why"] = why }));
        });
    }

    /// <summary>A push to one window — needs, watched or health — skipped when it reads as what that window was last
    /// sent of its kind (<paramref name="last"/>, the client's field for it). UI thread.</summary>
    private static void SendChanged(Client c, JsonObject message, ref string last)
    {
        var text = message.ToJsonString();
        if (text == last) return;
        last = text;
        c.Out.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>A window's reported level; the tray recomputes when an error band starts, ends or changes its line.</summary>
    private void SetLevel(Client c, string level, string text, double share)
    {
        if (level is not ("ok" or "warning" or "error")) level = "ok";
        bool tray = (c.Level == "error") != (level == "error")
                    || (level == "error" && (text != c.LevelText || share != c.LevelShare));
        c.Level = level;
        c.LevelText = text;
        c.LevelShare = share;
        if (tray) HealthChanged?.Invoke();
    }

    private static void SendWatched(Client c)
    {
        var (manifest, failed) = ProjectList.ReadManifest();
        if (!failed) SendWatched(c, manifest);
    }

    /// <summary>The folder states to one window, skipped when they read as what that window was last sent. UI thread.</summary>
    private static void SendWatched(Client c, ProjectsManifest manifest)
    {
        var folders = new JsonArray();
        foreach (var root in c.Folders)
            folders.Add(new JsonObject
            {
                ["root"] = root,
                ["state"] = HookEntries.IsListed(manifest, root) ? "manifest" : HookDrift.IsWired(root, manifest) ? "watched" : "off",
            });
        var message = new JsonObject { ["type"] = "watched", ["folders"] = folders };
        SendChanged(c, message, ref c.WatchedSent);
    }

    private async Task RunAsync(HttpListenerContext ctx)
    {
        WebSocket ws;
        try { ws = (await ctx.AcceptWebSocketAsync(null, TimeSpan.FromSeconds(20))).WebSocket; }
        catch (Exception ex)
        {
            Post(() => _log(LogKind.Warn, $"client · upgrade failed ({ex.Message})"));
            try { ctx.Response.Abort(); } catch { }
            return;
        }
        var c = new Client { Socket = ws };
        _clients[c] = 0;
        var sender = Task.Run(() => SendLoopAsync(c));
        try
        {
            var buf = new byte[16 * 1024];
            using var msg = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                msg.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    msg.Write(buf, 0, r.Count);
                    if (msg.Length > MaxMessage) throw new InvalidDataException($"a message over {MaxMessage} bytes");
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                    Post(() => OnMessage(c, text));
                }
            }
        }
        catch { /* the window went away, or sent what no shell sends — either ends this client */ }
        finally
        {
            _clients.TryRemove(c, out _);
            c.Out.Writer.TryComplete();
            try { await sender; } catch { }
            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch { }
            ws.Dispose();
            Post(() => Gone(c));
        }
    }

    private static async Task SendLoopAsync(Client c)
    {
        try
        {
            await foreach (var bytes in c.Out.Reader.ReadAllAsync())
                if (c.Socket.State == WebSocketState.Open)
                    await c.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { /* the socket broke — the receive side ends the client */ }
    }

    private static void Send(Client c, JsonNode message) => c.Out.Writer.TryWrite(Encoding.UTF8.GetBytes(message.ToJsonString()));

    // BeginInvoke, so a throw reaches DispatcherUnhandledException and its log — InvokeAsync keeps it in its task.
    private void Post(Action action) => _ui.BeginInvoke(action);

    /// <summary>One inbound message, on the UI thread; anything that is not a JSON object is dropped.</summary>
    private void OnMessage(Client c, string text)
    {
        JsonObject m;
        try { m = JsonNode.Parse(text) as JsonObject; } catch { return; }
        if (m == null) return;
        switch (Json.Str(m["type"]))
        {
            case "hello":
                c.HostPid = Json.Int(m["hostPid"]);
                c.Folders = Strings(m["folders"]);
                c.Version = Json.Str(m["version"]);
                c.Env = ReadEnv(m["env"]);
                c.HealthOn = Json.True(m["health"]);   // absent (an older shell or a probe) reads as off until a health.switch
                // The editor this window runs in — absent from an older shell or a probe, which leaves config's.
                EditorIdentity editor = m["editor"] is JsonObject ed && Json.Str(ed["processName"]) is { Length: > 0 } image
                    ? new EditorIdentity { ProcessName = image, UserDataDir = Json.Str(ed["userDataDir"]), Cli = Json.Str(ed["cli"]) }
                    : null;
                if (editor != null) EditorIdentities.Connected(c.HostPid, editor);
                EditorIdentities.Folders(c.HostPid, c.Folders);
                _log(LogKind.Info, $"client · window {c.HostPid} connected (shell {c.Version}" +
                                   (editor != null ? $", {editor.ProcessName} at {editor.UserDataDir}" : "") + ")" +
                                   (c.Folders.Count > 0 ? " · " + WindowName(c) : ""));
                SendChanged(c, Needs(), ref c.NeedsSent);
                SendWatched(c);
                if (IsLocal(c)) WindowConnected?.Invoke(c.HostPid, c.Folders.ToList(), WindowName(c));
                break;
            case "folders":
                c.Folders = Strings(m["folders"]);
                EditorIdentities.Folders(c.HostPid, c.Folders);
                SendWatched(c);
                break;
            case "show":
                _showPanel();
                break;
            case "settings":
                if (!_restartAsked && m["values"] is JsonObject values)
                {
                    var changes = ConfigWriter.Changes(values, Warn);
                    if (Json.True(m["connect"]) && DateTime.Now - _startedAt < Hold)
                        foreach (var held in changes.Where(ch => _restartedFor.Contains(ch.Key)).ToList())
                        {
                            changes.Remove(held);
                            Warn($"config · window {c.HostPid} connects with a different {held.Key} — its settings " +
                                 "disagree with the window that changed it; set them the same in both");
                        }
                    var keys = changes.Count > 0 ? ConfigWriter.Write(changes, Warn) : null;
                    if (keys is { Count: > 0 }) _restartAsked = _restart(keys);
                }
                break;
            case "health.switch":
                c.HealthOn = Json.True(m["on"]);
                c.HealthSent = "";
                if (!c.HealthOn) SetLevel(c, "ok", "", 0);
                break;
            case "health.level":
                SetLevel(c, Json.Str(m["level"]), Json.Str(m["text"]),
                         m["share"] is JsonValue sv && sv.TryGetValue<double>(out var share) ? share : 0);
                break;
            case "health.reloaded":
                // A panel reload frees the old heap: forget the window's panel — with no panel state, the next tick
                // reads the panels.
                c.PanelState = null;
                c.PanelMb = -1;
                c.PanelRead = -1;
                c.PanelMisses = 0;
                c.PanelError = null;
                c.PanelIsolate = null;
                if (IsLocal(c)) HealthReloaded?.Invoke(c.HostPid, c.Folders.ToList(), WindowName(c));
                break;
            case "health.busy":
                AnswerBusy(c, Json.Int(m["id"]));
                break;
            case "result":
            {
                int rid = Json.Int(m["id"]);
                if (_calls.TryGetValue(rid, out var call) && call.Client == c)
                    Finish(rid, Json.True(m["ok"]) ? null : Json.Str(m["error"]) is { Length: > 0 } err ? err : "failed");
                break;
            }
            case "hooks.propose":
            {
                var (p, error) = HookEntries.Propose(Json.Str(m["root"]), Json.True(m["remove"]), c.Env);
                var reply = new JsonObject { ["type"] = "reply", ["id"] = Json.Int(m["id"]), ["ok"] = p != null };
                if (p == null) reply["error"] = error;
                else
                {
                    _proposals[p.Id] = p;
                    reply["proposalId"] = p.Id;
                    reply["root"] = p.Root;
                    reply["before"] = p.Before;
                    reply["after"] = p.After;
                    reply["warnings"] = new JsonArray(p.Warnings.Select(w => (JsonNode)w).ToArray());
                }
                Send(c, reply);
                break;
            }
            case "hooks.apply":
            {
                string error = _proposals.Remove(Json.Str(m["proposalId"]), out var p)
                    ? HookEntries.Apply(p)
                    : "no such proposal — run the command again";
                if (error == null)
                    _log(LogKind.Info, $"hooks · {Paths.Leaf(p.Root)}: Mastermind's entries {(p.Remove ? "removed" : "written")} from window {c.HostPid}");
                var reply = new JsonObject { ["type"] = "reply", ["id"] = Json.Int(m["id"]), ["ok"] = error == null };
                if (error != null) reply["error"] = error;
                Send(c, reply);
                RefreshWatched();
                break;
            }
            case "handoffs.install":
                AnswerHandoffs(c, m, true);
                break;
            case "handoffs.uninstall":
                AnswerHandoffs(c, m, false);
                break;
            case "projects.get":
                Send(c, new JsonObject { ["type"] = "reply", ["id"] = Json.Int(m["id"]), ["ok"] = true, ["projects"] = ProjectsPage.Rows() });
                break;
            case "projects.save":
                AnswerProjects(c, m);
                break;
        }
    }

    private void Gone(Client c)
    {
        foreach (var id in _calls.Where(x => x.Value.Client == c).Select(x => x.Key).ToList())
            Finish(id, "its window disconnected");
        _log(LogKind.Info, $"client · window {c.HostPid} disconnected");
        if (!_clients.Keys.Any(x => x.HostPid == c.HostPid)) EditorIdentities.Disconnected(c.HostPid);
        if (c.Level == "error") HealthChanged?.Invoke();
    }

    /// <summary>"Use handoffs in this project" or its undo (<see cref="HandoffSkills"/>), answered with a reply whose
    /// error carries the warnings behind it — the activity log names a warning once a run, and the restart below wipes
    /// it. A config write restarts the exe, as a settings write does — half a second later, so the reply leaves first:
    /// the exit drops a queued send, and the shell would read the drop as a failure. While that restart is pending a
    /// second request is refused: config is read once a run, so it would check its code against a stale book.</summary>
    private void AnswerHandoffs(Client c, JsonObject m, bool install)
    {
        var root = Json.Str(m["root"]);
        var said = new List<string>();
        void Say(string text) { said.Add(text); Warn(text); }
        var o = _restartAsked
            ? HandoffSkills.Fail("Mastermind is restarting — run the command again in a moment")
            : install ? HandoffSkills.Install(root, Json.Str(m["code"]), Json.True(m["relay"]), Say)
                      : HandoffSkills.Uninstall(root, Say);
        bool restart = o.Keys.Count > 0;
        var reply = new JsonObject
        {
            ["type"] = "reply",
            ["id"] = Json.Int(m["id"]),
            ["ok"] = o.Ok,
            ["needCode"] = o.NeedCode,
            ["notes"] = new JsonArray(o.Notes.Select(n => (JsonNode)n).ToArray()),
            ["restart"] = restart,
            ["relay"] = o.Keys.Contains("handoffs.enabled"),
        };
        if (o.Error != null) reply["error"] = said.Count > 0 ? $"{o.Error} — {string.Join(" ", said)}" : o.Error;
        Send(c, reply);
        if (o.Ok) _log(LogKind.Info, $"handoffs · {Paths.Leaf(root)}: {(install ? "in use" : "no longer in use")} from window {c.HostPid}");
        if (restart)
        {
            _restartAsked = true;   // a settings message meanwhile waits for the fresh run, as after a settings write
            _ = Task.Delay(500).ContinueWith(_ => Post(() => _restartAsked = _restart(o.Keys)));
        }
    }

    /// <summary>The projects page's save (<see cref="ProjectsPage.Save"/>), answered with a reply; a write restarts the
    /// exe half a second later, as the handoff install's does, so the reply leaves first. Refused while a restart is
    /// pending — config is read once a run.</summary>
    private void AnswerProjects(Client c, JsonObject m)
    {
        (List<string> Keys, string Error) o = _restartAsked
            ? (null, "Mastermind is restarting — save again in a moment")
            : ProjectsPage.Save(m["projects"] as JsonArray ?? new JsonArray(), Warn);
        bool restart = o.Keys is { Count: > 0 };
        var reply = new JsonObject { ["type"] = "reply", ["id"] = Json.Int(m["id"]), ["ok"] = o.Error == null, ["restart"] = restart };
        if (o.Error != null) reply["error"] = o.Error;
        Send(c, reply);
        if (restart)
        {
            _log(LogKind.Info, $"projects · saved from window {c.HostPid}");
            _restartAsked = true;
            _ = Task.Delay(500).ContinueWith(_ => Post(() => _restartAsked = _restart(o.Keys)));
        }
    }

    private void Warn(string text)
    {
        if (_warned.Add(text)) _log(LogKind.Warn, text);
    }

    private JsonObject Needs()
    {
        var items = new JsonArray();
        foreach (var e in _store.Events)
            if (e.NeedsYou)
                items.Add(new JsonObject
                {
                    ["title"] = e.DisplayTitle,
                    ["project"] = e.ProjectName,
                    ["status"] = e.StatusText,
                    // What the shell reads — the status label is display text and may change.
                    ["state"] = e.Status switch
                    {
                        AgentStatus.NeedsPermission => "permission",
                        AgentStatus.WaitingForYou => "waiting",
                        _ => "finished",
                    },
                });
        return new JsonObject { ["type"] = "needs", ["count"] = items.Count, ["items"] = items };
    }

    private static List<string> Strings(JsonNode n) =>
        n is JsonArray a ? a.Select(x => Json.Str(x)).Where(s => s.Length > 0).ToList() : new List<string>();

    private static Dictionary<string, string> ReadEnv(JsonNode n)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (n is JsonObject o)
            foreach (var (k, v) in o)
                if (Json.Str(v) is { Length: > 0 } s) env[k] = s;
        return env;
    }
}
