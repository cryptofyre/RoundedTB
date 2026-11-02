using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RoundedTB
{
    /// <summary>
    /// Rounds the taskbar's button highlights to match the segments by loading RoundedTB.Tap.dll into
    /// Explorer. The DLL is hooked onto the taskbar thread and told the radius by a window message;
    /// see Tap.cpp for what happens inside Explorer.
    /// </summary>
    internal sealed class HighlightStyler : IDisposable
    {
        private const int WH_CALLWNDPROC = 4;

        private readonly uint applyMessage = RegisterWindowMessageW("RoundedTB.Tap.Apply");
        private IntPtr module;
        private IntPtr hook;
        private IntPtr hookedTaskbar;

        /// <summary>Why the styler can't run, or null when it can.</summary>
        public string Unavailable { get; }

        public HighlightStyler()
        {
            // A hook DLL has to match Explorer's architecture, which is the OS's.
            if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
            {
                Unavailable = "Needs the build of RoundedTB that matches your PC's architecture.";
                return;
            }
            string path = Path.Combine(AppContext.BaseDirectory, "RoundedTB.Tap.dll");
            module = LoadLibraryW(path);
            if (module == IntPtr.Zero)
            {
                Unavailable = "RoundedTB.Tap.dll is missing.";
            }
        }

        /// <summary>Applies <paramref name="radius"/> (DIPs) to every button highlight; below zero restores Explorer's own.</summary>
        public void Apply(double radius)
        {
            if (Unavailable != null)
            {
                return;
            }
            IntPtr taskbar = FindWindowW("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero)
            {
                return;
            }
            // A new taskbar window means Explorer restarted, and the DLL went with it.
            if (taskbar != hookedTaskbar)
            {
                Unhook();
                uint thread = GetWindowThreadProcessId(taskbar, out _);
                hook = SetWindowsHookExW(WH_CALLWNDPROC, GetProcAddress(module, "HookProc"), module, thread);
                hookedTaskbar = hook != IntPtr.Zero ? taskbar : IntPtr.Zero;
            }
            SendMessageW(taskbar, applyMessage, new IntPtr((long)Math.Round(radius * 100)), IntPtr.Zero);
        }

        private void Unhook()
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
            hookedTaskbar = IntPtr.Zero;
        }

        public void Dispose() => Unhook();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int id, IntPtr proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessageW(string name);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
