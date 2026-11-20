namespace Mastermind;

/// <summary>
/// The open-window check: the folders the IDE locks read open, logged whenever that set changes, and a warning once per
/// spell when a window connected over <c>/client</c> with a live session in its folder has read closed for a minute —
/// the lock reading decides READY against PARKED, and nothing else reports it going blind. A window with no live session
/// is never judged: one that never started the extension writes no lock. UI thread.
/// </summary>
internal sealed class WindowCheck
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);
    private string _openSeen;   // the last open set logged
    private readonly Dictionary<string, DateTime> _closedSince = new(StringComparer.Ordinal);   // folder key → first closed read
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>The <c>open windows · …</c> line when the open set differs from the last one logged, else null.</summary>
    public string OpenChanged(IReadOnlySet<string> openKeys)
    {
        var leaves = openKeys.Select(Paths.Leaf).OrderBy(l => l, StringComparer.Ordinal).ToList();
        var seen = string.Join(", ", leaves);
        if (seen == _openSeen) return null;
        _openSeen = seen;
        return "open windows · " + (leaves.Count == 0 ? "none" : seen);
    }

    /// <summary>Each connected folder newly judged blind — closed to the locks for <see cref="Grace"/> with a live
    /// session inside it. A folder reading open again, or no longer connected, starts its next spell from zero.</summary>
    public List<string> Blind(IEnumerable<string> connectedFolders, IReadOnlySet<string> openKeys,
        IEnumerable<string> liveCwds, DateTime now)
    {
        var live = liveCwds.Where(c => !string.IsNullOrEmpty(c)).Select(Paths.Key).ToList();
        var connected = new HashSet<string>(StringComparer.Ordinal);
        var blind = new List<string>();
        foreach (var folder in connectedFolders)
        {
            var key = Paths.Key(folder);
            if (string.IsNullOrEmpty(key) || !connected.Add(key)) continue;
            if (openKeys.Contains(key) || !live.Any(c => Paths.Within(c, key)))
            {
                _closedSince.Remove(key);
                _warned.Remove(key);
                continue;
            }
            if (!_closedSince.TryGetValue(key, out var since)) { _closedSince[key] = now; continue; }
            if (now - since >= Grace && _warned.Add(key)) blind.Add(folder);
        }
        foreach (var gone in _closedSince.Keys.Where(k => !connected.Contains(k)).ToList())
        {
            _closedSince.Remove(gone);
            _warned.Remove(gone);
        }
        return blind;
    }
}
