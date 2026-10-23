using System.Runtime.CompilerServices;

namespace Mastermind;

/// <summary>The entry point ahead of WPF: <c>--hook</c> answers one hook entry and exits (<see cref="HookRun"/>) —
/// before any window code loads or the single-instance lock is asked, so it costs only the process start;
/// <c>--uninstall</c> takes out what an install wrote (<see cref="Uninstall"/>); any other start is the app.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--hook") return HookRun.Run();
        if (args.Length == 1 && args[0] == "--uninstall") return Uninstall.Run();
        RunApp();
        return 0;
    }

    /// <summary>The app, in a method of its own that is never inlined: compiling Main then resolves no WPF type, so a
    /// --hook run loads none of WPF's assemblies.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApp() => App.Main();
}
