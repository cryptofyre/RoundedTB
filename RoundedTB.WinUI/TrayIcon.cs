using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RoundedTB.WinUI
{
    /// <summary>
    /// A notification-area icon on plain Shell_NotifyIcon, owned by a hidden top-level window
    /// (not message-only: those never see the TaskbarCreated broadcast after an Explorer restart).
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private const uint CallbackMessage = Native.WM_USER + 0x100;
        private const uint IconId = 1;
        private const int MenuSettings = 1, MenuQuit = 2;

        private readonly Native.WndProc wndProc; // kept alive for the native side
        private readonly uint taskbarCreatedMessage;
        private readonly IntPtr hwnd;
        private IntPtr icon;

        public event Action? Clicked;
        public event Action? QuitRequested;

        public TrayIcon(string tooltip)
        {
            wndProc = WindowProc;
            IntPtr instance = Native.GetModuleHandle(null);
            IntPtr className = Marshal.StringToHGlobalUni("RoundedTB.WinUI.Tray");
            Native.WNDCLASSEX wc = new()
            {
                cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = instance,
                lpszClassName = className,
            };
            Native.RegisterClassEx(wc);
            hwnd = Native.CreateWindowEx(0, "RoundedTB.WinUI.Tray", "RoundedTB.WinUI.Tray", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
            Tooltip = tooltip;
            Add();
        }

        public string Tooltip { get; }

        private Native.NOTIFYICONDATA CreateData() => new()
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = IconId,
            szTip = Tooltip,
            szInfo = "",
            szInfoTitle = "",
        };

        private void Add()
        {
            if (icon != IntPtr.Zero)
            {
                Native.DestroyIcon(icon);
            }
            icon = LoadTrayIcon();

            Native.NOTIFYICONDATA data = CreateData();
            data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
            data.uCallbackMessage = CallbackMessage;
            data.hIcon = icon;
            Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
            data.uVersion = Native.NOTIFYICON_VERSION_4;
            Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
        }

        /// <summary>The WPF app's tray icons: TrayDark is drawn for a dark taskbar.</summary>
        private static IntPtr LoadTrayIcon()
        {
            bool lightTaskbar = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1;
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", lightTaskbar ? "TrayLight.ico" : "TrayDark.ico");
            return Native.LoadImage(IntPtr.Zero, path, Native.IMAGE_ICON, 0, 0, Native.LR_LOADFROMFILE);
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == CallbackMessage)
            {
                // NOTIFYICON_VERSION_4: the event is in the low word of lParam, the anchor point in wParam.
                uint evt = (uint)(lParam.ToInt64() & 0xFFFF);
                if (evt == Native.NIN_SELECT || evt == Native.NIN_KEYSELECT)
                {
                    Clicked?.Invoke();
                }
                else if (evt == Native.WM_CONTEXTMENU)
                {
                    int x = (short)(wParam.ToInt64() & 0xFFFF);
                    int y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    ShowMenu(x, y);
                }
                return IntPtr.Zero;
            }
            if (msg == taskbarCreatedMessage && taskbarCreatedMessage != 0)
            {
                Add();
                return IntPtr.Zero;
            }
            return Native.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ShowMenu(int x, int y)
        {
            IntPtr menu = Native.CreatePopupMenu();
            Native.AppendMenu(menu, Native.MF_STRING, MenuSettings, "Settings");
            Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenu(menu, Native.MF_STRING, MenuQuit, "Quit");
            // The usual dance so the menu closes when clicking elsewhere.
            Native.SetForegroundWindow(hwnd);
            int cmd = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN, x, y, hwnd, IntPtr.Zero);
            Native.PostMessage(hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Native.DestroyMenu(menu);
            if (cmd == MenuSettings)
            {
                Clicked?.Invoke();
            }
            else if (cmd == MenuQuit)
            {
                QuitRequested?.Invoke();
            }
        }

        public void Dispose()
        {
            Native.NOTIFYICONDATA data = CreateData();
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            if (icon != IntPtr.Zero)
            {
                Native.DestroyIcon(icon);
                icon = IntPtr.Zero;
            }
        }
    }
}
