using System;
using System.IO;

namespace RoundedTB
{
    public static class StartupShortcut
    {
        private static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "RoundedTB.lnk");

        public static bool Exists => File.Exists(LinkPath);

        public static void Set(bool enabled)
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LinkPath));
                string exe = Environment.ProcessPath;
                ShellLink.CreateShortcut(LinkPath, exe, exe, "", "Start RoundedTB");
            }
            else if (Exists)
            {
                File.Delete(LinkPath);
            }
        }

        /// <summary>
        /// Earlier .NET 10 builds pointed the shortcut at RoundedTB.dll, which can't be launched.
        /// Retarget it at the RoundedTB.exe beside it.
        /// </summary>
        public static void Repair()
        {
            if (!Exists || !ShellLink.TryReadShortcut(LinkPath, out string target, out string icon))
            {
                return;
            }
            string fixedTarget = DllToExe(target);
            string fixedIcon = DllToExe(icon);
            if (fixedTarget == target && fixedIcon == icon)
            {
                return;
            }
            try
            {
                ShellLink.CreateShortcut(LinkPath, fixedTarget, string.IsNullOrEmpty(fixedIcon) ? fixedTarget : fixedIcon, "", "Start RoundedTB");
            }
            catch (Exception)
            {
            }
        }

        private static string DllToExe(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
            string exe = Path.ChangeExtension(path, ".exe");
            return File.Exists(exe) ? exe : path;
        }
    }
}
