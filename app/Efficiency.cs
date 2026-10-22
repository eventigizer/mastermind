namespace Mastermind;

/// <summary>
/// Clears Windows 11 efficiency mode off an app's whole process tree, on demand from the tray. Efficiency mode is
/// two levers — EcoQoS (the execution-speed power-throttling bit) and IDLE priority — and both are cleared. Windows
/// / the WebView2 runtime re-apply it on their own, so it cannot be turned off for good; the tray item is the
/// re-run for when the app starts failing.
/// </summary>
internal static class Efficiency
{
    /// <summary>WhatsApp desktop's host image — the WebView2 runtime processes (<c>msedgewebview2.exe</c>) hang
    /// under it, so the tree walk reaches them.</summary>
    public const string WhatsAppImage = "WhatsApp.Root.exe";

    /// <summary><c>Processes</c> = the root + its descendants (0 ⇒ not running); <c>Cleared</c> = those that were in
    /// efficiency mode and now are not; <c>Failed</c> = those that could not be opened or written.</summary>
    public readonly record struct Result(int Processes, int Cleared, int Failed);

    /// <summary>
    /// Every process named <paramref name="rootImage"/> plus all its descendants: EcoQoS is pinned OFF explicitly
    /// (ControlMask carries the bit, StateMask clears it — "never throttle", not "system decides"), other throttling
    /// bits the process set for itself are kept, and IDLE priority goes back to NORMAL. Never throws.
    /// </summary>
    public static Result Release(string rootImage)
    {
        var tree = Tree(rootImage);
        int cleared = 0, failed = 0;
        foreach (int pid in tree)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) { failed++; continue; }
            try
            {
                int size = System.Runtime.InteropServices.Marshal.SizeOf<Native.PROCESS_POWER_THROTTLING_STATE>();
                var s = new Native.PROCESS_POWER_THROTTLING_STATE { Version = Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION };
                Native.GetProcessInformation(h, Native.ProcessPowerThrottling, ref s, size);   // unread ⇒ zeros, still pinned below
                const uint exec = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                bool eco = (s.ControlMask & exec) != 0 && (s.StateMask & exec) != 0;
                bool idle = Native.GetPriorityClass(h) == Native.IDLE_PRIORITY_CLASS;

                s.Version = Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION;
                s.ControlMask |= exec;
                s.StateMask &= ~exec;
                bool ok = Native.SetProcessInformation(h, Native.ProcessPowerThrottling, ref s, size);
                if (idle) ok &= Native.SetPriorityClass(h, Native.NORMAL_PRIORITY_CLASS);

                if (!ok) failed++;
                else if (eco || idle) cleared++;
            }
            finally { Native.CloseHandle(h); }
        }
        return new Result(tree.Count, cleared, failed);
    }

    /// <summary>Pids of every <paramref name="rootImage"/> process and its descendants, from one Toolhelp snapshot
    /// (<see cref="Proc.Table"/>); empty on any failure.</summary>
    private static List<int> Tree(string rootImage)
    {
        List<(int Pid, int Parent, string Exe)> procs;
        try { procs = Proc.Table(); }
        catch (InvalidOperationException ex) { Diag.Warn("efficiency tree", ex); return new List<int>(); }
        var set = new HashSet<int>(procs.Where(p => string.Equals(p.Exe, rootImage, StringComparison.OrdinalIgnoreCase))
                                        .Select(p => p.Pid));
        for (bool added = set.Count > 0; added; )
        {
            added = false;
            foreach (var p in procs)
                if (!set.Contains(p.Pid) && set.Contains(p.Parent)) { set.Add(p.Pid); added = true; }
        }
        return set.ToList();
    }
}
