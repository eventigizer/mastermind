using System.Text.Json;

namespace Mastermind;

/// <summary>
/// Sends a fixed prompt INTO a session by driving the Claude composer over CDP — the write-side sibling of
/// <see cref="CdpSwitcher"/> (which only navigates). We first switch the sidebar to the target session
/// (reusing the switch), then reach the same nested React frame, set the composer text and click send. The
/// composer is the stable <c>[aria-label="Message input"]</c> contenteditable; the send button is matched by
/// partial class (<c>button[class*=sendButton]</c>) since the hashed suffix changes per build. DOM-driven, so
/// it works on an UNFOCUSED window (no focus-steal — that's the whole point: reply to a waiting agent from the
/// panel). Submitting fires the session's UserPromptSubmit hook, so the card flips to WORKING on its own.
///
/// Every action re-confirms it is on the right sidebar by SESSION ID (<see cref="CdpJs.OnTarget"/>) before it
/// types or clicks — the guard that keeps a prompt or an ALLOW off the wrong session. Titles are not
/// identity; they only serve as the drift fallback documented in <see cref="CdpJs"/>. Best-effort: port off → no-op.
/// </summary>
internal static class CdpInjector
{
    public enum Result { PortOff, Sent, SwitchFailed, NoInput, DraftInWay, NotFound }

    private static List<CdpClient.Target> Webviews(List<CdpClient.Target> targets) => CdpSwitcher.Webviews(targets);

    private static bool Flag(JsonElement el, string key) =>
        el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;

    /// <summary>The skeleton every session-addressed action shares: switch the target session's sidebar into view
    /// (<paramref name="cwd"/> narrows the probe to its project), then run <paramref name="js"/> in each webview until
    /// one answers <c>{match:true}</c> — the script's own <see cref="CdpJs.OnTarget"/> check, so only the target
    /// session's sidebar ever acts. <c>Hit</c> is that answer, or null when no sidebar matched.</summary>
    private static async Task<(CdpSwitcher.Result Switch, JsonElement? Hit)> RunOnSession(
        string sessionId, string sessionTitle, string cwd, string js)
    {
        var sw = await CdpSwitcher.TrySwitch(sessionId, sessionTitle, cwd: cwd);
        if (sw.Result is CdpSwitcher.Result.PortOff or CdpSwitcher.Result.NotFound) return (sw.Result, null);
        foreach (var wv in Webviews(await CdpClient.ListTargets()))
        {
            var v = await CdpClient.Eval(wv.Ws, js);
            if (v is JsonElement el && el.ValueKind == JsonValueKind.Object && Flag(el, "match")) return (sw.Result, el);
        }
        return (sw.Result, null);
    }

    /// <param name="appendable">True (default) for PROSE prompts: a hand-typed draft already in the composer is
    /// KEPT — the prompt is appended after it (newline between) and the combined message sends, so you can write
    /// context by hand and finish with a canned command. False for SLASH COMMANDS (<c>/model …</c>): those only
    /// work as the WHOLE message — appended after a draft they'd arrive as plain prose and do nothing — so a
    /// non-empty composer returns <see cref="Result.DraftInWay"/> and nothing is touched. Never overwrites a
    /// draft either way (the old select-all + insertText silently destroyed unsent text).</param>
    public static async Task<Result> TrySend(string sessionId, string sessionTitle, string promptText, bool appendable = true, string cwd = null)
    {
        if ((string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(sessionTitle))
            || string.IsNullOrWhiteSpace(promptText))
            return Result.NotFound;

        // The switch puts the target session's sidebar on screen; the inject JS re-confirms by session id, so we
        // type into the right session — not whatever each sidebar happened to be on.
        var (sw, hit) = await RunOnSession(sessionId, sessionTitle, cwd, SendJs(sessionId, sessionTitle, promptText, appendable));
        if (sw == CdpSwitcher.Result.PortOff) return Result.PortOff;
        if (sw == CdpSwitcher.Result.NotFound) return Result.SwitchFailed;
        if (hit is not JsonElement el) return Result.NotFound;
        if (Flag(el, "sent")) return Result.Sent;
        if (Flag(el, "draft")) return Result.DraftInWay;
        return Result.NoInput;   // found the target sidebar but no composer (selector drift)
    }

    /// <summary>Reach the Claude frame; bail unless it is on the target SESSION (so we only type into the
    /// session we mean). Focus the composer and insert via <c>insertText</c> (fires the editor's real input
    /// events): an empty composer just takes the text; a hand-typed draft is APPENDED to (caret to end,
    /// newline between) when <c>APPEND</c>, else refused as <c>{draft:true}</c>. Then click send (Enter
    /// keydown as a fallback).</summary>
    private static string SendJs(string sessionId, string title, string text, bool appendable)
    {
        var tx = JsonSerializer.Serialize(text);
        var ap = appendable ? "true" : "false";
        return "(()=>{" + CdpJs.Frame + CdpJs.SidOf + CdpJs.TitleHit + CdpJs.Target(sessionId, title)
             + CdpJs.OnTarget + @"
if(!onTarget)return{match:false};
const inp=d.querySelector('[aria-label=""Message input""]');
if(!inp)return{match:true,sent:false};
const APPEND=" + ap + @";
const cur=((inp.innerText||inp.textContent||'').trim());
if(cur&&!APPEND)return{match:true,sent:false,draft:true};
inp.focus();
const sel=d.getSelection();const r=d.createRange();r.selectNodeContents(inp);
if(cur)r.collapse(false);   // draft present: caret to END (keep it) — else the selection replaces leftovers
sel.removeAllRanges();sel.addRange(r);
d.execCommand('insertText',false,(cur?'\n':'')+" + tx + @");
return new Promise(res=>setTimeout(()=>{
const btn=d.querySelector('button[class*=sendButton]');
if(btn&&!btn.disabled){btn.click();res({match:true,sent:true});}
else{inp.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true,cancelable:true}));res({match:true,sent:true});}
},140));})()";
    }

    // ----- new-session dispatch ----------------------------------------------
    public enum CreateResult { PortOff, Sent, NotReached, NoComposer }

    /// <summary>Create a NEW conversation in the DEST project's sidebar and submit the command. Matches the dest
    /// sidebar by its current session's CWD (a new session has no id), clicks the New-session button, waits
    /// for the fresh composer, then injects+sends. Iterates webviews but ACTS on exactly one — the dest
    /// match. Best-effort: port off → PortOff; no attachable dest sidebar → NotReached (caller surfaces a retry).</summary>
    public static async Task<(CreateResult result, string sid)> TryCreateAndSend(string destKey, string commandText)
    {
        if (string.IsNullOrWhiteSpace(destKey) || string.IsNullOrWhiteSpace(commandText)) return (CreateResult.NotReached, null);
        var targets = await CdpClient.ListTargets();
        if (targets.Count == 0) return (CreateResult.PortOff, null);
        var webviews = Webviews(targets);
        if (webviews.Count == 0) return (CreateResult.NotReached, null);

        var js = CreateJs(destKey, commandText);
        foreach (var wv in webviews)
        {
            var v = await CdpClient.Eval(wv.Ws, js, timeoutMs: 8000);
            if (v is not JsonElement el || el.ValueKind != JsonValueKind.Object) continue;
            if (!(el.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.True)) continue;   // not the dest sidebar
            if (el.TryGetProperty("sent", out var s) && s.ValueKind == JsonValueKind.True)
            {
                // The JS polls the new session's id back out after send (may be null if it never landed in the cap).
                var sid = el.TryGetProperty("sid", out var sidEl) && sidEl.ValueKind == JsonValueKind.String ? sidEl.GetString() : null;
                return (CreateResult.Sent, sid);
            }
            return (CreateResult.NoComposer, null);   // matched dest, but the fresh composer never came up
        }
        return (CreateResult.NotReached, null);   // dest sidebar not attachable in any webview
    }

    private static string CreateJs(string destKey, string text)
    {
        var tx = JsonSerializer.Serialize(text);
        var dk = JsonSerializer.Serialize(destKey);
        return "(()=>{" + CdpJs.Frame + CdpJs.SidOf + CdpJs.ActiveCwd + @"
const norm=s=>(s||'').replace(/\\/g,'/').replace(/\/+$/,'').toLowerCase();
const DK=" + dk + @";
// Match the dest window's sidebar by its CURRENT session's launch cwd. Prefix-match (== root OR under root),
// because a session launched from a subfolder reports that subfolder as its cwd while DK is the project root
// (finding H). A session-less sidebar yields '' which never matches — no click, no spray.
const c0=norm(activeCwd());
if(!(c0===DK||c0.startsWith(DK+'/')))return{match:false};
const nb=[...d.querySelectorAll('button')].find(b=>b.getAttribute('aria-label')==='New session');
if(!nb)return{match:true,sent:false};   // dest matched but the New-session anchor is gone (selector drift)
const before=activeSid();   // the dest session showing right now — the fresh session must differ from this
nb.click();
// Wait for the NEW session, not just an empty composer: an idle/finished OLD session already has an empty
// composer, so `empty` alone injects into the wrong conversation before createSession() swaps the DOM (finding
// B). Discriminator (verified live on 2.1.215): a new session shows activeSid()===null for a transient
// ~1.65s window, THEN self-assigns an id even with NO submit — so `===null` alone is timing-fragile. Accept
// EITHER null OR an id different from the pre-click `before`, so a slightly-late first poll still fires into the
// new session. Both fail-safe on the tab-open config (openNewInTab): there the sidebar keeps the OLD session, so
// activeSid() stays === before (never null, never changed) → the poll times out → sent:false (finding C).
return new Promise(res=>{let n=0;const iv=setInterval(()=>{
  n++;
  const inp=d.querySelector('[aria-label=""Message input""]');
  const cur=activeSid();
  const fresh=cur===null||(before!==null&&cur!==before);   // new empty session: null, or a different id than before
  const empty=inp&&!((inp.innerText||inp.textContent||'').trim());
  if(inp&&empty&&fresh){clearInterval(iv);
    inp.focus();
    const sel=d.getSelection();const r=d.createRange();r.selectNodeContents(inp);sel.removeAllRanges();sel.addRange(r);
    d.execCommand('insertText',false," + tx + @");
    setTimeout(()=>{const btn=d.querySelector('button[class*=sendButton]');
      if(btn&&!btn.disabled){btn.click();}
      else{inp.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true,cancelable:true}));}
      // The new session self-assigns its id within ~1.65s of mounting; poll activeSid() until it lands so the
      // owner map records the REAL id (not the transient null). Bounded — resolve sent regardless after the cap.
      let m=0;const iv2=setInterval(()=>{m++;const nsid=activeSid();
        if(nsid){clearInterval(iv2);res({match:true,sent:true,sid:nsid});}
        else if(m>16){clearInterval(iv2);res({match:true,sent:true,sid:null});}
      },120);},140);
    return;}
  if(n>30){clearInterval(iv);res({match:true,sent:false});}   // ~4.5s cap → NoComposer (dest matched, new session never mounted)
},150);});})()";
    }

    // ----- #5 per-prompt allow / deny ----------------------------------------
    public enum PermissionResult { PortOff, Answered, SwitchFailed, NoDialog }

    /// <summary>
    /// Answer a single pending permission from the card — <paramref name="allow"/> clicks the FIRST action
    /// button (allow-once), else the LAST (deny). The dialog's option COUNT varies (2/3/4) so we pick by
    /// POSITION, never an absolute number; the middle buttons are the broader "allow for all" grants we
    /// deliberately skip (allow-once is the point — auto-allow is the blanket path). Switch to the session's
    /// sidebar first. Caller handles the SwitchFailed case (a backgrounded permission isn't in CDP until its
    /// window is raised — see the draft constraint).
    /// </summary>
    public static async Task<PermissionResult> TryAnswerPermission(string sessionId, string sessionTitle, bool allow, string cwd = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(sessionTitle))
            return PermissionResult.NoDialog;

        var (sw, hit) = await RunOnSession(sessionId, sessionTitle, cwd, PermissionJs(sessionId, sessionTitle, allow));
        if (sw == CdpSwitcher.Result.PortOff) return PermissionResult.PortOff;
        if (sw == CdpSwitcher.Result.NotFound) return PermissionResult.SwitchFailed;
        // No matching sidebar, or matched but no pending dialog → NoDialog either way.
        return hit is JsonElement el && Flag(el, "clicked") ? PermissionResult.Answered : PermissionResult.NoDialog;
    }

    private static string PermissionJs(string sessionId, string title, bool allow)
    {
        var pick = allow ? "btns[0]" : "btns[btns.length-1]";   // first = allow-once, last = deny
        // The permission action buttons carry a lowercase `button_<hash>` class (the icon/copy/send buttons are
        // capital-B `iconButton_`/`copyButton_`/`sendButton_` — excluded by the lowercase-`button_` filter).
        return "(()=>{" + CdpJs.Frame + CdpJs.SidOf + CdpJs.TitleHit + CdpJs.Target(sessionId, title)
             + CdpJs.OnTarget + @"
if(!onTarget)return{match:false};
const btns=[...d.querySelectorAll('[class*=permissionRequest] button')].filter(b=>/(^|\s)button_/.test(b.className||''));
if(!btns.length)return{match:true,clicked:false};
const t=" + pick + @";
t.click();
return{match:true,clicked:true,label:((t.innerText||'').trim().slice(0,40))};})()";
    }

    // ----- archive a session -------------------------------------------------
    public enum ArchiveResult { PortOff, Archived, NotFound, NoButton, SwitchFailed }

    /// <summary>The archive outcome plus the session the sidebar was moved onto first (null when it was showing
    /// another session already, or the project had no other live session to move to).</summary>
    public readonly record struct ArchiveOutcome(ArchiveResult Result, string SwitchedTo);

    /// <summary>
    /// Archive a session through the extension's OWN row button — the session-history dropdown's
    /// <c>title="Archive session"</c> icon — so the extension does its own bookkeeping: the archived set written
    /// through its memento and that window's other lists refreshed. It exposes no command or URI for
    /// it. Identity is the session id only; there is no title fallback, since a name match could put the wrong
    /// session away.
    ///
    /// Archiving the session a sidebar is SHOWING makes the extension open a blank new session there. So when the
    /// target is the active one, the script first switches that sidebar to the first of <paramref name="neighbours"/>
    /// it lists, then archives; with no neighbour listed it archives anyway and the extension opens its blank
    /// session. A sidebar showing the target is tried first, because that is the one the extension would blank.
    /// </summary>
    public static async Task<ArchiveOutcome> TryArchive(string sessionId, string cwd, IReadOnlyList<string> neighbours)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return new(ArchiveResult.NotFound, null);
        var targets = await CdpClient.ListTargets();
        if (targets.Count == 0) return new(ArchiveResult.PortOff, null);
        var webviews = Webviews(targets);
        if (webviews.Count == 0) return new(ArchiveResult.NotFound, null);

        var sidebars = await CdpSwitcher.ProbeSidebars(webviews);
        var shortlist = CdpSwitcher.Shortlist(sidebars, webviews, cwd).OrderBy(p => p.Sid == sessionId ? 0 : 1).ToList();
        var js = ArchiveJs(sessionId, neighbours ?? Array.Empty<string>());
        foreach (var cand in shortlist)
        {
            var v = await CdpClient.Eval(cand.Wv.Ws, js, timeoutMs: 9000);
            if (v is not JsonElement el || el.ValueKind != JsonValueKind.Object || !Flag(el, "match")) continue;
            var to = el.TryGetProperty("switchedTo", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (Flag(el, "archived")) return new(ArchiveResult.Archived, to);
            if (Flag(el, "switchFailed")) return new(ArchiveResult.SwitchFailed, null);
            return new(ArchiveResult.NoButton, to);
        }
        return new(ArchiveResult.NotFound, null);
    }

    /// <summary>Open Session history, find the target row by session id, move the sidebar off it when it is the
    /// active session, click the row's archive icon, close the dropdown. The icon is hover-revealed CSS
    /// (<c>visibility:hidden</c>), which a scripted <c>click()</c> ignores.</summary>
    private static string ArchiveJs(string sessionId, IReadOnlyList<string> neighbours)
    {
        return "(()=>{" + CdpJs.Frame + CdpJs.SidOf + @"
const SID=" + JsonSerializer.Serialize(sessionId) + @";
const NB=" + JsonSerializer.Serialize(neighbours) + @";
const hb=[...d.querySelectorAll('button')].find(b=>b.getAttribute('aria-label')==='Session history');
if(!hb)return{claude:false};
const wait=ms=>new Promise(r=>setTimeout(r,ms));
const rows=()=>[...d.querySelectorAll('button[class*=sessionItem]')];
const open=async()=>{if(!rows().length){hb.click();await wait(250);}return rows();};
const close=()=>{if(rows().length)hb.click();};
const rowOf=(list,id)=>list.find(b=>sidOf(b)===id);
return (async()=>{
let list=await open();
let row=rowOf(list,SID);
if(!row){close();return{claude:true,match:false};}
let switchedTo=null;
if(activeSid()===SID){
  const nb=NB.find(id=>rowOf(list,id));
  if(nb){
    rowOf(list,nb).click();
    let n=0;while(activeSid()!==nb&&n<30){await wait(100);n++;}
    if(activeSid()!==nb){close();return{claude:true,match:true,switchFailed:true};}
    switchedTo=nb;
    list=await open();
    row=rowOf(list,SID);
    if(!row){close();return{claude:true,match:true,archived:false,switchedTo};}
  }
}
const btn=row.querySelector('[title=""Archive session""]');
if(!btn){close();return{claude:true,match:true,archived:false,switchedTo};}
btn.click();
await wait(300);
close();
return{claude:true,match:true,archived:true,switchedTo};
})();})()";
    }
}
