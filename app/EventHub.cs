using System.IO;
using System.Net;
using System.Text;

namespace Mastermind;

/// <summary>
/// Receives Claude Code's raw hook payloads: HTTP POST to http://127.0.0.1:PORT/hook — from the background
/// <c>curl</c> command hook every event but PermissionRequest runs, and from PermissionRequest's own HTTP hook, the
/// one kind whose response carries an answer: allow for an auto-allowed session, else an empty 204. Every
/// other answer is an empty 204 — a background hook's JSON would reach the model on its next turn. Parses the body
/// once on the listener thread (<see cref="HookPayload"/>) and raises <see cref="EventReceived"/>; the UI layer
/// marshals it onto the dispatcher. An event sent while the exe is down is lost — nothing queues it; the registry
/// correction restores each card once it is back.
/// </summary>
internal sealed class EventHub : IDisposable
{
    /// <summary>config's hookPort — 7778 by default, Mastermind's own. Each project's hook entries post to it.</summary>
    public static int Port => Config.Current.HookPort;

    /// <summary>The URL Mastermind's hook entries post to — the writer (HookEntries) and the check (HookDrift) build it
    /// here, so the two never disagree on it.</summary>
    public static string HookUrl => $"http://127.0.0.1:{Port}/hook";

    public enum BindFailure { Busy, Reserved, Denied, Invalid }

    /// <summary>A bind failure in words — the startup warning and ConfigWriter's refusal of a new hookPort.</summary>
    public static string Describe(BindFailure cause, Exception ex) => cause switch
    {
        BindFailure.Busy => "busy",
        BindFailure.Reserved => "reserved by Windows (an excluded port range)",
        BindFailure.Denied => "reserved for another account (netsh http show urlacl)",
        _ => $"invalid ({ex.Message})",
    };

    /// <summary>
    /// Why <see cref="Start"/> could not bind <see cref="Port"/>, read off http.sys's error code (measured on build
    /// 26300). 183: another http.sys listener holds the prefix — Busy. 32: a plain socket holds the port — Busy — or
    /// it sits in a range Windows excluded at boot (Hyper-V, WSL and Docker reserve TCP ranges —
    /// <c>netsh int ipv4 show excludedportrange protocol=tcp</c>) — Reserved, which no retry frees. 5: a URL reservation
    /// for another account covers the port (<c>netsh http show urlacl</c> — Windows ships several) — Denied — or, again,
    /// an excluded range: field reports show an excluded port answering either code. Only a 32 or a 5
    /// consults that list: http.sys lists every port it listens on there too, so a port another listener holds (183)
    /// would read as excluded. Invalid: the listener refused the prefix itself. Anything else reads as Busy, which
    /// retries.
    /// </summary>
    public static BindFailure Classify(Exception ex) => Classify(ex, Port);

    /// <summary><see cref="Classify(Exception)"/> for a port other than this run's — ConfigWriter's check of a new
    /// hookPort.</summary>
    public static BindFailure Classify(Exception ex, int port)
    {
        if (ex is ArgumentException || ex is HttpListenerException { ErrorCode: 87 }) return BindFailure.Invalid;   // ERROR_INVALID_PARAMETER
        if (ex is HttpListenerException { ErrorCode: 5 or 32 } hle)                                                  // ACCESS_DENIED / SHARING_VIOLATION
        {
            foreach (var (lo, hi) in ExcludedRanges())
                if (port >= lo && port <= hi) return BindFailure.Reserved;
            if (hle.ErrorCode == 5) return BindFailure.Denied;
        }
        return BindFailure.Busy;
    }

    // A Windows call outside the platform seam (Platform.cs): it runs only after a failed bind, and the Avalonia phase
    // moves it with the rest.
    private static List<(int Lo, int Hi)> ExcludedRanges()
    {
        var ranges = new List<(int Lo, int Hi)>();
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "netsh", "int ipv4 show excludedportrange protocol=tcp")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            // A plain ReadToEnd waits as long as netsh keeps its output open, so the 3 s bound sits on the read itself:
            // a hung netsh never holds startup, which runs this on the UI thread.
            var read = p.StandardOutput.ReadToEndAsync();
            if (!read.Wait(3000)) { try { p.Kill(); } catch { } return ranges; }
            var text = read.Result;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         text, @"^\s*(\d+)\s+(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline))
                ranges.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        }
        catch { /* can't tell — read as busy, which retries */ }
        return ranges;
    }

    /// <summary>Null when this process can listen on <paramref name="port"/> now, else why not — ConfigWriter's check
    /// before a new hookPort is written, since a run that cannot bind it has no channel left for the setting that would
    /// undo it.</summary>
    public static string CanBind(int port)
    {
        var probe = new HttpListener();
        try
        {
            probe.Prefixes.Add($"http://127.0.0.1:{port}/");
            probe.Start();
            return null;
        }
        catch (Exception ex)
        {
            return Describe(Classify(ex, port), ex);
        }
        finally { try { probe.Close(); } catch { } }
    }

    public event Action<HookPayload>? EventReceived;

    /// <summary>A refused /hook post or /client upgrade, once per door and status code per run, with the client's
    /// user agent: an engine that starts sending Origin or a body something rewrote is otherwise silence, and the
    /// symptom warning would name the wrong cause. Raised on the listener thread.</summary>
    public event Action<string>? Refused;
    private readonly HashSet<string> _refused = new();   // "<door> <code>" — one accept loop at a time, no lock

    /// <summary>A <c>GET /client</c> WebSocket upgrade — a VS Code window's shell — handed off whole: the subscriber
    /// accepts it on its own task and owns the context from then on. Raised on the listener thread.</summary>
    public event Action<HttpListenerContext>? ClientRequested;

    private HttpListener _listener = new();
    private bool _running;

    /// <summary>True once the HTTP listener is bound; false while a busy port keeps us offline.</summary>
    public bool IsListening { get; private set; }

    public void Start() => Bind();   // throws on a busy port — App catches it and starts the rebind loop

    private void Bind()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _running = true;
        IsListening = true;
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Retry binding after a busy-port startup. Returns true once live.</summary>
    public bool TryRebind()
    {
        if (IsListening) return true;
        try { _listener = new HttpListener(); Bind(); }
        catch { return false; }
        return true;
    }

    private async Task LoopAsync()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; } // listener stopped

            bool handedOff = false;
            try
            {
                var req = ctx.Request;
                // A window's shell: handed off ahead of KeepAlive below and outside the finally's Close — either one,
                // reached, ends the socket the upgrade hands over. WebSockets skip CORS, so the Origin refusal /hook
                // makes is this door's guard too: a browser always sends Origin, the extension host's WebSocket never.
                if (req.IsWebSocketRequest && req.Url?.AbsolutePath.TrimEnd('/') == "/client")
                {
                    if (req.Headers.Get("Origin") != null) Refuse(ctx, 403, "Origin header (a browser)", "/client upgrade");
                    else if (ClientRequested is { } accept) { handedOff = true; accept(ctx); }
                    else ctx.Response.StatusCode = 503;
                    continue;
                }
                // One request per connection: a client reusing a pooled socket the listener already closed (its idle
                // timeout, a restart) fails that POST outright, and a permission answer then falls to the dialog.
                ctx.Response.KeepAlive = false;
                if (req.HttpMethod == "POST" && req.Url?.AbsolutePath.TrimEnd('/') == "/hook")
                {
                    // The listener answers any Host, so a DNS-rebinding page reaches the port: a browser always sends
                    // Origin, while Claude Code's client and curl never do, and both send application/json.
                    if (req.Headers.Get("Origin") != null) Refuse(ctx, 403, "Origin header (a browser)");
                    else if (!(req.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                        Refuse(ctx, 415, "not application/json");
                    else
                    {
                        // Sized from Content-Length when sent: a large tool event otherwise regrows the buffer by
                        // doubling, each copy left as garbage.
                        long sent = req.ContentLength64;
                        using var body = new MemoryStream(sent > 0 && sent <= int.MaxValue ? (int)sent : 0);
                        await req.InputStream.CopyToAsync(body);
                        var payload = HookPayload.Parse(body.GetBuffer(), (int)body.Length);
                        if (payload == null) Refuse(ctx, 400, "body not a JSON object");
                        else
                        {
                            var answer = Answer(payload, req.UserAgent);
                            EventReceived?.Invoke(payload);
                            if (answer == null) ctx.Response.StatusCode = 204;
                            else
                            {
                                ctx.Response.StatusCode = 200;
                                ctx.Response.ContentType = "application/json";
                                ctx.Response.OutputStream.Write(answer, 0, answer.Length);
                            }
                        }
                    }
                }
                else
                {
                    // /ping or anything else -> liveness probe
                    var ok = Encoding.UTF8.GetBytes("Mastermind");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.OutputStream.Write(ok, 0, ok.Length);
                }
            }
            catch { /* never let a bad request take the loop down */ }
            finally { if (!handedOff) try { ctx.Response.Close(); } catch { } }
        }
    }

    /// <summary>A refusal, reported once per door and status code per run — a page probing /client never uses up the
    /// line a /hook refusal would raise.</summary>
    private void Refuse(HttpListenerContext ctx, int code, string cause, string door = "/hook post")
    {
        ctx.Response.StatusCode = code;
        if (_refused.Add($"{door} {code}"))
            Refused?.Invoke($"{door} · {code} {cause} · first from {ctx.Request.UserAgent ?? "no user agent"}");
    }

    /// <summary>The body answering a PermissionRequest — allow, for an auto-allowed session's own claude process
    /// (AutoAllowStore.Allows) — else null, an empty 204.
    /// Never to curl: a command hook's stdout is its output.</summary>
    private static byte[] Answer(HookPayload p, string userAgent)
    {
        if (p.Event != "PermissionRequest") return null;
        if ((userAgent ?? "").StartsWith("curl/", StringComparison.OrdinalIgnoreCase)) return null;
        return AutoAllowStore.Allows(p.SessionId, LiveSessions.Newest(p.SessionId)?.Pid ?? 0, tidy: true) ? AutoAllowStore.AllowBody : null;
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
