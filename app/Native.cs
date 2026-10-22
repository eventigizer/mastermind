using System.Runtime.InteropServices;
using System.Text;

namespace Mastermind;

/// <summary>Thin Win32 layer: window enumeration, foreground stealing, process tree + power throttling, DWM styling.</summary>
internal static class Native
{
    // ---- enumeration -------------------------------------------------------
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    // ---- foreground / show -------------------------------------------------
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;
    public const uint ASFW_ANY = 0xFFFFFFFF;   // AllowSetForegroundWindow: enable EVERY process

    /// <summary>
    /// Grant every process the right to call <c>SetForegroundWindow</c> until the next foreground change.
    /// MUST be called by the CURRENT foreground process — Mastermind, right after the user clicked a card, so
    /// it's foreground-eligible. Windows otherwise blocks a NON-foreground process from raising its own window
    /// (it flashes the taskbar instead). The card-click raise runs over CDP as <c>Page.bringToFront</c>,
    /// executed by Code.exe — which is NOT foreground while you're in another app — so without this grant it
    /// only flashes. Calling this first lifts the block so <c>bringToFront</c> can actually raise the window.
    /// </summary>
    public static void AllowAnyForeground()
    {
        try { AllowSetForegroundWindow(ASFW_ANY); } catch { }
    }

    /// <summary>
    /// Bring <paramref name="hWnd"/> to the foreground reliably. Windows blocks a
    /// background process from calling SetForegroundWindow directly (it just flashes
    /// the taskbar), so we briefly attach our input queue to the current foreground
    /// thread, which lifts the restriction for the duration of the call.
    /// Returns false when Windows kept the window back (its foreground lock — the taskbar flashes), which the caller reports and falls back from.
    /// </summary>
    public static bool ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        // Only restore when actually minimized — never resize a maximized or
        // fullscreen window (SW_SHOW/SW_RESTORE on those un-maximizes them).
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

        IntPtr fore = GetForegroundWindow();
        uint foreThread = GetWindowThreadProcessId(fore, out _);
        uint thisThread = GetCurrentThreadId();

        if (foreThread != thisThread)
            AttachThreadInput(thisThread, foreThread, true);

        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);   // its BOOL can read success while Windows only flashed the taskbar — never trusted

        if (foreThread != thisThread)
            AttachThreadInput(thisThread, foreThread, false);
        // The window actually in front decides. An activation that lags reads as refused, which only adds
        // the CDP raise — harmless beside a raise reported that never happened.
        return GetForegroundWindow() == hWnd;
    }

    // ---- process tree + efficiency mode (EcoQoS) ---------------------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetPriorityClass(IntPtr h, uint cls);
    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetProcessMemoryInfo(IntPtr h, ref PROCESS_MEMORY_COUNTERS_EX counters, uint size);
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buffer, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref uint size);

    public const uint TH32CS_SNAPPROCESS = 0x2;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const int ProcessPowerThrottling = 4;                    // PROCESS_INFORMATION_CLASS
    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    public const uint IDLE_PRIORITY_CLASS = 0x40;
    public const uint NORMAL_PRIORITY_CLASS = 0x20;
    public const int ProcessCommandLineInformation = 60;            // PROCESSINFOCLASS, Windows 8.1+: a UNICODE_STRING, then its text
    public const int ERROR_ACCESS_DENIED = 5;
    public const uint STILL_ACTIVE = 259;                           // GetExitCodeProcess: the process has not exited

    // ---- icons -------------------------------------------------------------
    /// <summary>Frees a handle <c>Bitmap.GetHicon</c> made — an <c>Icon.FromHandle</c> never owns it, so its Dispose
    /// alone leaks one GDI handle per icon.</summary>
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

    // ---- DWM (Win11 acrylic / rounded / dark) ------------------------------
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;  // 2 = round
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;       // 3 = transient acrylic

    // ---- recycle bin, silent (the start-time staging clean) ------------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;                 // a double-null-terminated list
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    public const uint FO_DELETE = 3;
    public const ushort FOF_ALLOWUNDO = 0x40;
    public const ushort FOF_NO_UI = 0x614;   // FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR
    public const ushort FOF_NO_CONNECTED_ELEMENTS = 0x2000;   // foo.html alone, never the foo_files/ beside it
}
