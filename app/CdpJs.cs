using System.Text.Json;

namespace Mastermind;

/// <summary>
/// The shared JS fragments every CDP action injects — one definition of "which session is this, really".
///
/// A session is identified by its REAL session id (the one in <c>~/.claude/sessions/&lt;pid&gt;.json</c> and the
/// <c>.jsonl</c> filename), read out of the Claude sidebar's React internals: walk UP the fiber chain from a DOM
/// element to <c>memoizedProps.session.sessionId.v</c>. Titles are NOT identity — the agent regenerates its
/// ai-title mid-session, so a cached title goes stale between the card's meta sweep and your click, and two
/// sessions can legitimately share one. Ids never change and never collide.
///
/// Beware the neighbour: <c>session.internalId</c> is a DIFFERENT UUID sitting right next to
/// <c>session.sessionId</c>. Only the latter is the id Mastermind's cards, registry and transcripts key on.
///
/// TITLE FALLBACK — deliberately narrow. The fiber path is an extension internal a Claude update can move
/// without warning; when it does, id matching returns nothing and every CDP action would silently no-op. So we
/// fall back to matching by title, but ONLY when the id path yielded nothing at all (genuine drift) — never as
/// a "couldn't find that id, guess by name" path, which would let an action land on the wrong session. Callers
/// surface the fallback loudly in the activity log; <see cref="SelectorSelfCheck"/> checks the anchor at startup.
/// </summary>
internal static class CdpJs
{
    /// <summary>Defines <c>sidOf(el)</c> (fiber walk → session id) and <c>activeSid()</c> (the session this
    /// sidebar is currently showing). The active anchors are the composer / send / menu button — measured;
    /// <c>titleText</c>'s ancestors carry no session prop, so it is NOT usable here.</summary>
    public const string SidOf = @"
const sidOf=(el)=>{if(!el)return null;
const k=Object.keys(el).find(x=>x.startsWith('__reactFiber$'));
let f=k?el[k]:null;
for(let i=0;f&&i<12;i++,f=f.return){const p=f.memoizedProps;
if(p&&p.session&&p.session.sessionId){const s=p.session.sessionId;
const v=(s&&typeof s==='object'&&'v' in s)?s.v:s;
if(typeof v==='string'&&v)return v;}}
return null;};
const activeSid=()=>sidOf(d.querySelector('[aria-label=""Message input""]'))
||sidOf(d.querySelector('button[class*=sendButton]'))
||sidOf(d.querySelector('button[class*=menuButton]'));
";

    /// <summary>Defines <c>activeCwd()</c> — the launch cwd of the session this sidebar currently shows, read off
    /// the same fiber as the id (memoizedProps.session.cwd). Like <c>sessionId</c>, <c>cwd</c> is a BOXED signal
    /// (<c>{v: "c:\…"}</c>, verified live 2.1.215 — NOT a bare string), so unbox it exactly as <see cref="SidOf"/>
    /// does. Value carries Windows backslashes; the caller normalizes. Used to target a window's sidebar when
    /// there is no session id yet (a just-created New session).</summary>
    public const string ActiveCwd = @"
const cwdOf=(el)=>{if(!el)return null;
const k=Object.keys(el).find(x=>x.startsWith('__reactFiber$'));
let f=k?el[k]:null;
for(let i=0;f&&i<12;i++,f=f.return){const p=f.memoizedProps;
if(p&&p.session&&p.session.cwd){const c=p.session.cwd;
const v=(c&&typeof c==='object'&&'v' in c)?c.v:c;
if(typeof v==='string'&&v)return v;}}
return null;};
const activeCwd=()=>cwdOf(d.querySelector('[aria-label=""Message input""]'))
||cwdOf(d.querySelector('button[class*=sendButton]'))
||cwdOf(d.querySelector('button[class*=menuButton]'));
";

    /// <summary>Defines <c>titleHit(candidate, TT)</c> — the FALLBACK comparison only. Tolerates the card
    /// title's display truncation (<see cref="Sessions"/> caps it and appends an ellipsis, which would make a
    /// bare <c>===</c> or prefix test fail against the sidebar's full name) by stripping the ellipsis and
    /// prefix-matching, with a length floor so a short stub can't match half the list.</summary>
    public const string TitleHit = @"
const titleHit=(g,TT)=>{g=(g||'').trim();if(!g||!TT)return false;
const t=TT.endsWith('…')?TT.slice(0,-1):TT;
return g===TT||g===t||(t.length>20&&g.startsWith(t));};
";

    /// <summary>Reach the Claude sidebar's nested React frame as <c>d</c>, bailing with <c>{claude:false}</c>
    /// when this webview isn't a Claude sidebar. Always the first statement of an injected script.</summary>
    public const string Frame = @"
const host=document.querySelector('iframe');const d=host&&host.contentDocument;
if(!d)return{claude:false};
";

    /// <summary>The target session as JS literals: <c>SID</c> (real id, may be '') and <c>TT</c> (cached title,
    /// fallback only).</summary>
    public static string Target(string sessionId, string title) =>
        "const SID=" + JsonSerializer.Serialize(sessionId ?? "") + ";"
      + "const TT=" + JsonSerializer.Serialize(title ?? "") + ";\n";

    /// <summary>
    /// "Is this sidebar currently showing the target session?" → sets <c>onTarget</c> and <c>via</c>.
    /// Id first; the title comparison runs ONLY when <c>activeSid()</c> resolved nothing (drift).
    /// </summary>
    public const string OnTarget = @"
const A=activeSid();
const via=A?'id':'title';
const onTarget=A?(A===SID)
 :titleHit((d.querySelector('[class*=titleText]')||{}).innerText,TT);
";
}
