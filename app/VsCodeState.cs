using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Mastermind;

/// <summary>
/// Reads the Claude Code extension's <c>hiddenSessionIds</c> out of the editor's
/// <c>state.vscdb</c> (global + per-profile, under each editor's settings folder — EditorIdentities). The extension's visible session
/// list is "all sessions for a project MINUS hiddenSessionIds", so this is how
/// Mastermind shows the same working set instead of every historical .jsonl.
///
/// The DB is copied to a temp file (with -wal/-shm) before reading, so we never
/// contend with VS Code's locks and we see WAL-committed data.
/// </summary>
internal static class VsCodeState
{
    private const string ExtKey = "Anthropic.claude-code";

    /// <summary>One editor's hidden ids, read off every state DB under its settings folder.</summary>
    private sealed record EditorArchive(HashSet<string> Hidden, string UserDataDir);

    /// <summary>Archived across every editor in play — Clean hidden sessions and Reap background agents.</summary>
    public static HashSet<string> HiddenSessionIds() => Archived(EditorIdentities.All(), strict: false);

    /// <summary>Hidden in any editor in play — the card archive's did-it-land check, whichever editor archived
    /// it.</summary>
    public static HashSet<string> HiddenSessionIdsAny()
    {
        var any = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in EditorIdentities.All()) any.UnionWith(ReadArchive(e.UserDataDir, strict: false).Hidden);
        return any;
    }

    /// <summary>The archived set of the editor whose window runs under this extension host, read fresh, or null when
    /// it could not be read whole — no state DB found (no editor in play keeps one) or one that failed. Session restore
    /// opens nothing on null: an empty set there would reopen every archived session. An absent key still reads as
    /// nothing archived, as the extension reads it.</summary>
    public static HashSet<string> HiddenSessionIdsStrict(int hostPid) =>
        Archived(new[] { EditorIdentities.ForHost(hostPid) }, strict: true);

    /// <summary>Hidden in one editor and in every editor that opened its project or hides it — a session another
    /// editor still lists stays (user decision). Strict answers null when an editor's DBs fail or none
    /// exists.</summary>
    private static HashSet<string> Archived(IReadOnlyList<EditorIdentity> editors, bool strict)
    {
        var archives = new List<EditorArchive>();
        foreach (var e in editors)
        {
            var a = ReadArchive(e.UserDataDir, strict);
            if (a == null) return null;
            archives.Add(a);
        }
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in archives) hidden.UnionWith(a.Hidden);
        if (archives.Count > 1 && hidden.Count > 0)
        {
            var slugs = Sessions.ProjectSlugs(hidden);
            var opened = archives.Select(a => OpenedProjects(a.UserDataDir)).ToList();
            hidden.RemoveWhere(id => slugs.TryGetValue(id, out var slug)
                && archives.Where((a, i) => !a.Hidden.Contains(id) && opened[i].Contains(slug)).Any());
        }
        return hidden;
    }

    /// <summary>One editor's every state DB, merged. Lenient skips a DB that failed (locked / missing / corrupt);
    /// strict answers null on one, and when none was found.</summary>
    private static EditorArchive ReadArchive(string userDataDir, bool strict)
    {
        var a = new EditorArchive(new HashSet<string>(StringComparer.OrdinalIgnoreCase), userDataDir);
        int read = 0;
        foreach (var db in Platform.EditorState.StateDbs(userDataDir))
        {
            try { ReadInto(db, a.Hidden); read++; }
            catch (Exception ex)
            {
                Diag.Warn("hiddenSessionIds", ex);
                if (strict) return null;
            }
        }
        return strict && read == 0 ? null : a;
    }

    /// <summary>The project slug of every folder an editor opened — each <c>workspaceStorage\*\workspace.json</c> under
    /// its settings folder names a folder, or a .code-workspace file whose folders count — slugged as Claude Code names
    /// a project's transcript folder. An unreadable record opens nothing.</summary>
    private static HashSet<string> OpenedProjects(string userDataDir)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storage = Path.Combine(userDataDir ?? "", "User", "workspaceStorage");
        if (!Directory.Exists(storage)) return slugs;
        foreach (var dir in Directory.EnumerateDirectories(storage))
        {
            try
            {
                var record = Path.Combine(dir, "workspace.json");
                if (!File.Exists(record)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(record));
                if (LocalPath(doc.RootElement, "folder") is { } folder) slugs.Add(Sessions.SlugForCwd(folder));
                else if (LocalPath(doc.RootElement, "workspace") is { } ws && File.Exists(ws))
                {
                    using var w = JsonDocument.Parse(File.ReadAllText(ws), Jsonc);
                    if (w.RootElement.TryGetProperty("folders", out var folders) && folders.ValueKind == JsonValueKind.Array)
                        foreach (var f in folders.EnumerateArray())
                            if (Json.Str(f, "path") is { Length: > 0 } rel)
                                slugs.Add(Sessions.SlugForCwd(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ws)!, rel))));
                }
            }
            catch { /* an unreadable record opens nothing */ }
        }
        return slugs;
    }

    /// <summary>A record's file URI as a local path. VS Code encodes the drive colon (<c>file:///c%3A/…</c>), which
    /// <see cref="Uri.LocalPath"/> leaves as <c>/c:/…</c> — a slug no transcript folder carries — so the path is
    /// unescaped and that slash dropped (read on this machine's .NET 10, 2026-10-22).</summary>
    private static string LocalPath(JsonElement e, string key)
    {
        string path = null;
        if (Json.Str(e, key) is { Length: > 0 } s && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.IsFile)
        {
            path = Uri.UnescapeDataString(u.AbsolutePath);
            if (u.IsUnc) path = u.LocalPath;
            else if (path.Length > 2 && path[0] == '/' && path[2] == ':') path = Path.GetFullPath(path[1..]);
        }
        return path;
    }

    // claudeCode.archiveInactiveSessions — the days after which the extension archives an inactive session: 0 (never),
    // 1, 2, 7 or 14, 14 by default, and any other value read as never.
    private const int DefaultArchiveDays = 14;
    private static readonly double[] ArchiveDayValues = { 0, 1, 2, 7, 14 };
    // settings.json is JSON with comments and trailing commas.
    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>claudeCode.archiveInactiveSessions off the user settings of the editor whose window runs under this
    /// extension host, read as the extension reads it — its default when unset, never (0) for a value outside its five —
    /// the line session restore keeps to; the default too when the file cannot be read. The setting is
    /// application-scoped, so the default profile's file holds it.</summary>
    public static int ArchiveInactiveDays(int hostPid)
    {
        try
        {
            var text = Json.ReadShared(Platform.EditorState.UserSettings(hostPid));
            if (text == null) return DefaultArchiveDays;
            using var doc = JsonDocument.Parse(text, Jsonc);
            JsonElement? value = null;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name == "claudeCode.archiveInactiveSessions") value = p.Value;   // of several, the last wins
            if (value is not { } v) return DefaultArchiveDays;
            return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var days) && ArchiveDayValues.Contains(days)
                ? (int)days : 0;
        }
        catch { return DefaultArchiveDays; }
    }

    // Short-TTL cache over HiddenSessionIds. The live scan (LiveSessions.Active) + every hook event consult
    // the hidden set to keep hidden sessions off the list, so it can't pay a state.vscdb copy each time —
    // the read is re-done at most once per TTL. Refresh invalidates it via InvalidateHidden().
    private static readonly object _hiddenLock = new();
    private static HashSet<string> _hiddenCache;
    private static DateTime _hiddenAt;
    private static bool _hiddenRefreshing;
    private static readonly TimeSpan HiddenTtl = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The hidden set, cached, over the connected windows' editors. On TTL expiry the STALE set is returned immediately and the re-read runs on a
    /// threadpool thread — the underlying read COPIES every state.vscdb (+wal/shm, which grow to tens of MB),
    /// and the callers (the 8s reconcile tick, every hook event) are on the UI thread, so a synchronous
    /// re-read there was a periodic UI stutter, not the "light read" the reconciler assumes. A 15s-stale
    /// hidden set is harmless (hiding already takes a tick to reflect). Only the FIRST call (no cache at
    /// all) reads synchronously.
    /// </summary>
    public static HashSet<string> HiddenSessionIdsCached()
    {
        lock (_hiddenLock)
        {
            if (_hiddenCache != null)
            {
                if (DateTime.Now - _hiddenAt >= HiddenTtl && !_hiddenRefreshing)
                {
                    _hiddenRefreshing = true;
                    Task.Run(() =>
                    {
                        HashSet<string> fresh = null;
                        try { fresh = Archived(EditorIdentities.ConnectedEditors(), strict: false); } catch { /* keep the stale set */ }
                        lock (_hiddenLock)
                        {
                            if (fresh != null) { _hiddenCache = fresh; _hiddenAt = DateTime.Now; }
                            _hiddenRefreshing = false;
                        }
                    });
                }
                return _hiddenCache;   // fresh, or stale-while-refreshing
            }
        }
        var sync = Archived(EditorIdentities.ConnectedEditors(), strict: false);   // first-ever call: nothing to serve yet
        lock (_hiddenLock) { _hiddenCache = sync; _hiddenAt = DateTime.Now; }
        return sync;
    }

    /// <summary>Drop the cached hidden set so the next consult re-reads state.vscdb — called on a manual
    /// Refresh so a just-hidden (or just-un-hidden) session reflects without waiting out the TTL.</summary>
    public static void InvalidateHidden() { lock (_hiddenLock) _hiddenCache = null; }

    private static void ReadInto(string dbPath, HashSet<string> hidden)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mastermind_db_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempDb = Path.Combine(tempDir, "state.vscdb");
        try
        {
            CopyIfExists(dbPath, tempDb);
            CopyIfExists(dbPath + "-wal", tempDb + "-wal");
            CopyIfExists(dbPath + "-shm", tempDb + "-shm");

            // Pooling off: a pooled connection keeps the copy open past Dispose, so the delete below
            // fails silently and leaves one empty mastermind_db_* folder in %TEMP% per read.
            using var con = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly;Pooling=False");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT value FROM ItemTable WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", ExtKey);
            var value = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(value)) return;

            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.TryGetProperty("hiddenSessionIds", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
                foreach (var id in arr.EnumerateArray())
                    if (id.ValueKind == JsonValueKind.String)
                        hidden.Add(id.GetString());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void CopyIfExists(string src, string dst)
    {
        if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
    }

    /// <summary>
    /// Take these ids out of hiddenSessionIds in every editor in play's state.vscdb — a session
    /// Clean kept stays archived where it was. ONLY call when VS Code
    /// is fully closed — otherwise the extension overwrites the DB on quit.
    /// Written directly (not a copy) since there's no running VS Code to contend with.
    /// </summary>
    public static void ClearHiddenSessionIds(HashSet<string> ids)
    {
        foreach (var db in EditorIdentities.All().SelectMany(e => Platform.EditorState.StateDbs(e.UserDataDir)))
        {
            try { ClearOne(db, ids); } catch { /* locked / missing — skip */ }
        }
    }

    private static void ClearOne(string dbPath, HashSet<string> ids)
    {
        using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite");
        con.Open();

        string value;
        using (var get = con.CreateCommand())
        {
            get.CommandText = "SELECT value FROM ItemTable WHERE key = $k";
            get.Parameters.AddWithValue("$k", ExtKey);
            value = get.ExecuteScalar() as string;
        }
        if (string.IsNullOrEmpty(value)) return;

        var node = JsonNode.Parse(value);
        if (node is null) return;
        var kept = (node["hiddenSessionIds"] as JsonArray ?? new JsonArray())
            .Where(id => id is JsonValue v && v.TryGetValue<string>(out var s) && !ids.Contains(s))
            .Select(id => id.DeepClone()).ToArray();
        node["hiddenSessionIds"] = new JsonArray(kept);

        using var upd = con.CreateCommand();
        upd.CommandText = "UPDATE ItemTable SET value = $v WHERE key = $k";
        upd.Parameters.AddWithValue("$v", node.ToJsonString());
        upd.Parameters.AddWithValue("$k", ExtKey);
        upd.ExecuteNonQuery();
    }
}
