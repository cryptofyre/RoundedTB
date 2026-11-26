using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RoundedTB
{
    public class Background
    {
        private struct ReloadChecker
        {
            public bool IsReload { get; set; }
        }

        public AppHost mw;
        bool redrawOverride = false;
        int infrequentCount = 0;

        // Backoff for taskbar regeneration. While Explorer is down the handles stay null, and
        // rebuilding the UI Automation objects on every 100ms tick is what pins the CPU.
        private const int RegenBackoffInitialTicks = 1;  // 100ms
        private const int RegenBackoffMaxTicks = 50;     // 5s
        private int regenBackoffTicks;
        private int regenCooldownTicks;
        private bool regenDegraded;

        // Display changes (resolution, scaling, monitors, work area). Explorer re-lays out the taskbar
        // asynchronously after one of these, so after regenerating we keep forcing redraws for a short
        // settle window instead of trusting the first (often intermediate) rects we read.
        private const int DisplaySettleTicks = 30;       // 3s
        private const int DisplaySettleRedrawEvery = 3;  // 300ms
        private volatile bool displayChangeRequested;
        private volatile bool settingsRedrawRequested;
        private int displaySettleTicks;

        // Hover state is transient and must never be written into the persisted settings.
        private bool hoverShowTray;
        private bool hoverShowWidgets;

        // The watermark patch lives in Explorer's memory, so an Explorer restart drops it. Track the
        // invalid->valid handle transition so AppHost can re-apply it exactly once per restart.
        private bool handlesWereValid = true;

        public Background()
        {
            mw = AppHost.Instance;
        }


        // Main method for the BackgroundWorker - runs indefinitely
        public void DoWork(object sender, DoWorkEventArgs e)
        {
            mw.interaction.AddLog("in bw");
            BackgroundWorker worker = sender as BackgroundWorker;
            while (true)
            {
                try
                {
                    if (worker.CancellationPending == true)
                    {
                        mw.interaction.AddLog("cancelling");
                        e.Cancel = true;
                        break;
                    }

                    // Primary loop for the running process
                    else
                    {
                        // Section for running less important things without requiring an additional thread
                        infrequentCount++;
                        if (infrequentCount == 10)
                        {
                            ReloadChecker checker = new();
                            mw.taskbarDetails?.ForEach(taskbar =>
                            {
                                if (taskbar.AppListXaml.ReloadRequired)
                                {
                                    taskbar.AppListXaml.ReloadTaskbarFrameElement();
                                    checker.IsReload = true;
                                }
                            });
                            // Update tray icon
                            mw.interaction.RefreshUiTray(isForceReset: checker.IsReload);

                            infrequentCount = 0;
                        }

                        // Check if the taskbar is centred, and if it is, directly update the settings; using an interim bool to avoid delaying because I'm lazy
                        bool isCentred = Taskbar.CheckIfCentred();
                        mw.activeSettings.IsCentred = isCentred;

                        // Work with static values to avoid some null reference exceptions
                        List<Types.Taskbar> taskbars = mw.taskbarDetails;
                        Types.Settings settings = mw.activeSettings;

                        // A scale change doesn't always arrive as a window message, so also poll the DPI
                        if (!displayChangeRequested && TaskbarDpiChanged(taskbars))
                        {
                            displayChangeRequested = true;
                        }

                        // If the display configuration changed, rebuild everything so handles, rects and
                        // scale factors are current, then redraw repeatedly while Explorer settles.
                        if (displayChangeRequested)
                        {
                            displayChangeRequested = false;
                            taskbars.ForEach(t => t.Dispose());
                            taskbars = Taskbar.GenerateTaskbarInfo(mw.interaction.IsWindows11());
                            mw.interaction.RefreshUiTray(isForceReset: true);
                            displaySettleTicks = DisplaySettleTicks;
                            mw.interaction.AddLog("Display changed - regenerating taskbar info");
                            Debug.WriteLine("Display changed - regenerating taskbar info");
                        }
                        redrawOverride = false;
                        if (settingsRedrawRequested)
                        {
                            settingsRedrawRequested = false;
                            redrawOverride = true;
                        }
                        if (displaySettleTicks > 0)
                        {
                            redrawOverride = displaySettleTicks % DisplaySettleRedrawEvery == 0;
                            displaySettleTicks--;
                        }

                        // If the number of taskbars has changed, regenerate taskbar information
                        if (Taskbar.TaskbarCountOrHandleChanged(taskbars.Count, taskbars[0].TaskbarHwnd))
                        {
                            // Forcefully reset taskbars if the taskbar count or main taskbar handle has changed
                            taskbars.ForEach(t => t.Dispose());
                            taskbars = Taskbar.GenerateTaskbarInfo(mw.interaction.IsWindows11());
                            mw.interaction.RefreshUiTray(isForceReset: true);
                            Debug.WriteLine("Regenerating taskbar info");

                            // Explorer coming back changes the main handle, so this is the path that
                            // usually fires on recovery - clear any backoff left over from its absence.
                            if (TaskbarHandlesAreValid(taskbars))
                            {
                                ResetRegenBackoff();
                            }
                        }

                        for (int current = 0; current < taskbars.Count; current++)
                        {
                            if (taskbars[current].TaskbarHwnd == IntPtr.Zero || taskbars[current].AppListHwnd == IntPtr.Zero)
                            {
                                // Explorer is (probably) gone. Retry with an exponential backoff instead of
                                // rebuilding CUIAutomation + AppListXaml ten times a second until it returns.
                                if (regenCooldownTicks > 0)
                                {
                                    regenCooldownTicks--;
                                    break;
                                }

                                taskbars.ForEach(t => t.Dispose());
                                taskbars = Taskbar.GenerateTaskbarInfo(mw.interaction.IsWindows11());
                                mw.interaction.RefreshUiTray(isForceReset: true);
                                Debug.WriteLine("Regenerating taskbar info due to a missing handle");

                                if (TaskbarHandlesAreValid(taskbars))
                                {
                                    ResetRegenBackoff();
                                }
                                else
                                {
                                    EscalateRegenBackoff();
                                }
                                break;
                            }
                            // Get the latest quick details of this taskbar
                            Types.Taskbar newTaskbar = Taskbar.GetQuickTaskbarRects(taskbars[current].TaskbarHwnd, taskbars[current].TrayHwnd, taskbars[current].AppListHwnd, taskbars[current].AppListXaml);

                            bool needsMaximisedCheck = settings.FillOnMaximise || settings.AutoHide == (int)Types.AutoHideMode.OnMaximise;
                            bool hasMaximisedWindow = needsMaximisedCheck && Taskbar.HasMaximizedWindow(taskbars[current].TaskbarHwnd);
                            bool shouldFill = settings.FillOnMaximise && (hasMaximisedWindow || Taskbar.IsTaskSwitcherOpen(settings));
                            bool shouldAutoHide = settings.AutoHide == (int)Types.AutoHideMode.Always
                                || (settings.AutoHide == (int)Types.AutoHideMode.OnMaximise && hasMaximisedWindow);

                            if (settings.AutoHide == (int)Types.AutoHideMode.OnMaximise)
                            {
                                SyncOnMaximiseWorkArea(taskbars[current], newTaskbar.TaskbarRect, hasMaximisedWindow);
                            }
                            else
                            {
                                taskbars[current].WorkAreaExpanded = settings.AutoHide == (int)Types.AutoHideMode.Always;
                                taskbars[current].WorkAreaSettleTicks = 0;
                            }

                            UpdateAutoHide(taskbars[current], newTaskbar.TaskbarRect, shouldAutoHide);

                            // If the taskbar's monitor has a maximised window (or task switcher), clear its region so it's "filled"
                            if (shouldFill)
                            {
                                if (!taskbars[current].IsFilled)
                                {
                                    Taskbar.ClearTaskbarRegion(taskbars[current], settings);
                                    taskbars[current].IsFilled = true;
                                    taskbars[current].Ignored = true;
                                }
                                continue;
                            }

                            if (taskbars[current].IsFilled)
                            {
                                taskbars[current].IsFilled = false;
                                taskbars[current].Ignored = true;
                            }

                            // Showhide tray on hover. The result goes into effectiveSettings, never into
                            // the persisted settings object - otherwise a WriteJSON while hovering would
                            // overwrite the user's own ShowTray/ShowWidgets choices.
                            Types.Settings effectiveSettings = settings;
                            if (settings.ShowSegmentsOnHover)
                            {
                                LocalPInvoke.RECT currentTrayRect = taskbars[current].TrayRect;
                                LocalPInvoke.RECT currentWidgetsRect = taskbars[current].TaskbarRect;
                                currentWidgetsRect.Right = Convert.ToInt32(currentWidgetsRect.Right - (currentWidgetsRect.Right - currentWidgetsRect.Left) + (168 * taskbars[current].ScaleFactor));

                                if (currentTrayRect.Left != 0)
                                {
                                    LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT msPt);
                                    bool isHoveringOverTray = LocalPInvoke.PtInRect(ref currentTrayRect, msPt);
                                    bool isHoveringOverWidgets = LocalPInvoke.PtInRect(ref currentWidgetsRect, msPt);
                                    if (isHoveringOverTray && !hoverShowTray)
                                    {
                                        hoverShowTray = true;
                                        taskbars[current].Ignored = true;
                                    }
                                    else if (!isHoveringOverTray)
                                    {
                                        taskbars[current].Ignored = true;
                                        hoverShowTray = false;
                                    }

                                    if (isHoveringOverWidgets && !hoverShowWidgets)
                                    {
                                        hoverShowWidgets = true;
                                        taskbars[current].Ignored = true;
                                    }
                                    else if (!isHoveringOverWidgets)
                                    {
                                        taskbars[current].Ignored = true;
                                        hoverShowWidgets = false;
                                    }
                                }

                                effectiveSettings = settings.ShallowCopy();
                                effectiveSettings.ShowTray = hoverShowTray;
                                effectiveSettings.ShowWidgets = hoverShowWidgets;
                            }


                            // If the taskbar's overall rect has changed, update it. If it's simple, just update. If it's dynamic, check it's a valid change, then update it.
                            if (Taskbar.TaskbarRefreshRequired(taskbars[current], newTaskbar, settings.IsDynamic) || taskbars[current].Ignored || redrawOverride)
                            {
                                Debug.WriteLine($"Refresh required on taskbar {current}");
                                taskbars[current].Ignored = false;
                                int isFullTest = newTaskbar.TrayRect.Left - newTaskbar.AppListRect.Right;
                                mw.interaction.AddLog($"Taskbar: {current} - AppList ends: {newTaskbar.AppListRect.Right} - Tray starts: {newTaskbar.TrayRect.Left} - Total gap: {isFullTest}");
                                if (!settings.IsDynamic || (isFullTest <= taskbars[current].ScaleFactor * 25 && isFullTest > 0 && newTaskbar.TrayRect.Left != 0))
                                {
                                    // Add the rect changes to the temporary list of taskbars
                                    taskbars[current].TaskbarRect = newTaskbar.TaskbarRect;
                                    taskbars[current].AppListRect = newTaskbar.AppListRect;
                                    taskbars[current].TrayRect = newTaskbar.TrayRect;
                                    Taskbar.UpdateSimpleTaskbar(taskbars[current], effectiveSettings);
                                    mw.interaction.AddLog($"Updated taskbar {current} simply");
                                }
                                else
                                {
                                    if (Taskbar.CheckDynamicUpdateIsValid(taskbars[current], newTaskbar))
                                    {
                                        // Add the rect changes to the temporary list of taskbars
                                        taskbars[current].TaskbarRect = newTaskbar.TaskbarRect;
                                        taskbars[current].AppListRect = newTaskbar.AppListRect;
                                        taskbars[current].TrayRect = newTaskbar.TrayRect;
                                        Taskbar.UpdateDynamicTaskbar(taskbars[current], effectiveSettings);
                                        mw.interaction.AddLog($"Updated taskbar {current} dynamically");
                                    }
                                }
                            }
                        }
                        mw.taskbarDetails = taskbars;

                        // Detect Explorer coming back (handles were missing, now present) and have the
                        // watermark patch re-applied, since it didn't survive the restart.
                        bool handlesValid = TaskbarHandlesAreValid(taskbars);
                        if (handlesValid && !handlesWereValid)
                        {
                            mw.OnExplorerRecovered();
                        }
                        handlesWereValid = handlesValid;

                        const int tickMs = 100;
                        const int pollMs = 10;

                        // Explorer slides the taskbar back 85-110ms after the cursor reaches the strip, and SetWindowRgn blocks mid-slide.
                        Stopwatch waited = Stopwatch.StartNew();
                        while (waited.ElapsedMilliseconds < tickMs)
                        {
                            bool anySlid = false;
                            foreach (Types.Taskbar taskbar in taskbars)
                            {
                                if (!LocalPInvoke.IsWindow(taskbar.TaskbarHwnd))
                                {
                                    continue;
                                }
                                LocalPInvoke.RECT strip = Taskbar.GetAutoHideStrip(taskbar.TaskbarHwnd, out bool slid);
                                anySlid |= slid;
                                LocalPInvoke.RECT oldStrip = taskbar.AutoHideStrip;
                                if (Taskbar.RectsEqual(strip, oldStrip))
                                {
                                    continue;
                                }

                                IntPtr region = LocalPInvoke.CreateRectRgn(0, 0, 0, 0);
                                if (LocalPInvoke.GetWindowRgn(taskbar.TaskbarHwnd, region) == 0)
                                {
                                    LocalPInvoke.DeleteObject(region);
                                    taskbar.AutoHideStrip = default;
                                    continue;
                                }
                                if (oldStrip.Right > oldStrip.Left)
                                {
                                    IntPtr oldStripRegion = LocalPInvoke.CreateRectRgn(oldStrip.Left, oldStrip.Top, oldStrip.Right, oldStrip.Bottom);
                                    int combined = LocalPInvoke.CombineRgn(region, region, oldStripRegion, 4);
                                    LocalPInvoke.DeleteObject(oldStripRegion);
                                    if (combined == 0)
                                    {
                                        LocalPInvoke.DeleteObject(region);
                                        throw new InvalidOperationException($"CombineRgn failed removing the auto-hide strip from taskbar {taskbar.TaskbarHwnd}");
                                    }
                                }

                                Taskbar.AddAutoHideStrip(taskbar, region, strip);
                                if (Taskbar.RectsEqual(taskbar.AutoHideStrip, oldStrip))
                                {
                                    LocalPInvoke.DeleteObject(region);
                                    continue;
                                }
                                if (LocalPInvoke.SetWindowRgn(taskbar.TaskbarHwnd, region, true) == 0)
                                {
                                    LocalPInvoke.DeleteObject(region);
                                    throw new InvalidOperationException($"SetWindowRgn failed for taskbar {taskbar.TaskbarHwnd}");
                                }
                            }
                            if (!anySlid)
                            {
                                System.Threading.Thread.Sleep((int)Math.Max(0, tickMs - waited.ElapsedMilliseconds));
                                break;
                            }
                            System.Threading.Thread.Sleep(pollMs);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Anything escaping here is swallowed by BackgroundWorker and handed to
                    // RunWorkerCompleted, which silently ends the loop - so log it and let
                    // AppHost's RunWorkerCompleted handler restart us.
                    mw.interaction.AddLog(ex.Message);
                    if (ex.InnerException != null)
                    {
                        mw.interaction.AddLog(ex.InnerException.Message);
                    }
                    Debug.WriteLine($"Taskbar worker failed: {ex}");
                    throw;
                }
            }
        }

        /// <summary>
        /// Asks the worker to rebuild and redraw every taskbar, e.g. after a resolution or scaling change.
        /// Safe to call from any thread.
        /// </summary>
        public void RequestDisplayRedraw()
        {
            displayChangeRequested = true;
        }

        /// <summary>
        /// Asks the worker to redraw every taskbar once with the current settings, after a live edit
        /// in the settings flyout. Safe to call from any thread.
        /// </summary>
        public void RequestSettingsRedraw()
        {
            settingsRedrawRequested = true;
        }

        /// <summary>
        /// Checks whether any taskbar's DPI no longer matches the scale factor it was measured with.
        /// </summary>
        private static bool TaskbarDpiChanged(List<Types.Taskbar> taskbars)
        {
            foreach (Types.Taskbar taskbar in taskbars)
            {
                if (taskbar.TaskbarHwnd == IntPtr.Zero)
                {
                    continue;
                }
                int dpi = LocalPInvoke.GetDpiForWindow(taskbar.TaskbarHwnd);
                if (dpi != 0 && Math.Abs(dpi / 96.00 - taskbar.ScaleFactor) > 0.001)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks whether every known taskbar has the handles the main loop needs.
        /// </summary>
        private static bool TaskbarHandlesAreValid(List<Types.Taskbar> taskbars)
        {
            if (taskbars == null || taskbars.Count == 0)
            {
                return false;
            }
            foreach (Types.Taskbar taskbar in taskbars)
            {
                if (taskbar.TaskbarHwnd == IntPtr.Zero || taskbar.AppListHwnd == IntPtr.Zero)
                {
                    return false;
                }
            }
            return true;
        }

        private static void SyncOnMaximiseWorkArea(Types.Taskbar taskbar, LocalPInvoke.RECT taskbarRect, bool hasMaximisedWindow)
        {
            bool transitioned = taskbar.WorkAreaExpanded != hasMaximisedWindow;
            if (!transitioned && (!hasMaximisedWindow || taskbar.WorkAreaSettleTicks <= 0))
            {
                return;
            }

            IntPtr monitor = LocalPInvoke.MonitorFromWindow(taskbar.TaskbarHwnd, 2);
            MonitorStuff.MONITORINFO mi = new MonitorStuff.MONITORINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MonitorStuff.MONITORINFO>(),
            };
            if (!MonitorStuff.GetMonitorInfo(monitor, ref mi))
            {
                return;
            }

            if (transitioned)
            {
                taskbar.WorkAreaExpanded = hasMaximisedWindow;
                LocalPInvoke.RECT targetWorkArea = hasMaximisedWindow
                    ? mi.rcMonitor
                    : Taskbar.GetRestoredWorkArea(mi.rcMonitor, taskbarRect);
                Taskbar.SetMonitorWorkArea(monitor, targetWorkArea, broadcast: false);
                taskbar.WorkAreaSettleTicks = hasMaximisedWindow ? 5 : 0;
            }

            if (hasMaximisedWindow && taskbar.WorkAreaSettleTicks > 0)
            {
                taskbar.WorkAreaSettleTicks--;
                Taskbar.ExpandMaximizedWindows(taskbar.TaskbarHwnd, mi.rcMonitor);
            }
        }

        private static void UpdateAutoHide(Types.Taskbar taskbar, LocalPInvoke.RECT taskbarRect, bool shouldAutoHide)
        {
            const int animSpeed = 15;
            if (shouldAutoHide)
            {
                EnsureLayered(taskbar.TaskbarHwnd);

                LocalPInvoke.RECT hoverRect = taskbarRect;
                if (taskbar.TaskbarHidden)
                {
                    hoverRect.Top = hoverRect.Bottom - 2;
                }
                LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT msPt);
                bool shouldReveal = LocalPInvoke.PtInRect(ref hoverRect, msPt) || Taskbar.IsStartMenuOpen(taskbar.TaskbarHwnd);

                bool hasAlpha = LocalPInvoke.GetLayeredWindowAttributes(taskbar.TaskbarHwnd, out _, out byte taskbarOpacity, out uint flags)
                    && (flags & LocalPInvoke.LWA_ALPHA) != 0;
                if (!hasAlpha)
                {
                    taskbarOpacity = 255;
                }

                if (shouldReveal && taskbarOpacity < 255)
                {
                    FadeTaskbar(taskbar, show: true, animSpeed);
                }
                else if (!shouldReveal && taskbarOpacity > 1)
                {
                    FadeTaskbar(taskbar, show: false, animSpeed);
                }
                return;
            }

            bool isLayeredAlpha = LocalPInvoke.GetLayeredWindowAttributes(taskbar.TaskbarHwnd, out _, out byte currentOpacity, out uint currentFlags)
                && (currentFlags & LocalPInvoke.LWA_ALPHA) != 0;
            if (isLayeredAlpha && currentOpacity < 255)
            {
                FadeTaskbar(taskbar, show: true, animSpeed);
            }
            else if (taskbar.TaskbarHidden)
            {
                SetTransparent(taskbar.TaskbarHwnd, transparent: false);
                taskbar.TaskbarHidden = false;
                taskbar.Ignored = true;
            }
        }

        private static void EnsureLayered(IntPtr hwnd)
        {
            int style = LocalPInvoke.GetWindowLong(hwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
            if ((style & LocalPInvoke.WS_EX_LAYERED) != LocalPInvoke.WS_EX_LAYERED)
            {
                LocalPInvoke.SetWindowLong(hwnd, LocalPInvoke.GWL_EXSTYLE, style | LocalPInvoke.WS_EX_LAYERED);
                LocalPInvoke.SetLayeredWindowAttributes(hwnd, 0, 255, LocalPInvoke.LWA_ALPHA);
            }
        }

        private static void SetTransparent(IntPtr hwnd, bool transparent)
        {
            int style = LocalPInvoke.GetWindowLong(hwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
            int target = transparent
                ? style | LocalPInvoke.WS_EX_TRANSPARENT
                : style & ~LocalPInvoke.WS_EX_TRANSPARENT;
            if (target != style)
            {
                LocalPInvoke.SetWindowLong(hwnd, LocalPInvoke.GWL_EXSTYLE, target);
            }
        }

        private static void FadeTaskbar(Types.Taskbar taskbar, bool show, int animSpeed)
        {
            if (show)
            {
                SetTransparent(taskbar.TaskbarHwnd, transparent: false);
                LocalPInvoke.SetWindowPos(taskbar.TaskbarHwnd, new IntPtr(-1), 0, 0, 0, 0,
                    LocalPInvoke.SetWindowPosFlags.IgnoreMove | LocalPInvoke.SetWindowPosFlags.IgnoreResize | LocalPInvoke.SetWindowPosFlags.DoNotActivate);
            }

            byte[] steps = show ? new byte[] { 63, 127, 191, 255 } : new byte[] { 191, 127, 63, 1 };
            for (int i = 0; i < steps.Length; i++)
            {
                LocalPInvoke.SetLayeredWindowAttributes(taskbar.TaskbarHwnd, 0, steps[i], LocalPInvoke.LWA_ALPHA);
                if (i < steps.Length - 1)
                {
                    System.Threading.Thread.Sleep(animSpeed);
                }
            }

            if (!show)
            {
                SetTransparent(taskbar.TaskbarHwnd, transparent: true);
            }
            taskbar.Ignored = true;
            taskbar.TaskbarHidden = !show;
            Debug.WriteLine(show ? "MouseOver TB" : "MouseOff TB");
        }

        private void ResetRegenBackoff()
        {
            regenBackoffTicks = 0;
            regenCooldownTicks = 0;
            if (regenDegraded)
            {
                regenDegraded = false;
                mw.interaction.AddLog("Taskbar handles recovered.");
                Debug.WriteLine("Taskbar handles recovered");
                mw.SetTrayStatus(null);
            }
        }

        private void EscalateRegenBackoff()
        {
            if (regenBackoffTicks == 0)
            {
                regenBackoffTicks = RegenBackoffInitialTicks;
            }
            else if (regenBackoffTicks < RegenBackoffMaxTicks)
            {
                regenBackoffTicks = Math.Min(regenBackoffTicks * 2, RegenBackoffMaxTicks);
            }
            regenCooldownTicks = regenBackoffTicks;

            // Once we're retrying at the slowest rate, say so rather than degrading silently.
            if (regenBackoffTicks >= RegenBackoffMaxTicks && !regenDegraded)
            {
                regenDegraded = true;
                mw.interaction.AddLog("Taskbar handles unavailable - retrying slowly.");
                Debug.WriteLine("Taskbar handles unavailable - retrying slowly");
                mw.SetTrayStatus("RoundedTB - waiting for the taskbar...");
            }
        }
    }
}
