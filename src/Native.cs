using System;
using System.Runtime.InteropServices;

namespace ClaudeCurtain
{
    /// <summary>Win32 declarations. Nothing here needs administrator rights.</summary>
    internal static class Native
    {
        // ---- low-level hooks ----
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;
        public const int LLKHF_LOWER_IL_INJECTED = 0x02;
        public const int LLKHF_INJECTED = 0x10;
        public const int LLKHF_UP = 0x80;
        public const int LLMHF_INJECTED = 0x01;
        public const int LLMHF_LOWER_IL_INJECTED = 0x02;

        // ---- window messages ----
        public const int WM_CLOSE = 0x0010;
        public const int WM_QUERYENDSESSION = 0x0011;
        public const int WM_QUIT = 0x0012;
        public const int WM_ENDSESSION = 0x0016;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_XBUTTONDOWN = 0x020B;
        public const int WM_XBUTTONUP = 0x020C;
        public const int WM_POWERBROADCAST = 0x0218;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_APP = 0x8000;
        public const int PBT_APMSUSPEND = 0x0004;
        public const int PBT_APMRESUMEAUTOMATIC = 0x0012;

        // ---- virtual keys ----
        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_MBUTTON = 0x04;
        public const int VK_XBUTTON1 = 0x05;
        public const int VK_XBUTTON2 = 0x06;
        public const int VK_BACK = 0x08;
        public const int VK_RETURN = 0x0D;
        public const int VK_ESCAPE = 0x1B;

        // ---- window styles ----
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const uint LWA_ALPHA = 0x2;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        public const uint MOD_NOREPEAT = 0x4000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_NOOWNERZORDER = 0x0200;
        public const int SW_SHOWNOACTIVATE = 4;

        // ---- process / token ----
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint TOKEN_QUERY = 0x0008;
        public const int TokenIntegrityLevel = 25;
        public const int ERROR_ACCESS_DENIED = 5;

        public const uint SEM_FAILCRITICALERRORS = 0x0001;
        public const uint SEM_NOGPFAULTERRORBOX = 0x0002;
        public const uint SEM_NOOPENFILEERRORBOX = 0x8000;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("advapi32.dll")]
        public static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

        [DllImport("advapi32.dll")]
        public static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("kernel32.dll")]
        public static extern uint SetErrorMode(uint uMode);

        // ---- lightweight machine stats (no admin, no counters) ----
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        public sealed class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // Keep-awake request. It belongs to the calling thread and vanishes with the process.
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002;
        public const uint ES_CONTINUOUS = 0x80000000;

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint esFlags);

        // Which desktop receives input. It is not ours while the Ctrl+Alt+Del screen, the lock screen or an
        // administrator prompt is showing, and then this call is refused.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseDesktop(IntPtr desktop);

        // Lock, unlock and Remote Desktop notifications for this session. No rights needed.
        public const int WM_WTSSESSION_CHANGE = 0x02B1;

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
    }
}
