namespace Mastermind;

/// <summary>
/// Remembers each session's last live status event (PermissionRequest / Stop / Notification / …) so a
/// rescan or relaunch can restore the real last situation instead of flattening every row to OPEN.
/// Persisted to status.json in the app folder (<see cref="ClaudePaths.StatusJson"/>, %LOCALAPPDATA%\Mastermind);
/// entries older than a day are pruned on load.
/// </summary>
internal static class StatusCache
{
    private static readonly object _lock = new();
    private static Dictionary<string, Entry> _map;

    private sealed class Entry
    {
        public string Event { get; set; } = "";
        public long When { get; set; }   // unix ms
    }

    private static string StatePath => ClaudePaths.StatusJson;

    private static Dictionary<string, Entry> Map()
    {
        if (_map != null) return _map;
        _map = Json.Load<Dictionary<string, Entry>>(StatePath) ?? new();

        // prune entries older than a day so the file can't grow without bound
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 24L * 3600 * 1000;
        var stale = new List<string>();
        foreach (var kv in _map) if (kv.Value.When < cutoff) stale.Add(kv.Key);
        foreach (var k in stale) _map.Remove(k);
        return _map;
    }

    /// <summary>Record this session's latest status-bearing event.</summary>
    public static void Record(string sessionId, string ev)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(ev)) return;
        lock (_lock)
        {
            // No-op when the status is unchanged — PostToolUse fires per tool, so this collapses a working
            // streak to a single disk write at the PermissionRequest→PostToolUse (amber→working) transition
            // instead of rewriting status.json on every tool call.
            var map = Map();
            if (map.TryGetValue(sessionId, out var prev) && prev.Event == ev) return;
            map[sessionId] = new Entry { Event = ev, When = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            Save();
        }
    }

    public static void Remove(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_lock) { if (Map().Remove(sessionId)) Save(); }
    }

    /// <summary>The session's last status event name, or null if none remembered.</summary>
    public static string Get(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        lock (_lock) { return Map().TryGetValue(sessionId, out var e) ? e.Event : null; }
    }

    // Save() runs on every status transition, so the write must be atomic — a crash mid-write must not
    // leave a truncated status.json that Map() then fails to parse and resets. Json.SaveAtomic owns that.
    private static void Save() => Json.SaveAtomic(StatePath, _map);
}
