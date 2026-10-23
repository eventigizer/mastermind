using System.Diagnostics;
using System.IO;

namespace Mastermind;

/// <summary><c>Mastermind.exe --uninstall</c> — started detached by the VS Code extension's uninstall script, from the
/// installed copy: every other run of the installed copy stopped, Mastermind's hook entries out of every project the
/// extension watched, the login shortcut removed (user decision), then the installed copy's folder and its swap
/// leftovers deleted once this process has exited — a running exe's folder cannot go while it runs. A project the
/// manifest lists is never touched — its entries are the manifest writer's. Handoff folders and skills stay. Prints
/// nothing, exits 0.</summary>
internal static class Uninstall
{
    public static int Run()
    {
        try { StopInstalled(); } catch { /* best effort, as every step here */ }
        try { HookEntries.StripWatched(); } catch { }
        try { Platform.LoginStartup.Apply(enabled: false, cleanRead: true); } catch { }
        var app = Path.Combine(Config.AppDir, "app");
        try { Platform.Processes.DeleteAfterExit(app, app + ".new", app + ".old"); } catch { }
        return 0;
    }

    /// <summary>Every process running the installed copy's exe, killed — never a copy started from elsewhere (a
    /// mastermind.exePath build).</summary>
    private static void StopInstalled()
    {
        var installed = Path.Combine(Config.AppDir, "app", "Mastermind.exe");
        foreach (var p in Process.GetProcessesByName("Mastermind"))
            using (p)
                if (p.Id != Environment.ProcessId
                    && string.Equals(Platform.Processes.ImagePath(p.Id), installed, StringComparison.OrdinalIgnoreCase))
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
    }
}
