using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mastermind;

/// <summary>Process helpers shared across the app.</summary>
internal static class Proc
{
    /// <summary>
    /// Live process ids for an image name (no <c>.exe</c>), e.g. <c>"Code"</c> or <c>"claude"</c>; empty on
    /// any failure. The pid set is how Mastermind tells a real running agent/window from a stale lock or
    /// registry file a hard-killed process left behind, and (paired with the file's own pid) also defends
    /// against pid reuse.
    /// </summary>
    public static HashSet<int> LivePids(string processName)
    {
        var pids = new HashSet<int>();
        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                try { pids.Add(p.Id); } finally { p.Dispose(); }
            }
        }
        catch { /* enumeration failed — treat as none */ }
        return pids;
    }

    /// <summary>Every running process from one Toolhelp snapshot — pid, parent pid and image name (with
    /// <c>.exe</c>). Throws when the snapshot cannot be taken or reads empty — a running Windows always lists itself —
    /// so a failure never passes for "no such process" (VS Code health's busy check would read a working session
    /// idle).</summary>
    public static List<(int Pid, int Parent, string Exe)> Table()
    {
        var procs = new List<(int Pid, int Parent, string Exe)>();
        IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) throw new InvalidOperationException("the process snapshot could not be taken");
        try
        {
            var e = new Native.PROCESSENTRY32W { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.PROCESSENTRY32W>() };
            for (bool more = Native.Process32FirstW(snap, ref e); more; more = Native.Process32NextW(snap, ref e))
                procs.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile));
        }
        finally { Native.CloseHandle(snap); }
        if (procs.Count == 0) throw new InvalidOperationException("the process snapshot read empty");
        return procs;
    }

    /// <summary>A process's command line and private memory, off one PROCESS_QUERY_LIMITED_INFORMATION handle. Gone
    /// (exited since the snapshot) when it cannot be opened for any reason but access, or a read fails and the process
    /// has exited; Error set when access is refused at open — a process of another Windows account; an elevated one
    /// of the same account still grants this right — or a read fails on a process still running, never zeros passed
    /// off as a reading.</summary>
    public static (bool Gone, string CommandLine, long PrivateBytes, string Error) Inspect(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            return err == Native.ERROR_ACCESS_DENIED ? (false, "", 0, $"pid {pid}: access denied (VS Code run as another account?)") : (true, "", 0, null);
        }
        try
        {
            string cmd = null;
            Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation, IntPtr.Zero, 0, out int len);
            if (len > 0)
            {
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation, buf, len, out _) == 0)
                    {
                        var us = Marshal.PtrToStructure<Native.UNICODE_STRING>(buf);
                        cmd = us.Buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            var mc = new Native.PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS_EX>() };
            bool mem = Native.GetProcessMemoryInfo(h, ref mc, mc.cb);
            if (cmd == null || !mem)
                return Exited(h) ? (true, "", 0, null) : (false, cmd ?? "", 0, $"pid {pid}: {(cmd == null ? "command line" : "memory")} unreadable");
            return (false, cmd, (long)mc.PrivateUsage.ToUInt64(), null);
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>True once the process behind an open handle has exited — a read failing on it then means gone.</summary>
    private static bool Exited(IntPtr h) => Native.GetExitCodeProcess(h, out uint code) && code != Native.STILL_ACTIVE;

    /// <summary>A process's full image path, off a PROCESS_QUERY_LIMITED_INFORMATION handle, or null when it cannot be
    /// read.</summary>
    public static string ImagePath(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var name = new System.Text.StringBuilder(1024);
            uint size = (uint)name.Capacity;
            return Native.QueryFullProcessImageName(h, 0, name, ref size) ? name.ToString(0, (int)size) : null;
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>When a running process started, in UTC, or null when it is not running or cannot be read.</summary>
    public static DateTime? StartedAt(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime(); }
        catch { return null; }   // gone, or another account's
    }

    /// <summary>Outcome of a reap attempt — lets the caller log WHY a target wasn't killed (a hard-to-explain
    /// "reaped 0" otherwise). AccessDenied is the elevated-VS-Code case.</summary>
    public enum KillResult { Killed, Gone, AccessDenied, NotClaude }

    /// <summary>
    /// Kill a claude agent by pid — but ONLY after re-verifying the pid is still a <c>claude</c> process, so a
    /// reused pid handed to a non-claude is never killed. Tree-kill so the session's own descendants go with
    /// it. IMPORTANT: <c>Kill(entireProcessTree:true)</c> does NOT let the ROOT's own access-denied escape as a
    /// bare Win32Exception — it collects EVERY failure (the root included) into an <see cref="AggregateException"/>,
    /// so the root is NOT guaranteed dead when that throws. We re-verify with <c>WaitForExit</c>: still alive ⇒
    /// AccessDenied (elevated / higher-integrity target), exited ⇒ Killed (only a descendant lingered). Never
    /// throws.
    /// </summary>
    public static KillResult KillClaude(int pid)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch { return KillResult.Gone; }                       // pid not running
        try
        {
            using (p)
            {
                if (!string.Equals(p.ProcessName, "claude", StringComparison.OrdinalIgnoreCase))
                    return KillResult.NotClaude;
                try { p.Kill(entireProcessTree: true); }
                catch (AggregateException)
                {
                    // The root's own access-denied is bundled in here — the root may still be alive. Re-verify.
                    try { if (!p.WaitForExit(500)) return KillResult.AccessDenied; }
                    catch { return KillResult.AccessDenied; }   // couldn't even confirm exit -> not reclaimed
                }
                return KillResult.Killed;                        // no throw, or root confirmed exited
            }
        }
        catch { return KillResult.Gone; }                       // exited between get and kill
    }
}

/// <summary>The real parent links of one <see cref="Proc.Table"/> snapshot. The snapshot keeps a dead parent's pid, so a
/// process started before the one now holding that pid is no child of it — followed, a session's shell holding the
/// editor's dead parent's pid leads a walk from one window's host into every other window's, and a WhatsApp process
/// holding the desktop shell's takes the whole desktop into its tree. Each start time is read once; one that cannot
/// be read keeps its link.</summary>
internal sealed class ParentLinks
{
    private readonly Dictionary<int, DateTime?> _started = new();

    public bool IsReal(int child, int parent) => !(Started(child) < Started(parent));

    private DateTime? Started(int pid) => _started.TryGetValue(pid, out var at) ? at : (_started[pid] = Proc.StartedAt(pid));
}
