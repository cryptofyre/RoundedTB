using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RoundedTB
{
    /// <summary>
    /// Notification-area icon owned by a hidden top-level window. Not message-only: those miss the
    /// TaskbarCreated, display-change and setting-change broadcasts this window also forwards.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        public const string WindowClass = "RoundedTB.Tray";
        public const string ShowSettingsMessageName = "RoundedTB_ShowSettings";

        private const uint CallbackMessage = Native.WM_USER + 0x100;
        private const uint IconId = 1;

        private readonly Native.WndProc wndProc;
        private readonly uint taskbarCreatedMessage;
        private readonly uint showSettingsMessage;
        private IntPtr icon;
        private string tooltip;

        public event Action Clicked;
        public event Action ShowSettingsRequested;
        public event Action<int, int> ContextMenuRequested;
        public event Action<uint, IntPtr, IntPtr> MessageReceived;

        public IntPtr Handle { get; }

        public TrayIcon(string tooltip)
        {
            this.tooltip = tooltip;
            wndProc = WindowProc;
            IntPtr instance = Native.GetModuleHandle(null);
            Native.WNDCLASSEX wc = new()
            {
                cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = instance,
                lpszClassName = Marshal.StringToHGlobalUni(WindowClass),
            };
            Native.RegisterClassEx(wc);
            Handle = Native.CreateWindowEx(0, WindowClass, WindowClass, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
            showSettingsMessage = Native.RegisterWindowMessage(ShowSettingsMessageName);
            Add();
        }

        public void SetTooltip(string text)
        {
            tooltip = text;
            Native.NOTIFYICONDATA data = CreateData();
            data.uFlags = Native.NIF_TIP | Native.NIF_SHOWTIP;
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
        }

        /// <summary>Re-adds the icon if Explorer has lost it.</summary>
        public void Ensure()
        {
            Native.NOTIFYICONDATA data = CreateData();
            data.uFlags = Native.NIF_TIP | Native.NIF_SHOWTIP;
            if (!Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data))
            {
                Add();
            }
        }

        private Native.NOTIFYICONDATA CreateData() => new()
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = Handle,
            uID = IconId,
            szTip = tooltip,
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

        private static IntPtr LoadTrayIcon()
        {
            bool lightTaskbar = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1;
            string path = Path.Combine(AppContext.BaseDirectory, "res", lightTaskbar ? "TrayLight.ico" : "TrayDark.ico");
            return Native.LoadImage(IntPtr.Zero, path, Native.IMAGE_ICON, 0, 0, Native.LR_LOADFROMFILE);
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == CallbackMessage)
            {
                uint evt = (uint)(lParam.ToInt64() & 0xFFFF);
                if (evt == Native.NIN_SELECT || evt == Native.NIN_KEYSELECT)
                {
                    Clicked?.Invoke();
                }
                else if (evt == Native.WM_CONTEXTMENU)
                {
                    ContextMenuRequested?.Invoke((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                }
                return IntPtr.Zero;
            }
            if (msg == taskbarCreatedMessage)
            {
                Add();
                return IntPtr.Zero;
            }
            if (msg == showSettingsMessage)
            {
                ShowSettingsRequested?.Invoke();
                return IntPtr.Zero;
            }
            MessageReceived?.Invoke(msg, wParam, lParam);
            return Native.DefWindowProc(hWnd, msg, wParam, lParam);
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
