namespace Mastermind;

/// <summary>
/// Warnings raised where the activity log cannot be reached — before it exists (config loads on first use, which can
/// be ahead of the window) or from a class that holds no log (the CDP client, the relay's address book). Held until
/// <see cref="Attach"/> hands over the sink, replayed into it in order, then forwarded as they come. Thread-safe; the
/// sink decides which thread a line lands on.
/// </summary>
internal static class Warnings
{
    private static readonly object _lock = new();
    private static List<string> _held = new();
    private static Action<string> _sink;

    public static void Add(string text)
    {
        Action<string> sink;
        lock (_lock)
        {
            sink = _sink;
            if (sink == null) { _held.Add(text); return; }
        }
        sink(text);
    }

    /// <summary>Route every warning to <paramref name="sink"/> from now on, first replaying the held ones.</summary>
    public static void Attach(Action<string> sink)
    {
        List<string> held;
        lock (_lock) { _sink = sink; held = _held; _held = new(); }
        foreach (var t in held) sink(t);
    }
}
