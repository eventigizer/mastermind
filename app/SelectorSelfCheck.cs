using System.Linq;
using System.Text.Json;

namespace Mastermind;

/// <summary>
/// #7 — CDP selector self-check. When a Claude-extension update renames the DOM classes, every CDP action
/// (switch / send / interrupt / per-prompt allow-deny) silently dies — you click and nothing happens. This
/// confirms the load-bearing, ALWAYS-present selectors still resolve in a live Claude sidebar, so a rename is
/// LOUD (a warning in the log) instead of silent. It only checks the selectors that exist in every state — the
/// permission-dialog / stop selectors are live-state-only and can't be probed when nothing's pending.
/// </summary>
internal static class SelectorSelfCheck
{
    // Always-present anchors. Live-state-only ones (permissionRequest, the sendButton-as-stop) are intentionally
    // out — their absence on an idle/empty sidebar is normal, not drift.
    private static readonly string[] Required = { "titleText", "sessionHistory", "messageInput", "sendButton", "menuButton", "sessionId", "newSession" };

    /// <summary>One line per problem (missing selector, or "can't check" when the port's off / no sidebar is
    /// rendered); empty list = all anchors resolved. Best-effort and read-only (no clicks).</summary>
    public static async Task<List<string>> RunAsync()
    {
        var targets = await CdpClient.ListTargets();
        if (targets.Count == 0) return new() { "selector self-check skipped — debug port off" };

        var webviews = targets.Where(t => !string.IsNullOrEmpty(t.Ws) && t.Url.StartsWith("vscode-webview://")).ToList();
        int claudeSidebars = 0;
        var present = new HashSet<string>();

        foreach (var wv in webviews)
        {
            var v = await CdpClient.Eval(wv.Ws, CheckJs);
            if (v is not JsonElement el || el.ValueKind != JsonValueKind.Object) continue;
            if (!(el.TryGetProperty("claude", out var c) && c.ValueKind == JsonValueKind.True)) continue;
            claudeSidebars++;
            if (el.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.Array)
                foreach (var s in ok.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.String) present.Add(s.GetString() ?? "");
        }

        if (claudeSidebars == 0)
            return new() { "selector self-check skipped — no Claude sidebar rendered (open a Claude panel)" };

        // A selector found in ANY rendered Claude sidebar counts as present (a backgrounded sidebar may be absent
        // from CDP entirely; that's not a rename).
        var missing = Required.Where(r => !present.Contains(r)).ToList();
        return missing.Select(m => m == "sessionId"
            ? "CDP session-id path MISSING — every action has silently fallen back to matching by NAME "
              + "(not rename- or duplicate-proof). Claude Code changed its panel — update Mastermind"
            : $"CDP selector MISSING: '{m}' — Claude extension renamed its DOM? fix in CdpSwitcher/CdpInjector").ToList();
    }

    // `sessionId` is not a DOM selector but the React-fiber path every CDP action identifies a session by
    // (CdpJs.SidOf). It drifts independently of the class names, and its loss silently demotes every action to
    // name matching — so it is checked here alongside them.
    private const string CheckJs = @"(()=>{const host=document.querySelector('iframe');const d=host&&host.contentDocument;
if(!d)return{claude:false};
const hist=[...d.querySelectorAll('button')].some(b=>b.getAttribute('aria-label')==='Session history');
if(!hist)return{claude:false};
const ok=['sessionHistory'];
if(d.querySelector('[class*=titleText]'))ok.push('titleText');
if(d.querySelector('[aria-label=""Message input""]'))ok.push('messageInput');
if(d.querySelector('button[class*=sendButton]'))ok.push('sendButton');
if(d.querySelector('button[class*=menuButton]'))ok.push('menuButton');
if([...d.querySelectorAll('button')].some(b=>b.getAttribute('aria-label')==='New session'))ok.push('newSession');
" + CdpJs.SidOf + @"
if(activeSid())ok.push('sessionId');
return{claude:true,ok};})()";
}
