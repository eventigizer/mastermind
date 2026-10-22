using System.Linq;

namespace Mastermind;

/// <summary>One owner record for an in-flight prompt file — the session that a dispatch handed the file to. An
/// object (not a bare sid string) so more can be bolted on later (dispatchedVia / destProject / dispatchedAtIso)
/// with no format break — that was the whole reason for JSON-with-object-values.</summary>
internal sealed class PromptOwner
{
    public string sid { get; set; } = "";   // the dispatched-to session id, or the PENDING marker while it resolves
    public bool typed { get; set; }         // the dispatch only typed its text (debug port off) and nobody has sent it yet
    public bool spawned { get; set; }       // a `new` dispatch: the session that sends it runs under config's newDispatchAutoAllow
}

/// <summary>
/// The prompt→owner link: a <c>{fileKey: {sid}}</c> map at <c>%LOCALAPPDATA%\Mastermind\prompt_owners.json</c>,
/// keyed on the <c>processing/</c> file (<see cref="Paths.Key"/>). The dispatcher writes an entry the moment it
/// moves a file into <c>processing/</c>; the scan reads it each tick and, cross-checking the sid against the
/// live-agent set, decides whether a PROCESSING row is genuinely owned or ORPHANED (owner dead / unknown).
///
/// Persisted across restarts on purpose — resetting on launch would re-orphan every in-flight prompt whose agent
/// is still alive. Instead the scan GCs it each tick via <see cref="RetainOnly"/> (drop entries whose processing
/// file is gone), so it never grows and never re-orphans live work. Mastermind is the SOLE writer; mirrors
/// <see cref="AutoAllowStore"/> — cached map, atomic save, lock-guarded (a write can land from the new-session
/// id-resolve continuation while the tick reads).
/// </summary>
internal static class PromptOwnerStore
{
    public const string Pending = "PENDING";   // new-session dispatch: owner protected while its id is still resolving

    private static readonly object _lock = new();
    private static Dictionary<string, PromptOwner> _map;

    private static Dictionary<string, PromptOwner> Map() =>
        _map ??= Json.Load<Dictionary<string, PromptOwner>>(ClaudePaths.PromptOwnersJson) ?? new();

    /// <summary>Record (or update) the owner sid for a processing file and persist — <paramref name="typed"/> when the
    /// dispatch only TYPED its text (the debug port off, the person presses Enter), where <paramref name="sid"/> may be
    /// <see cref="Pending"/> while a new session's id resolves; <paramref name="spawned"/> when a `new` dispatch started it.</summary>
    public static void Set(string fileKey, string sid, bool typed = false, bool spawned = false)
    {
        if (string.IsNullOrEmpty(fileKey) || string.IsNullOrEmpty(sid)) return;
        lock (_lock)
        {
            var map = Map();
            if (map.TryGetValue(fileKey, out var cur) && cur.sid == sid && cur.typed == typed && cur.spawned == spawned) return;   // no-op: don't rewrite for an unchanged owner
            map[fileKey] = new PromptOwner { sid = sid, typed = typed, spawned = spawned };
            Json.SaveAtomic(ClaudePaths.PromptOwnersJson, map);
        }
    }

    /// <summary>Mark a processing file as owned-but-resolving (new-session dispatch, id not assigned yet).</summary>
    public static void SetPending(string fileKey) => Set(fileKey, Pending);

    /// <summary>The file's owner — its sid (or the <see cref="Pending"/> marker), whether the dispatch only typed its
    /// text, nobody having sent it yet, and whether a `new` dispatch started it — or null if this file has no entry.</summary>
    public static (string Sid, bool Typed, bool Spawned)? Find(string fileKey)
    {
        if (string.IsNullOrEmpty(fileKey)) return null;
        lock (_lock) return Map().TryGetValue(fileKey, out var o) ? (o.sid, o.typed, o.spawned) : null;
    }

    /// <summary>Drop the entry (dispatch rolled back to Waiting → no owner).</summary>
    public static void Remove(string fileKey)
    {
        if (string.IsNullOrEmpty(fileKey)) return;
        lock (_lock)
        {
            var map = Map();
            if (map.Remove(fileKey)) Json.SaveAtomic(ClaudePaths.PromptOwnersJson, map);
        }
    }

    /// <summary>GC: keep only entries whose processing file still exists (the set the scan just saw). Called each
    /// tick so a consumed/deleted prompt's owner entry goes with it — the map tracks only live processing files.</summary>
    public static void RetainOnly(HashSet<string> liveFileKeys)
    {
        lock (_lock)
        {
            var map = Map();
            var drop = map.Keys.Where(k => !liveFileKeys.Contains(k)).ToList();
            if (drop.Count == 0) return;
            foreach (var k in drop) map.Remove(k);
            Json.SaveAtomic(ClaudePaths.PromptOwnersJson, map);
        }
    }
}
