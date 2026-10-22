using System.IO;

namespace Mastermind;

/// <summary>
/// <c>Mastermind.exe --hook</c> — the command entry beside the HTTP one on PermissionRequest, so auto-allow
/// holds while Mastermind is closed. The payload arrives on stdin; the asking process is the claude.exe that ran this
/// hook. When <see cref="AutoAllowStore.Allows"/> answers yes it prints the allow answer: the engine reads a command
/// hook's stdout as its output, and two PermissionRequest hooks combine as any-allow-wins, so this and a running
/// Mastermind's HTTP answer never disagree. It writes nothing — Mastermind stays the map's one writer. Anything else,
/// or any error, prints nothing and exits 0 — a hook never blocks a tool. It reads no config: loading it would write
/// config.lastgood.json on every permission prompt.
/// </summary>
internal static class HookRun
{
    public static int Run()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var body = new MemoryStream();
            stdin.CopyTo(body);
            var p = HookPayload.Parse(body.GetBuffer(), (int)body.Length, quiet: true);
            if (p?.Event == "PermissionRequest" && AutoAllowStore.Allows(p.SessionId, AskingClaude(), tidy: false))
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(AutoAllowStore.AllowBody, 0, AutoAllowStore.AllowBody.Length);
                stdout.Flush();
            }
        }
        catch { /* a hook never fails a tool */ }
        return 0;
    }

    /// <summary>The claude.exe that ran this hook — the nearest one above this process, off one process snapshot — or
    /// 0.</summary>
    private static int AskingClaude()
    {
        var table = Proc.Table().ToDictionary(t => t.Pid);
        int pid = Environment.ProcessId;
        for (int hops = 0; hops < 8 && table.TryGetValue(pid, out var self); hops++)
        {
            pid = self.Parent;
            if (table.TryGetValue(pid, out var up) && string.Equals(up.Exe, "claude.exe", StringComparison.OrdinalIgnoreCase))
                return pid;
        }
        return 0;
    }
}
