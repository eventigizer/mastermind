namespace Mastermind;

/// <summary>Last-known window placement, persisted so the panel reopens where you left it.</summary>
public sealed class WindowState
{
    // Normal (non-fullscreen) bounds — what we restore to, and what Exit-fullscreen returns to.
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Topmost { get; set; }

    // Fullscreen: remember it was full and which monitor (origin) so we re-fill the same screen.
    public bool Full { get; set; }
    public double FullLeft { get; set; }
    public double FullTop { get; set; }

    // Activity-log strip visibility. Nullable so a pre-existing window.json (no field) reads as null =
    // "default" = shown, rather than a hard false that would hide it for anyone who saved state before this.
    public bool? LogVisible { get; set; }
}

/// <summary>
/// Reads/writes <see cref="WindowState"/> to a per-machine JSON in the app folder
/// (%LOCALAPPDATA%\Mastermind\window.json). Per-machine on purpose — monitor layout is local;
/// the file is not committed. Every path is best-effort: a missing/corrupt file just means
/// "fall back to the default corner".
/// </summary>
internal static class WindowStateStore
{
    public static WindowState? Load() => Json.Load<WindowState>(ClaudePaths.WindowJson);
    public static void Save(WindowState st) => Json.SaveAtomic(ClaudePaths.WindowJson, st);
}
