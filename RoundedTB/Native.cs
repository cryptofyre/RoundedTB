using System;
using System.Runtime.InteropServices;

namespace RoundedTB
{
    /// <summary>Win32 used by the app shell (tray, flyout window). The engine uses LocalPInvoke.</summary>
    internal static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public IntPtr lpszMenuName;
            public IntPtr lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DispatcherQueueOptions
        {
            public int dwSize;
            public int threadType;
            public int apartmentType;
        }

        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        public delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint id, IntPtr refData);

        public const uint WM_CONTEXTMENU = 0x007B, WM_NCCALCSIZE = 0x0083, WM_USER = 0x0400;
        public const uint NIN_SELECT = WM_USER, NIN_KEYSELECT = WM_USER + 1;
        public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
        public const uint NOTIFYICON_VERSION_4 = 4;
        public const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint MOD_WIN = 0x8, VK_F2 = 0x71;
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        public const int DWMWA_NCRENDERING_POLICY = 2, DWMNCRP_DISABLED = 1;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;
        public const int DWMWA_CLOAK = 13;
        public const int DWMWA_BORDER_COLOR = 34, DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

        [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
        public static partial ushort RegisterClassEx(in WNDCLASSEX wc);

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        public static partial IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("comctl32.dll")]
        public static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id, IntPtr refData);

        [DllImport("comctl32.dll")]
        public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial uint RegisterWindowMessage(string name);

        [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr FindWindow(string className, string windowName);

        [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AllowSetForegroundWindow(uint processId);

        [LibraryImport("user32.dll")]
        public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

        [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyIcon(IntPtr icon);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetCursorPos(out POINT point);

        [LibraryImport("user32.dll")]
        public static partial IntPtr MonitorFromPoint(POINT point, uint flags);

        [LibraryImport("user32.dll")]
        public static partial IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [LibraryImport("shcore.dll")]
        public static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static partial long GetWindowLongPtr(IntPtr hWnd, int index);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static partial long SetWindowLongPtr(IntPtr hWnd, int index, long value);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [LibraryImport("dwmapi.dll")]
        public static partial int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        [LibraryImport("dwmapi.dll")]
        public static partial int DwmExtendFrameIntoClientArea(IntPtr hWnd, in MARGINS margins);

        [LibraryImport("CoreMessaging.dll")]
        private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr GetModuleHandle(string name);

        [LibraryImport("kernel32.dll")]
        private static partial int GetCurrentPackageFullName(ref int length, IntPtr name);

        private static IntPtr systemDispatcherQueueController;

        public static bool IsPackaged()
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, IntPtr.Zero) != APPMODEL_ERROR_NO_PACKAGE;
        }

        /// <summary>Windows.UI.Composition needs a Windows.System.DispatcherQueue, which WinUI doesn't create.</summary>
        public static void EnsureSystemDispatcherQueue()
        {
            if (systemDispatcherQueueController != IntPtr.Zero || Windows.System.DispatcherQueue.GetForCurrentThread() != null)
            {
                return;
            }
            DispatcherQueueOptions options = new() { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 };
            CreateDispatcherQueueController(options, out systemDispatcherQueueController);
        }

        public static void GetMonitor(IntPtr monitor, out RECT monitorRect, out RECT workRect, out double scale)
        {
            MONITORINFO mi = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref mi);
            monitorRect = mi.rcMonitor;
            workRect = mi.rcWork;
            scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        }

        /// <summary>The taskbar on a monitor, or the strip between its work area and edge if none is found.</summary>
        public static RECT FindTaskbarRect(IntPtr monitor, RECT monitorRect, RECT workRect)
        {
            foreach (string cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr hwnd = IntPtr.Zero;
                while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, cls, null)) != IntPtr.Zero)
                {
                    if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == monitor && GetWindowRect(hwnd, out RECT r))
                    {
                        return r;
                    }
                }
            }
            return new RECT { Left = monitorRect.Left, Right = monitorRect.Right, Top = workRect.Bottom, Bottom = monitorRect.Bottom };
        }
    }
}
