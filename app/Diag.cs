using System.IO;

namespace Mastermind;

/// <summary>
/// Append-only diagnostic trail. The app is built on best-effort <c>catch</c> blocks (skip a corrupt lock,
/// an unparsable transcript line, a momentarily-locked DB) — that's correct for the inner per-item skips,
/// but the COARSE read-failures (the whole IDE-lock dir, the whole state.vscdb read, the whole project
/// scan) used to vanish silently and leave a confident-but-blind panel. Those land here so a "why was it
/// empty?" can be answered after the fact. The live signal is the in-app degraded banner; this is the
/// post-mortem. Shares the crash-log file App.xaml.cs already writes unhandled exceptions to.
/// </summary>
internal static class Diag
{
    /// <summary><c>&lt;logDir&gt;/mastermind_crash.log</c> — config's logDir: the app folder by default, else any folder
    /// config names.</summary>
    private static string LogPath => Path.Combine(Config.Current.LogDir, "mastermind_crash.log");

    public static void Warn(string context, Exception ex) =>
        Append($"{DateTime.Now:HH:mm:ss} [{context}] {ex.GetType().Name}: {ex.Message}\n");

    /// <summary>Append an unhandled-exception entry (App-level last-resort handlers).</summary>
    public static void Fatal(string tag, object detail) => Append($"{DateTime.Now:HH:mm:ss} [{tag}] {detail}\n\n");

    /// <summary>The log folder comes into being on the first write, never at startup.</summary>
    private static void Append(string text)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, text);
        }
        catch { /* logging must never throw */ }
    }
}
