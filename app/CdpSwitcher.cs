using System.Linq;
using System.Text.Json;

namespace Mastermind;

/// <summary>
/// Switches a VS Code window's Claude secondary-sidebar to a specific session by driving the extension's own
/// history-list click over CDP — the one action the extension exposes no command/URI/API for (verified by
/// reverse-engineering: the switch is webview-internal signal state, only reachable by a real click inside
/// the iframe). We reach the sidebar webview's nested React frame (same-origin <c>contentDocument</c>), click
/// the "Session history" button, then the session row carrying the target SESSION ID.
///
/// Identity is the real session id, not the title — see <see cref="CdpJs"/> for why (renames + duplicates) and
/// for the narrow title fallback that covers a Claude-update fiber drift.
///
/// We can't pick a window's sidebar up front (webviews aren't in the page's <c>getFrameTree</c>, every window
/// shares one <c>browserContextId</c>, and <c>document.visibilityState</c> is 'visible' for every non-minimized
/// window) — but we don't have to probe them all with the DROPDOWN either. The flow is two-phase:
///
///  1. PRE-PASS (parallel, read-only, no clicks): every webview answers "are you a Claude sidebar, which
///     session are you on, which cwd" (<see cref="ProbeJs"/> — activeSid + activeCwd off the same fiber).
///     A sidebar already ON the target short-circuits to AlreadyThere with zero dropdown flash. Otherwise the
///     caller's project cwd narrows the candidates to THAT project's sidebar(s) — a session's history rows
///     only exist in its own project's windows, so probing the rest was pure latency (each non-matching
///     sidebar cost a serial websocket round-trip + a 250ms dropdown wait; that latency also starved the
///     window-raise of its foreground grant, which is what made click-to-focus intermittently only flash).
///  2. SWITCH (serial over the shortlist, usually ONE sidebar): the dropdown probe, stop at first match.
///     Serial is load-bearing for the same-project-twice case — two windows on one project list the SAME
///     sessions, and a parallel probe could click the row in both, flipping a neighbouring window's sidebar.
///
/// AFTER the match we focus its exact host window. Primary raise: the matched webview's <c>parentId</c> page
/// target's TITLE equals the OS window title (verified live: /json/list page titles byte-match every Code.exe
/// top-level window title), so <see cref="IEditorWindows.RaiseByTitle"/> raises the HWND from OUR process —
/// no dependency on the <c>AllowSetForegroundWindow</c> grant surviving the async gap (the old CDP-side
/// <c>Page.bringToFront</c> ran inside Code.exe and could only flash the taskbar once the grant died).
/// The CDP raise is kept as the fallback when no window title matches (drift). Port off → no-op.
/// </summary>
internal static class CdpSwitcher
{
    public enum Result { PortOff, Switched, AlreadyThere, NotFound }

    /// <summary>The switch result plus HOW the session was identified. <paramref name="ViaTitle"/> true means
    /// the id path resolved nothing and we fell back to name matching — the loud signal that a Claude update
    /// moved the fiber path (callers log it).</summary>
    public readonly record struct Outcome(Result Result, bool ViaTitle);

    /// <summary>One Claude sidebar's read-only answer: which webview, the session it shows, that session's cwd.</summary>
    internal sealed record Probe(CdpClient.Target Wv, string Sid, string Cwd);

    /// <param name="sessionId">The real session id — the primary and only trustworthy key.</param>
    /// <param name="sessionTitle">Cached display title; used ONLY for the drift fallback.</param>
    /// <param name="focusWindow">When true, also raise the EXACT host window of the matched session to the
    /// foreground (the navigate case). Left false for send-a-prompt, which must NOT steal focus.</param>
    /// <param name="cwd">The session's project root (the card's Cwd) — narrows the dropdown probe to that
    /// project's sidebar(s). Null/empty = probe every Claude sidebar (the pre-narrowing behavior).</param>
    public static async Task<Outcome> TrySwitch(string sessionId, string sessionTitle, bool focusWindow = false, string cwd = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(sessionTitle))
            return new(Result.NotFound, false);
        var targets = await CdpClient.ListTargets();
        if (targets.Count == 0) return new(Result.PortOff, false);

        var webviews = Webviews(targets);
        if (webviews.Count == 0) return new(Result.NotFound, false);

        // ---- pass 1: parallel read-only probe (which sidebars exist, what's active where) --------------
        var sidebars = await ProbeSidebars(webviews);

        // A sidebar already ON the target: done — no dropdown, no flash (the common re-click case).
        var already = !string.IsNullOrEmpty(sessionId) ? sidebars.FirstOrDefault(p => p.Sid == sessionId) : null;
        if (already != null)
        {
            if (focusWindow) await Raise(targets, already.Wv);
            return new(Result.AlreadyThere, false);
        }

        var shortlist = Shortlist(sidebars, webviews, cwd);

        // ---- pass 2: the dropdown probe, serial over the shortlist, stop at first match ----------------
        var js = SwitchJs(sessionId, sessionTitle);
        foreach (var cand in shortlist)
        {
            var v = await CdpClient.Eval(cand.Wv.Ws, js);
            if (v is not JsonElement el || el.ValueKind != JsonValueKind.Object) continue;
            if (!(el.TryGetProperty("claude", out var c) && c.ValueKind == JsonValueKind.True)) continue;

            bool alreadyHere = el.TryGetProperty("already",  out var a) && a.ValueKind == JsonValueKind.True;
            bool switched    = el.TryGetProperty("switched", out var s) && s.ValueKind == JsonValueKind.True;
            if (!alreadyHere && !switched) continue;   // a Claude sidebar, but NOT this session — keep looking

            bool viaTitle = el.TryGetProperty("via", out var vi) && vi.ValueKind == JsonValueKind.String
                            && vi.GetString() == "title";

            if (focusWindow) await Raise(targets, cand.Wv);
            return new(alreadyHere ? Result.AlreadyThere : Result.Switched, viaTitle);
        }
        return new(Result.NotFound, false);
    }

    private static string Norm(string p) => Paths.Key(p);

    internal static List<CdpClient.Target> Webviews(List<CdpClient.Target> targets) =>
        targets.Where(t => !string.IsNullOrEmpty(t.Ws) && t.Url.StartsWith("vscode-webview://")).ToList();

    /// <summary>The webviews that are Claude panels — their URL carries the Claude extension's id.</summary>
    internal static List<CdpClient.Target> ClaudePanels(List<CdpClient.Target> targets) =>
        Webviews(targets).Where(t => t.Url.Contains("extensionId=Anthropic.claude-code", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Pass 1, shared by every sidebar action: every webview answers in parallel whether it is a Claude
    /// sidebar, which session it shows and that session's cwd (<see cref="ProbeJs"/> — no clicks).</summary>
    internal static async Task<List<Probe>> ProbeSidebars(List<CdpClient.Target> webviews)
    {
        var probed = await Task.WhenAll(webviews.Select(async wv =>
        {
            var v = await CdpClient.Eval(wv.Ws, ProbeJs);
            if (v is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
            if (!(el.TryGetProperty("claude", out var c) && c.ValueKind == JsonValueKind.True)) return null;
            string S(string k) => el.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            return new Probe(wv, S("sid"), S("cwd"));
        }));
        return probed.Where(p => p != null).ToList();
    }

    /// <summary>Every Claude panel's session right now, keyed by its webview target — what a new session's raise
    /// compares against (<see cref="RaiseMovedSidebar"/>). Empty with the port off.</summary>
    internal static async Task<Dictionary<string, string>> SidebarSessions()
    {
        var targets = await CdpClient.ListTargets();
        if (targets.Count == 0) return new();
        return (await ProbeSidebars(ClaudePanels(targets))).ToDictionary(p => p.Wv.Id, p => p.Sid);
    }

    /// <summary>Raise the window a new session just opened in: a Claude sidebar whose cwd sits within one of
    /// <paramref name="folders"/> — the chosen window's — and whose session differs from <paramref name="before"/>, or
    /// that was not there. A new session has no id to match, so the move is the signal, as
    /// the new-session dispatch reads it (CdpInjector.CreateJs); the folders keep another window's move, or a panel
    /// whose first probe failed, from raising the wrong window. Polled to a deadline checked between passes, since the
    /// sidebar loads the new session after the open returns. True only when the title raise landed — false with the
    /// port off, no sidebar moved or the raise missed, and the caller falls back to the title-scored raise.</summary>
    internal static async Task<bool> RaiseMovedSidebar(Dictionary<string, string> before, IReadOnlyList<string> folders,
                                                       int deadlineMs = 4000)
    {
        var roots = folders.Select(Paths.Key).ToList();
        var until = DateTime.UtcNow.AddMilliseconds(deadlineMs);
        while (true)
        {
            var targets = await CdpClient.ListTargets();
            if (targets.Count == 0) return false;
            var moved = (await ProbeSidebars(ClaudePanels(targets))).FirstOrDefault(p =>
                roots.Any(r => Paths.Within(Paths.Key(p.Cwd), r))
                && (!before.TryGetValue(p.Wv.Id, out var was) || p.Sid != was));
            if (moved != null) return await Raise(targets, moved.Wv);
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(300);
        }
    }

    /// <summary>
    /// Shortlist by project: a session's history rows only exist in its own project's windows. A sidebar
    /// launched from a subfolder reports that subfolder as its cwd while ours is the project root, so
    /// prefix-match (same rule as the new-session dispatch). No cwd / no match → all sidebars, and no sidebar
    /// probed at all → every webview (fail-open: a probe drift degrades to the slow path, never to a dead action).
    /// </summary>
    internal static List<Probe> Shortlist(List<Probe> sidebars, List<CdpClient.Target> webviews, string cwd)
    {
        if (sidebars.Count == 0) return webviews.Select(wv => new Probe(wv, null, null)).ToList();
        var dk = Norm(cwd);
        if (dk.Length == 0) return sidebars;
        var hits = sidebars.Where(p =>
        {
            var c0 = Norm(p.Cwd);
            return Paths.Within(c0, dk);
        }).ToList();
        return hits.Count > 0 ? hits : sidebars;
    }

    /// <summary>
    /// Raise the matched sidebar's host window. Primary: resolve the webview's <c>parentId</c> to its page
    /// target, whose TITLE equals the OS window title (verified live), and raise that HWND from OUR process
    /// via <see cref="IEditorWindows.RaiseByTitle"/> — immune to the foreground-grant race that made the
    /// CDP-side raise intermittently flash-only. Fallback: the old <c>Page.bringToFront</c> (needs the
    /// AllowSetForegroundWindow grant to still be alive) when no window title matches (drift / renamed title) or Windows refused the raise.
    /// True only when the title raise landed — the bringToFront fallback reports nothing back.
    /// </summary>
    private static async Task<bool> Raise(List<CdpClient.Target> targets, CdpClient.Target wv)
    {
        if (string.IsNullOrEmpty(wv.ParentId)) return false;
        var page = targets.FirstOrDefault(t => t.Id == wv.ParentId && t.Type == "page");
        if (page is null) return false;
        // Any thread: the platform raise marshals to the UI thread itself. A refused raise takes the CDP fallback a
        // missing title does — reporting a refusal as success was the bug that left a click on a taskbar flash.
        var raise = Platform.EditorWindows.RaiseByTitle(page.Title);
        if (raise == RaiseResult.Refused)
            Warnings.Add($"Windows kept the VS Code window back (taskbar flash) — trying VS Code's own raise · {page.Title}");
        bool raised = raise == RaiseResult.Raised;
        if (!raised && !string.IsNullOrEmpty(page.Ws))
            await CdpClient.BringToFront(page.Ws);
        return raised;
    }

    /// <summary>Read-only sidebar probe: is this a Claude sidebar, which session is it on, which project.
    /// No clicks, no dropdown — safe to run on every webview in parallel.</summary>
    private static readonly string ProbeJs =
        "(()=>{" + CdpJs.Frame + @"
const hist=[...d.querySelectorAll('button')].some(b=>b.getAttribute('aria-label')==='Session history');
if(!hist)return{claude:false};
" + CdpJs.SidOf + CdpJs.ActiveCwd + @"
return{claude:true,sid:activeSid(),cwd:activeCwd()};})()";

    /// <summary>
    /// Reach the Claude frame; if it's already on the target session return {already} with NO dropdown; else
    /// open Session history and click the row whose fiber carries the target session id.
    ///
    /// The title fallback engages ONLY when NO row yielded an id (the fiber path drifted). That gate is
    /// load-bearing: without it, "target id not in this list" (the normal case while iterating past other
    /// windows' sidebars) would degrade into a name guess and could click a DIFFERENT session that happens to
    /// share the title. When it does engage we require a UNIQUE name match for the same reason.
    /// </summary>
    private static string SwitchJs(string sessionId, string title)
    {
        return "(()=>{" + CdpJs.Frame + CdpJs.SidOf + CdpJs.TitleHit + CdpJs.Target(sessionId, title) + @"
const hb=[...d.querySelectorAll('button')].find(b=>b.getAttribute('aria-label')==='Session history');
if(!hb)return{claude:false};
" + CdpJs.OnTarget + @"
if(onTarget)return{claude:true,already:true,via};
hb.click();
return new Promise(res=>setTimeout(()=>{
const rows=[...d.querySelectorAll('button[class*=sessionItem]')];
const ids=rows.map(b=>sidOf(b));
let row=SID?rows.find((b,i)=>ids[i]===SID):null;
let how='id';
if(!row&&ids.every(x=>!x)){
  const nm=b=>{const n=b.querySelector('[class*=sessionName]');return n?n.innerText:'';};
  const cand=rows.filter(b=>titleHit(nm(b),TT));
  if(cand.length===1){row=cand[0];how='title';}
}
if(row){row.click();res({claude:true,switched:true,via:how});}
else{hb.click();res({claude:true,switched:false});}
},250));})()";
    }
}
