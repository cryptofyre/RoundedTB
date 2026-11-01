using System;
using System.Diagnostics;
using System.IO;

namespace RoundedTB.WinUI
{
    /// <summary>
    /// Appends "click -> milestone" timings to %TEMP%\RoundedTB.WinUI.timing.log, for comparing
    /// open latency with the WPF flyout.
    /// </summary>
    internal static class Timing
    {
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "RoundedTB.WinUI.timing.log");

        public static void Mark(Stopwatch? clock, string milestone)
        {
            if (clock == null)
            {
                return;
            }
            string line = $"{DateTime.Now:HH:mm:ss.fff} {milestone}: {clock.Elapsed.TotalMilliseconds:F1} ms";
            Debug.WriteLine(line);
            try
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }
}
