namespace Mastermind;

/// <summary>
/// The one symptom check for hook entries that are wired but deliver nothing — a managed policy blocking every hook
/// (<c>allowManagedHooksOnly</c>), a wrong port or an engine too old for them: a live
/// session of a watched project — one the last hook check found fully wired — that the registry shows busy, on a turn
/// begun after the listener bound and running three minutes, from which no hook event has ever arrived. Three
/// minutes, because a background-task wake starts a turn with no UserPromptSubmit and may think a while before its
/// first tool. Judged per session and once — a built-in command may mark a session busy with no hook at all, and an
/// entry wiped mid-session is the hook check's to report. UI thread.
/// </summary>
internal sealed class HookSymptom
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);
    private readonly HashSet<string> _heard = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private DateTime _since = DateTime.MaxValue;   // the listener's bind; no turn is judged before one

    /// <summary>The listener bound — turns begun from here on are judged.</summary>
    public void Listening(DateTime at) { if (at < _since) _since = at; }

    /// <summary>A hook event arrived from this session.</summary>
    public void Heard(string sessionId) { if (!string.IsNullOrEmpty(sessionId)) _heard.Add(sessionId); }

    /// <summary>The project of each session newly judged silent — each session at most once.</summary>
    public List<string> Silent(IReadOnlyDictionary<string, (string Cwd, RunState Run)> live, Func<string, bool> watched,
        DateTime now)
    {
        var silent = new List<string>();
        foreach (var (sid, s) in live)
        {
            if (s.Run.Status != "busy" || s.Run.At <= 0 || _heard.Contains(sid) || _warned.Contains(sid)) continue;
            var began = DateTimeOffset.FromUnixTimeMilliseconds(s.Run.At).LocalDateTime;
            if (began < _since || now - began < Grace || !watched(s.Cwd)) continue;
            _warned.Add(sid);
            silent.Add(Paths.Leaf(s.Cwd));
        }
        return silent;
    }
}
