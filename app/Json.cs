using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mastermind;

/// <summary>
/// Small <see cref="System.Text.Json"/> helpers shared across the app: the safe "give me this property as a
/// string/long, or a default" reads that were hand-rolled in LiveSessions / IdeRegistry / CdpClient /
/// AgentEvent / Sessions, plus best-effort atomic load/save for the little JSON state files.
/// </summary>
internal static class Json
{
    /// <summary>How Mastermind writes a JSON file a person also reads — config.json, a project's settings.local.json: two
    /// spaces, LF, and a path with a non-ASCII letter left readable.</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>String value of property <paramref name="key"/>, or "" if missing / not a string.</summary>
    public static string Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Int64 value of property <paramref name="key"/>, or 0 if missing / not a number.</summary>
    public static long Long(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>A JsonNode's string, or "" when it is missing or not a string.</summary>
    public static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>A JsonNode's int, or 0 when it is missing or not a whole number.</summary>
    public static int Int(JsonNode n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    /// <summary>True only when the JsonNode is the literal true.</summary>
    public static bool True(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Deserialize a JSON file, or null on a missing / corrupt / unreadable file (best effort).</summary>
    public static T Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    /// <summary>
    /// A whole text file another process writes (config, the manifest). Null when the file does not exist; throws on
    /// any other failure, so a caller can tell an absent file from an unreadable one. Strict UTF-8: a byte-order mark
    /// is dropped, and a byte that is not UTF-8 — a file saved as ANSI or UTF-16 — throws a DecoderFallbackException
    /// rather than reading as U+FFFD. The read never blocks another process's open, but no sharing lets a replace
    /// through while the file is held, so the writer retries; the handle lives only for the read.
    /// </summary>
    public static string ReadShared(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, StrictUtf8, detectEncodingFromByteOrderMarks: false);
        var text = sr.ReadToEnd();
        return text.Length > 0 && text[0] == (char)0xFEFF ? text[1..] : text;
    }

    // No preamble of its own, so StreamReader leaves a UTF-8 byte-order mark in the text for ReadShared to drop.
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Write a text file atomically (write-temp-then-replace), creating the dir as needed. False on a failure, which
    /// leaves the previous file intact and lands in the crash log. The replace fails while any process holds the
    /// destination open, whatever sharing that reader asked for, so a reader's moment — the --hook run reading
    /// autoallow.json on a PermissionRequest (HookRun), a session reloading its settings file — is waited out briefly
    /// instead of dropping the write. Never from Config's own load: Diag reads its folder from config.
    /// </summary>
    public static bool WriteAtomic(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); break; }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10) { Thread.Sleep(30); }
            }
            return true;
        }
        catch (Exception ex) { Diag.Warn("writeAtomic " + Path.GetFileName(path), ex); return false; }
    }

    /// <summary>Serialize to a JSON file through <see cref="WriteAtomic"/> — status.json / window.json, rewritten on
    /// live events, so a crash mid-write never leaves a truncated file the reader can't parse. Best effort: a value
    /// that does not serialize is logged, never thrown.</summary>
    public static void SaveAtomic<T>(string path, T value)
    {
        try { WriteAtomic(path, JsonSerializer.Serialize(value)); }
        catch (Exception ex) { Diag.Warn("saveAtomic " + Path.GetFileName(path), ex); }
    }
}
