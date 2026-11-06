namespace Mastermind;

/// <summary>
/// The open-window self-check: a window connected over /client with a live session in its folder, whose folder the
/// IDE locks read as closed for a minute, means the lock reading has gone blind — every handoff to it sits PARKED and
/// nothing else says why. The session in the folder proves the Claude extension runs there, so a window that never
/// started it writes no lock and is not judged. Warned once per folder until it clears. UI thread.
/// </summary>
internal sealed class WindowCheck
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, DateTime> _missingSince = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>The name of each folder newly judged blind.</summary>
    public List<string> Blind(IEnumerable<string> connectedFolders, HashSet<string> openKeys, IEnumerable<string> sessionCwds,
        DateTime now)
    {
        var cwds = sessionCwds.Select(Paths.Key).ToList();
        var missing = connectedFolders.Select(Paths.Key).Distinct()
            .Where(f => !openKeys.Contains(f) && cwds.Any(c => Paths.Within(c, f))).ToHashSet(StringComparer.Ordinal);

        foreach (var f in _missingSince.Keys.Where(f => !missing.Contains(f)).ToList()) { _missingSince.Remove(f); _warned.Remove(f); }

        var blind = new List<string>();
        foreach (var f in missing)
        {
            if (!_missingSince.TryGetValue(f, out var since)) { _missingSince[f] = now; continue; }
            if (now - since >= Grace && _warned.Add(f)) blind.Add(Paths.Leaf(f));
        }
        return blind;
    }
}
