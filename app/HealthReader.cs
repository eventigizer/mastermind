using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mastermind;

/// <summary>
/// VS Code health's one reader (phase 3b): each connected window's language servers — the children of its extension
/// host, the pid its shell's hello names — why any of its Claude sessions is anything but idle, and each Claude
/// panel's JavaScript heap over the debug port. Runs off the UI thread; the caller posts the result back.
/// </summary>
internal static class HealthReader
{
    /// <summary>One server: <c>typescript</c>, <c>eslint</c> or <c>angular</c>; private memory in MB, rounded to 10 so a
    /// send means a real change; the heap it can reach (<see cref="LimitOf"/>).</summary>
    internal sealed record Server(string Kind, int Pid, long Mb, long LimitMb);

    /// <summary>The most heap a flag can give a server on VS Code's own Node — its pointer-compression cage, 4192 MB of
    /// heap_size_limit (VS Code 1.140.0); past it only a <c>node.exe</c> of the server's own. Also the fallback when the
    /// default cannot be measured.</summary>
    internal const long CageMb = 4192;

    // Each editor exe's default V8 heap, measured once a run: V8 sizes it off the machine's memory — 4192 MB at 16 GB of
    // RAM and up, about half below — so no one number holds on every PC. A failed measurement is held a minute, then
    // taken again: a probe that timed out on a busy machine would otherwise sit on the item until the next run, and the
    // hold keeps every window on one exe from waiting out the probe's bound on each tick.
    private static readonly ConcurrentDictionary<string, (long Mb, string Error, long At)> Defaults = new(StringComparer.OrdinalIgnoreCase);
    private const long RetryMs = 60000;

    /// <summary>A window's servers; busy, with why, unless every Claude session of the window reads idle (<see
    /// cref="BusyWhy(int, IReadOnlySet{string})"/>); the Claude panel's ceiling, its exe's default heap; the first child
    /// it could not read, else the default's failed measurement.</summary>
    internal sealed record WindowReading(List<Server> Servers, bool Busy, string Why, long PanelLimitMb, string Error);

    /// <summary>One server read: each host's reading; each health-on host's Claude sessions, session id → host; every
    /// running Claude session's id — what the panel read places by.</summary>
    internal sealed record ServerRead(Dictionary<int, WindowReading> Windows, Dictionary<string, int> HostBySession, HashSet<string> LiveIds);

    /// <summary>The panels' read: Answered false when the debug port gives no VS Code target list — down, or another
    /// program on it — with Error saying so; each host's largest Claude panel heap in MB; each host whose placed panel
    /// failed its heap read, with why; each host's reading's V8 isolate id — the heap is the isolate's, and panels of
    /// several windows can share one; and, when answered, Error for a Claude panel no probe could place — the
    /// extension drifted, or a panel still loading or signed out — which belongs to no window, so ClientHub logs it on a
    /// second panel read running and puts it on no window's item.</summary>
    internal sealed record Panels(bool Answered, Dictionary<int, long> Mb, Dictionary<int, string> Errors,
                                  Dictionary<int, string> Isolates, string Error);

    private static readonly (string Kind, Func<string, bool> Is)[] Kinds =
    {
        ("typescript", c => c.Contains("tsserver.js", StringComparison.OrdinalIgnoreCase)
                            && !c.Contains("typingsInstaller.js", StringComparison.OrdinalIgnoreCase)),
        ("eslint", c => c.Contains("eslintServer.js", StringComparison.OrdinalIgnoreCase)),
        ("angular", c => c.Contains("--ngProbeLocations", StringComparison.Ordinal)),
    };
    private static readonly Regex HeapFlag = new(@"--max-old-space-size=(\d+)");

    /// <summary>Each host's servers — the largest of a kind, since a window runs more than one TypeScript server — and
    /// why its sessions are busy, if they are; with the session map the panel read places by. <paramref
    /// name="awaiting"/>: the session ids holding the WAITING card, read on the UI thread. The registry read is safe
    /// here — its snapshot is volatile.</summary>
    public static ServerRead ReadServers(IReadOnlyCollection<int> hosts, IReadOnlySet<string> awaiting)
    {
        var children = Platform.Processes.ChildrenOf(hosts);
        var claude = SessionProcesses(hosts);   // pid → host
        var rows = RunningByPid();
        var (hostBySession, liveIds) = Join(claude.Select(c => (c.Key, c.Value)), rows);
        var result = new Dictionary<int, WindowReading>();
        foreach (int host in hosts)
        {
            var mine = children.Where(c => c.Parent == host).ToList();
            var (defaultMb, unmeasured) = DefaultHeapOf(host);
            var servers = new List<Server>();
            foreach (var (kind, isKind) in Kinds)
            {
                var top = mine.Where(c => c.Error == null && isKind(c.CommandLine))
                              .OrderByDescending(c => c.PrivateBytes).FirstOrDefault();
                if (top.Pid == 0) continue;
                servers.Add(new Server(kind, top.Pid, top.PrivateBytes / (1024 * 1024) / 10 * 10, LimitOf(top, defaultMb)));
            }
            string why = BusyWhy(claude.Where(c => c.Value == host).Select(c => c.Key), rows, awaiting);
            result[host] = new WindowReading(servers, why != null, why, defaultMb,
                                             mine.FirstOrDefault(c => c.Error != null).Error ?? unmeasured);
        }
        return new ServerRead(result, hostBySession, liveIds);
    }

    /// <summary>The heap a server can reach: its own <c>--max-old-space-size</c> (TypeScript's is
    /// <c>js/ts.tsserver.maxMemory</c>), held to <see cref="CageMb"/> unless it runs on a <c>node.exe</c> of its own;
    /// with no flag, the measured default of its window's exe.</summary>
    private static long LimitOf(ChildProcess server, long defaultMb)
    {
        var flag = HeapFlag.Match(server.CommandLine);
        bool node = string.Equals(server.Exe, "node.exe", StringComparison.OrdinalIgnoreCase);
        return !flag.Success ? defaultMb : node ? long.Parse(flag.Groups[1].Value) : Math.Min(long.Parse(flag.Groups[1].Value), CageMb);
    }

    /// <summary>The default V8 heap of the exe a window's host runs on — what VS Code's own Node gives a server with no
    /// flag, and the nearest measure of a Claude panel renderer's cap, which Chromium sizes off the machine's memory
    /// too. Measured once a run per exe; a failure keeps <see cref="CageMb"/> with why, which the window's item shows, and
    /// is measured again <see cref="RetryMs"/> on.</summary>
    private static (long Mb, string Error) DefaultHeapOf(int host)
    {
        string exe = Platform.Processes.ImagePath(host);
        var (mb, error, _) = exe == null
            ? (CageMb, "VS Code's heap cap could not be measured (its exe path is unreadable) — 4.1 GB assumed", 0L)
            : Defaults.AddOrUpdate(exe, Measure,
                                   (key, was) => was.Error != null && Environment.TickCount64 - was.At >= RetryMs ? Measure(key) : was);
        return (mb, error);
    }

    /// <summary>The exe run as Node (ELECTRON_RUN_AS_NODE) to print its heap_size_limit in MB, stamped with when.</summary>
    private static (long Mb, string Error, long At) Measure(string exe)
    {
        (long Mb, string Error) result;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("Math.round(require('v8').getHeapStatistics().heap_size_limit / 1048576)");
            psi.Environment["ELECTRON_RUN_AS_NODE"] = "1";
            using var p = Process.Start(psi);
            // The bound sits on the read itself, as EventHub's netsh read does: a hung exe never holds the health tick.
            var read = p.StandardOutput.ReadToEndAsync();
            if (!read.Wait(10000))
            {
                try { p.Kill(); } catch { }
                throw new TimeoutException("no answer in 10 s");
            }
            string text = read.Result.Trim();
            if (!long.TryParse(text, out var mb) || mb <= 0) throw new FormatException($"it printed \"{text}\"");
            result = (mb, null);
        }
        catch (Exception ex) { result = (CageMb, $"VS Code's heap cap could not be measured ({ex.Message}) — 4.1 GB assumed"); }
        return (result.Mb, result.Error, Environment.TickCount64);
    }

    /// <summary>Why one host's Claude sessions are not all idle, or null when they are — a fresh read for the shell's
    /// check before a reload or an automatic restart. <paramref name="awaiting"/> as <see cref="ReadServers"/>.</summary>
    public static string BusyWhy(int host, IReadOnlySet<string> awaiting) =>
        BusyWhy(SessionProcesses(new[] { host }).Keys, RunningByPid(), awaiting);

    // The Claude extension's Chrome-integration server: one more claude.exe in each window, beside its sessions — no
    // session, so no registry row (extension v2.1.301).
    private const string ChromeHelperFlag = "--claude-in-chrome-mcp";

    /// <summary>Every Claude session process anywhere under these hosts, pid → host. The extension's Chrome helper is
    /// left out; any other <c>claude.exe</c> stays in, so one with no registry row still reads busy.</summary>
    private static Dictionary<int, int> SessionProcesses(IReadOnlyCollection<int> hosts) =>
        Platform.Processes.DescendantsNamed(hosts, "claude.exe")
            .Where(p => !p.CommandLine.Contains(ChromeHelperFlag, StringComparison.Ordinal))
            .ToDictionary(p => p.Pid, p => p.Root);

    /// <summary>Session restore's map, off one registry read: each running window session's host, session id → host —
    /// the outermost <c>claude.exe</c> on each branch under the hosts, so a <c>claude -p</c> a session's own shell
    /// runs is none (VS Code health's busy gate keeps the any-depth walk) — joined to its registry row, one with no
    /// row yet (a session starting, the Chrome helper) left out; and every running session's id. Throws when the
    /// snapshot fails or holds none of the hosts, and when <c>claude.exe</c> runs under them while no claude pid is
    /// listed — the registry read returns empty on that failure, which would read as every session ended.</summary>
    public static (Dictionary<string, int> HostBySession, HashSet<string> LiveIds) SessionHosts(IReadOnlyCollection<int> hosts)
    {
        var outermost = Platform.Processes.OutermostNamed(hosts, "claude.exe")
                        ?? throw new InvalidOperationException("the process snapshot holds none of the windows' hosts");
        if (outermost.Count > 0 && Platform.Processes.LivePids("claude").Count == 0)
            throw new InvalidOperationException("claude.exe runs under the windows' hosts, yet no claude pid is listed");
        return Join(outermost, RunningByPid());
    }

    /// <summary>Claude processes joined to their registry rows — session id → host, one with no row left out — and every
    /// running session's id.</summary>
    private static (Dictionary<string, int> HostBySession, HashSet<string> LiveIds) Join(
        IEnumerable<(int Pid, int Root)> claude, Dictionary<int, (string SessionId, RunState Run)> rows)
    {
        var bySession = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (pid, root) in claude)
            if (rows.TryGetValue(pid, out var row)) bySession[row.SessionId] = root;
        return (bySession, rows.Values.Select(r => r.SessionId).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>The first reason a host's Claude sessions are busy, or null — every <c>claude.exe</c> anywhere under the
    /// host, so a process wrapper between them (<c>claudeCode.claudeProcessWrapper</c>) hides none. Fails closed: a
    /// process is idle only when its registry row reads <c>idle</c> and its session holds no WAITING card — a background
    /// task or wake-up a reload would cut. An empty status (an engine older than the field), any other status, or no
    /// row at all (a session starting, a file mid-write) is busy. Archived sessions count: archive hides, the process
    /// runs.</summary>
    private static string BusyWhy(IEnumerable<int> claudePids, IReadOnlyDictionary<int, (string SessionId, RunState Run)> rows,
                                  IReadOnlySet<string> awaiting)
    {
        foreach (int pid in claudePids)
        {
            string why = !rows.TryGetValue(pid, out var row) ? "a Claude process has no session registry entry"
                : awaiting.Contains(row.SessionId) ? "a session has background work in flight"
                : row.Run.Status switch
                {
                    "idle" => null,
                    "busy" => "a session is working",
                    "waiting" => "a session is waiting on you",
                    null or "" => "a session's Claude Code is too old to report whether it is working",
                    _ => $"a session reports {row.Run.Status}",
                };
            if (why != null) return why;
        }
        return null;
    }

    private static Dictionary<int, (string SessionId, RunState Run)> RunningByPid()
    {
        var rows = new Dictionary<int, (string SessionId, RunState Run)>();
        foreach (var r in LiveSessions.Running()) rows[r.Pid] = (r.SessionId, r.Run);
        return rows;
    }

    /// <summary>Each host's largest Claude panel heap: every webview whose URL carries the Claude extension's id, probed
    /// for its session and cwd as the session switch does, read with Runtime.getHeapUsage (the renderer's whole
    /// isolate, whose id the largest's reading carries), and placed by <see cref="HostOf"/>; a webview the probe cannot place takes the window of the placed
    /// ones sharing its parentId — the window's page target — and nothing when they disagree.</summary>
    public static async Task<Panels> ReadPanels(ServerRead servers, IReadOnlyDictionary<int, IReadOnlyList<string>> hostFolders)
    {
        var targets = await CdpClient.ListTargets();
        if (targets.Count == 0)
            return new Panels(false, new Dictionary<int, long>(), new Dictionary<int, string>(), new Dictionary<int, string>(),
                              $"debug port {CdpClient.Port} gave no VS Code target list — VS Code was not started with it, or another program holds it");
        var claude = CdpSwitcher.ClaudePanels(targets);
        var probes = claude.Count == 0 ? new List<CdpSwitcher.Probe>() : await CdpSwitcher.ProbeSidebars(claude);
        var hostById = new Dictionary<string, int>();       // webview target id → host
        var hostByParent = new Dictionary<string, int>();   // page target id → host; 0 where its placed panels disagree
        foreach (var p in probes)
        {
            int placed = HostOf(p, servers, hostFolders);
            if (placed == 0) continue;
            hostById[p.Wv.Id] = placed;
            if (!string.IsNullOrEmpty(p.Wv.ParentId))
                hostByParent[p.Wv.ParentId] = hostByParent.TryGetValue(p.Wv.ParentId, out var seen) && seen != placed ? 0 : placed;
        }
        var answered = probes.ToDictionary(p => p.Wv.Id);
        var mb = new Dictionary<int, long>();
        var errors = new Dictionary<int, string>();
        var isolates = new Dictionary<int, string>();
        bool blind = false;   // a Claude panel the probe could not place — no answer, or no session and no folder
        foreach (var wv in claude)
        {
            int host = hostById.TryGetValue(wv.Id, out var byId) ? byId
                     : !string.IsNullOrEmpty(wv.ParentId) && hostByParent.TryGetValue(wv.ParentId, out var byParent) ? byParent
                     : 0;
            if (host == 0)
            {
                // A panel whose session or folder belongs to a window outside health is no one's error.
                blind |= !answered.TryGetValue(wv.Id, out var p) || (string.IsNullOrEmpty(p.Sid) && string.IsNullOrEmpty(p.Cwd));
                continue;
            }
            var usage = await CdpClient.Send(wv.Ws, "Runtime.getHeapUsage", new { });
            if (usage is JsonElement u && u.TryGetProperty("usedSize", out var used) && used.TryGetDouble(out var bytes))
            {
                long m = (long)(bytes / (1024 * 1024)) / 10 * 10;
                if (!mb.TryGetValue(host, out var cur) || m > cur)
                {
                    mb[host] = m;
                    isolates[host] = await IsolateOf(wv.Ws);
                }
            }
            else errors[host] = "a Claude panel did not answer Runtime.getHeapUsage";
        }
        string error = claude.Count > 0 && probes.Count == 0
            ? "Claude panels found, none answered the probe on two reads running — signed out or still loading, else Claude Code changed its panel and Mastermind needs an update"
            : blind ? "a Claude panel could not be placed in its window on two reads running — signed out or still loading, else Claude Code changed its panel and Mastermind needs an update"
            : null;
        return new Panels(true, mb, errors, isolates, error);
    }

    /// <summary>A webview's V8 isolate id (Runtime.getIsolateId, experimental), null when it gives none — then no
    /// sharing is claimed for its window, and the heap reading stands alone.</summary>
    private static async Task<string> IsolateOf(string ws)
    {
        var r = await CdpClient.Send(ws, "Runtime.getIsolateId", new { });
        return r is JsonElement e && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    }

    /// <summary>A probed panel's window, 0 for none: its session's own <c>claude.exe</c>, under that window's host, off
    /// the server read's map; nothing for a session running anywhere else (a window outside health, a terminal);
    /// else — no session id, or none running — the window whose folder holding its cwd is the deepest, nothing when none
    /// does or two hold it at that depth.</summary>
    private static int HostOf(CdpSwitcher.Probe p, ServerRead servers, IReadOnlyDictionary<int, IReadOnlyList<string>> hostFolders)
    {
        int host = 0;
        if (!string.IsNullOrEmpty(p.Sid) && servers.HostBySession.TryGetValue(p.Sid, out var bySession)) host = bySession;
        else if (string.IsNullOrEmpty(p.Sid) || !servers.LiveIds.Contains(p.Sid))
        {
            // A window opened inside another window's folder is the nearer match, so its folder wins there; each holding
            // root is a prefix of the cwd's key, so the longer one is the deeper.
            var key = Paths.Key(p.Cwd);
            var holding = key.Length == 0
                ? new List<(int Host, int Depth)>()
                : hostFolders.Select(h => (Host: h.Key, Depth: h.Value.Select(Paths.Key).Where(f => Paths.Within(key, f)).Select(f => f.Length).DefaultIfEmpty(-1).Max()))
                             .Where(h => h.Depth >= 0).OrderByDescending(h => h.Depth).ToList();
            if (holding.Count == 1 || (holding.Count > 1 && holding[0].Depth > holding[1].Depth)) host = holding[0].Host;
        }
        return host;
    }
}
