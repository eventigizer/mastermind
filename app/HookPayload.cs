using System.Text.Json;

namespace Mastermind;

/// <summary>One in-flight background task off a Stop payload's <c>background_tasks</c>.</summary>
internal readonly record struct HookTask(string Type, string Description, string Command);

/// <summary>
/// The fields Mastermind reads off Claude Code's raw hook payload, taken in ONE forward pass over the body's bytes
/// (<see cref="Utf8JsonReader"/>). A raw <c>PostToolUse</c> can carry megabytes of <c>tool_response</c>: every value
/// not named here is skipped token by token, never built into a string or a document.
/// </summary>
internal sealed class HookPayload
{
    public string Event { get; private set; } = "";
    public string SessionId { get; private set; } = "";
    public string Cwd { get; private set; } = "";
    public string ToolName { get; private set; } = "";
    /// <summary>The string-valued <c>tool_input</c> keys the action phrases read (<see cref="ToolInputKeys"/>).</summary>
    public Dictionary<string, string> ToolInput { get; } = new(StringComparer.Ordinal);
    public string Prompt { get; private set; } = "";
    public string Message { get; private set; } = "";
    public string NotificationType { get; private set; } = "";
    public string LastAssistantMessage { get; private set; } = "";
    /// <summary>Null when the payload carries no list — an engine before v2.1.145.</summary>
    public List<HookTask> BackgroundTasks { get; private set; }
    /// <summary>Each session cron's <c>schedule</c>, the cron expression it fires on; null as above.</summary>
    public List<string> SessionCrons { get; private set; }
    public string ToModel { get; private set; } = "";
    /// <summary>Set only when the hook fired inside a subagent call — the event still carries the parent's session id.</summary>
    public string AgentId { get; private set; } = "";

    private static readonly HashSet<string> ToolInputKeys = new(StringComparer.Ordinal)
    { "command", "file_path", "notebook_path", "pattern", "url", "query", "description", "subagent_type" };
    private static readonly HashSet<string> TaskKeys = new(StringComparer.Ordinal) { "type", "description", "command" };
    private static readonly HashSet<string> CronKeys = new(StringComparer.Ordinal) { "schedule" };

    // A tool_response nests as deep as the tool's own output; past the reader's default of 64 it would refuse the body.
    private static readonly JsonReaderOptions Options = new() { MaxDepth = 512 };

    /// <summary>The payload in <paramref name="buffer"/>'s first <paramref name="length"/> bytes, or null when they are
    /// not a JSON object (the crash log says why).</summary>
    public static HookPayload Parse(byte[] buffer, int length, bool quiet = false)
    {
        try
        {
            var r = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer, 0, length), Options);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return null;
            var p = new HookPayload();
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var name = r.GetString();
                r.Read();
                switch (name)
                {
                    case "hook_event_name":        p.Event = Str(ref r); break;
                    case "session_id":             p.SessionId = Str(ref r); break;
                    case "cwd":                    p.Cwd = Str(ref r); break;
                    case "tool_name":              p.ToolName = Str(ref r); break;
                    case "tool_input":             ReadObject(ref r, ToolInputKeys, p.ToolInput); break;
                    case "prompt":                 p.Prompt = Str(ref r); break;
                    case "message":                p.Message = Str(ref r); break;
                    case "notification_type":      p.NotificationType = Str(ref r); break;
                    case "last_assistant_message": p.LastAssistantMessage = Str(ref r); break;
                    case "background_tasks":
                        p.BackgroundTasks = ReadObjects(ref r, TaskKeys).ConvertAll(
                            t => new HookTask(Get(t, "type"), Get(t, "description"), Get(t, "command")));
                        break;
                    case "session_crons":
                        p.SessionCrons = ReadObjects(ref r, CronKeys).ConvertAll(c => Get(c, "schedule"));
                        break;
                    case "to_model":               p.ToModel = Str(ref r); break;
                    case "agent_id":               p.AgentId = Str(ref r); break;
                    default:                       r.Skip(); break;
                }
            }
            return p;
        }
        catch (Exception ex) { if (!quiet) Diag.Warn("hookPayload", ex); return null; }
    }

    /// <summary>The current value as a string, else "" with the value skipped. A string that cannot become UTF-16 — an
    /// escaped lone surrogate, left where the engine cut a description or a message mid-character — reads as "" rather
    /// than costing the whole event.</summary>
    private static string Str(ref Utf8JsonReader r)
    {
        if (r.TokenType == JsonTokenType.String)
        {
            try { return r.GetString() ?? ""; }
            catch (InvalidOperationException) { return ""; }
        }
        r.Skip();
        return "";
    }

    /// <summary>The <paramref name="keys"/> of the current object into <paramref name="into"/> as strings, every other
    /// value skipped; a value that is not an object is skipped whole.</summary>
    private static void ReadObject(ref Utf8JsonReader r, HashSet<string> keys, Dictionary<string, string> into)
    {
        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return; }
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            var key = r.GetString();
            r.Read();
            if (keys.Contains(key)) into[key] = Str(ref r);
            else r.Skip();
        }
    }

    /// <summary>Each object element of the current array, read as <see cref="ReadObject"/> reads one; any other element
    /// is skipped. Empty for a value that is not an array.</summary>
    private static List<Dictionary<string, string>> ReadObjects(ref Utf8JsonReader r, HashSet<string> keys)
    {
        var list = new List<Dictionary<string, string>>();
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return list; }
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
            var item = new Dictionary<string, string>(StringComparer.Ordinal);
            ReadObject(ref r, keys, item);
            list.Add(item);
        }
        return list;
    }

    private static string Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) ? v : "";
}
