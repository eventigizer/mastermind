using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Mastermind;

/// <summary>
/// Tiny Chrome DevTools Protocol client against VS Code's remote-debugging port — config's debugPort (9222 on this
/// fleet, set in the user's <c>~/.vscode/argv.json</c>; 0, the default, is off). Used to switch the Claude sidebar to a specific session — the one
/// thing the extension exposes no command/URI/API for (see <see cref="CdpSwitcher"/>). No deps: HttpClient
/// for the <c>/json</c> endpoints + ClientWebSocket for one-shot CDP calls. Everything is best-effort: if
/// the port isn't up (VS Code started without the flag), every call returns empty/null and the caller
/// falls back to plain window focus.
/// </summary>
internal static class CdpClient
{
    public static int Port => Config.Current.DebugPort;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(800) };
    private static bool _foreignWarned;   // one warning per stretch of a foreign answer, not one per action

    // ParentId: a webview/iframe target's parent target id — for a Claude sidebar webview this resolves to
    // its host window's `page` target, which is how we focus the EXACT window a session lives in (see
    // CdpSwitcher). Empty on page targets.
    internal sealed record Target(string Id, string Type, string Title, string Url, string Ws, string ParentId);

    /// <summary>
    /// Every target on the debug port — none when the port is off (0), down, or answered by something that is not a
    /// VS Code-family editor, so every caller's PortOff path covers all three. A VS Code is told apart by a
    /// <c>vscode-webview://</c> target or a <c>vscode-file://</c> page — every window's workbench page carries the
    /// second, so a VS Code whose Claude sidebars are all backgrounded (their webviews drop out of CDP) still reads as
    /// VS Code; a browser carries neither. <c>/json/version</c> is no test, its Browser field reads Chrome for VS Code
    /// too. A foreign answer warns once until a VS Code list is seen again.
    /// </summary>
    public static async Task<List<Target>> ListTargets()
    {
        var list = new List<Target>();
        if (Port == 0) return list;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"http://127.0.0.1:{Port}/json/list"));
            foreach (var t in doc.RootElement.EnumerateArray())
                list.Add(new Target(Json.Str(t, "id"), Json.Str(t, "type"), Json.Str(t, "title"),
                                     Json.Str(t, "url"), Json.Str(t, "webSocketDebuggerUrl"), Json.Str(t, "parentId")));
        }
        catch { return new List<Target>(); /* port down */ }
        if (list.Count == 0) return list;
        if (!list.Any(IsVsCodeTarget))
        {
            if (!_foreignWarned)
            {
                _foreignWarned = true;
                Warnings.Add($"debug port {Port} is answered by something that is not VS Code (no vscode-webview or vscode-file target) — treated as off");
            }
            return new List<Target>();
        }
        _foreignWarned = false;
        return list;
    }

    /// <summary>A target only a VS Code-family editor serves: a webview, or a window's workbench page. Identity only —
    /// what an action drives is still <see cref="CdpSwitcher.Webviews"/>' pick.</summary>
    private static bool IsVsCodeTarget(Target t) =>
        t.Url.StartsWith("vscode-webview://", StringComparison.Ordinal)
        || (t.Type == "page" && t.Url.StartsWith("vscode-file://", StringComparison.Ordinal));

    /// <summary>Raise a page target's OS window to the foreground. Verified to focus the whole Electron
    /// window (not just activate a tab) on VS Code. Best-effort — null/empty ws is a no-op.</summary>
    public static async Task BringToFront(string wsUrl) => await Send(wsUrl, "Page.bringToFront", new { });

    /// <summary>One request/response CDP call on a target's websocket. Returns the message's <c>result</c>
    /// object, or null on any failure. Skips interleaved event messages (those carry no matching id).</summary>
    public static async Task<JsonElement?> Send(string wsUrl, string method, object @params, int timeoutMs = 4000)
    {
        if (string.IsNullOrEmpty(wsUrl)) return null;
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
            var msg = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method, @params });
            await ws.SendAsync(msg, WebSocketMessageType.Text, true, cts.Token);

            var buf = new byte[64 * 1024];
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                ms.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;   // one CDP message can span frames — buffer all bytes first
                // Decode the WHOLE message once (a multi-byte UTF-8 char can straddle a frame boundary, so
                // per-frame decoding would corrupt it — e.g. a non-ASCII session title in getFrameTree).
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) && id == 1)
                    return root.TryGetProperty("result", out var res) ? res.Clone() : (JsonElement?)null;
                ms.SetLength(0);   // an event came first — drop it and keep reading for our reply
            }
        }
        catch { return null; }
    }

    /// <summary>Runtime.evaluate (awaiting promises, by-value) and return just the resolved JS value.</summary>
    public static async Task<JsonElement?> Eval(string wsUrl, string expression, int timeoutMs = 4000)
    {
        var res = await Send(wsUrl, "Runtime.evaluate",
            new { expression, returnByValue = true, awaitPromise = true }, timeoutMs);
        if (res is JsonElement r && r.TryGetProperty("result", out var ro) && ro.TryGetProperty("value", out var val))
            return val.Clone();
        return null;
    }
}
