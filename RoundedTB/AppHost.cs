using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace RoundedTB
{
    public class AppHost
    {
        public static AppHost Instance { get; private set; }

        public static readonly string settingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoundedTB");
        public string configPath = Path.Combine(settingsFolder, "rtb.json");
        public string logPath = Path.Combine(settingsFolder, "rtb.log");
        public bool isWindows11;
        public List<Types.Taskbar> taskbarDetails = new();
        public Types.Settings activeSettings = new();
        public Background background;
        public Interaction interaction;
        public int version = -1;

        public DispatcherQueue Dispatcher { get; }
        public bool IsQuitting { get; private set; }
        public bool IsPackaged { get; }

        private const int HotkeyId = 9000;
        private const int MaxWorkerRestarts = 5;
        private static readonly TimeSpan WorkerRestartWindow = TimeSpan.FromMinutes(1);

        private readonly BackgroundWorker taskbarThread = new() { WorkerSupportsCancellation = true };
        private readonly ManualResetEventSlim workerExited = new(true);
        private readonly TrayIcon tray;
        private FlyoutWindow flyout;
        private TrayMenu trayMenu;
        private readonly HighlightStyler highlights = new();
        private bool highlightsApplied;
        private int workerRestartCount;
        private DateTime workerRestartWindowStart = DateTime.MinValue;
        private int appliedAutoHide = -1;
        private bool taskbarsRestored;

        public AppHost()
        {
            Instance = this;
            Dispatcher = DispatcherQueue.GetForCurrentThread();
            IsPackaged = Native.IsPackaged();
            isWindows11 = Environment.OSVersion.Version.Build >= 21996;

            if (IsPackaged)
            {
                string unpackagedConfig = configPath;
                configPath = Path.Combine(Windows.Storage.ApplicationData.Current.RoamingFolder.Path, "rtb.json");
                logPath = Path.Combine(Windows.Storage.ApplicationData.Current.RoamingFolder.Path, "rtb.log");
                // Carry settings over from the zip build on first packaged run.
                if (!File.Exists(configPath) && File.Exists(unpackagedConfig))
                {
                    File.Copy(unpackagedConfig, configPath);
                }
            }

            background = new Background();
            interaction = new Interaction();

            tray = new TrayIcon("RoundedTB");
            // Queued: an exception inside the native window procedure would kill the process outright.
            tray.Clicked += () => Post(ToggleSettings);
            tray.ShowSettingsRequested += () => Post(ShowSettings);
            tray.ContextMenuRequested += (x, y) => Post(() => (trayMenu ??= new TrayMenu(this)).ShowAt(x, y));
            tray.MessageReceived += (msg, wParam, lParam) =>
            {
                bool handled = false;
                interaction.HwndHook(tray.Handle, (int)msg, wParam, lParam, ref handled);
            };
            Native.RegisterHotKey(tray.Handle, HotkeyId, Native.MOD_WIN, Native.VK_F2);

            if (!IsPackaged)
            {
                StartupShortcut.Repair();
            }

            taskbarThread.DoWork += (sender, e) =>
            {
                try
                {
                    background.DoWork(sender, e);
                }
                finally
                {
                    workerExited.Set();
                }
            };
            taskbarThread.RunWorkerCompleted += TaskbarThread_RunWorkerCompleted;

            interaction.FileSystem();
            activeSettings = interaction.ReadJSON();
            activeSettings.IsWindows11 = isWindows11;
            if (version != activeSettings.Version && version != -1)
            {
                activeSettings.IsNotFirstLaunch = false;
            }
            activeSettings.Version = version;
            if (!isWindows11)
            {
                activeSettings.IsCentred = false;
            }
            if (!activeSettings.FillOnMaximise)
            {
                activeSettings.FillOnTaskSwitch = false;
            }
            // Only 0 (always show) and 1 (auto-hide) were ever selectable.
            if (activeSettings.AutoHide < 0 || activeSettings.AutoHide > 1)
            {
                activeSettings.AutoHide = 0;
            }

            taskbarDetails = Taskbar.GenerateTaskbarInfo(isWindows11);
            ApplySettingsNow();
            if (activeSettings.RemoveInsiderWatermark)
            {
                SyncWatermarkState();
            }

            if (!activeSettings.IsNotFirstLaunch)
            {
                activeSettings.IsNotFirstLaunch = true;
                Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => ShowSettings(helpPage: true));
            }
        }

        private void Post(Action action)
        {
            Dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }
            });
        }

        public static void LogError(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(settingsFolder, "crash.log"), $"[{DateTime.Now}] {ex}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }

        #region Settings flyout

        public void ShowSettings() => ShowSettings(helpPage: false);

        public void ShowSettings(bool helpPage) => WithFlyout(f => f.ShowFlyout(helpPage));

        private void ToggleSettings() => WithFlyout(f => f.Toggle());

        private void WithFlyout(Action<FlyoutWindow> action)
        {
            if (taskbarsRestored)
            {
                return;
            }
            flyout ??= new FlyoutWindow(this);
            action(flyout);
        }

        #endregion

        #region Applying settings

        // The worker redraws, so it stays the only thing touching the taskbar regions.
        public void ApplySettings()
        {
            if (taskbarsRestored)
            {
                return;
            }
            activeSettings.IsCentred = Taskbar.CheckIfCentred();
            if (!activeSettings.FillOnMaximise)
            {
                activeSettings.FillOnTaskSwitch = false;
            }

            ApplyHighlights();
            background.RequestSettingsRedraw();
            if (!taskbarThread.IsBusy)
            {
                StartWorker();
            }

            if (activeSettings.AutoHide != appliedAutoHide)
            {
                AutoHide(activeSettings.AutoHide > 0, taskbarDetails);
                appliedAutoHide = activeSettings.AutoHide;
            }
            interaction.WriteJSON();
        }

        private void ApplySettingsNow()
        {
            activeSettings.IsCentred = Taskbar.CheckIfCentred();
            try
            {
                foreach (Types.Taskbar taskbar in taskbarDetails)
                {
                    int isFullTest = taskbar.TrayRect.Left - taskbar.AppListRect.Right;
                    if (!activeSettings.IsDynamic || (isFullTest <= taskbar.ScaleFactor * 25 && isFullTest > 0 && taskbar.TrayRect.Left != 0))
                    {
                        Taskbar.UpdateSimpleTaskbar(taskbar, activeSettings);
                    }
                    else
                    {
                        Taskbar.UpdateDynamicTaskbar(taskbar, activeSettings);
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                interaction.AddLog(ex.Message);
            }

            if (!taskbarThread.IsBusy)
            {
                StartWorker();
            }
            ApplyHighlights();
            AutoHide(activeSettings.AutoHide > 0, taskbarDetails);
            appliedAutoHide = activeSettings.AutoHide;
            interaction.WriteJSON();
        }

        public void ToggleTray()
        {
            activeSettings.ShowTray = !activeSettings.ShowTray;
            SettingsChangedExternally();
        }

        /// <summary>Settings changed outside the flyout (the Win+F2 hotkey).</summary>
        /// <summary>Rounds the taskbar button highlights to sit inside the segment corners, or restores Explorer's.</summary>
        private void ApplyHighlights()
        {
            if (activeSettings.RoundButtonHighlights)
            {
                int segment = (activeSettings.IsDynamic ? activeSettings.DynamicAppListLayout : activeSettings.SimpleTaskbarLayout).CornerRadius;
                highlights.Apply(Math.Max(2, segment - 2));
                highlightsApplied = true;
            }
            else if (highlightsApplied)
            {
                highlights.Apply(-1);
                highlightsApplied = false;
            }
        }

        public void SettingsChangedExternally()
        {
            ApplySettings();
            flyout?.RefreshFromSettings();
        }

        public void AutoHide(bool enabled, List<Types.Taskbar> taskbars)
        {
            if (taskbars.Count == 0)
            {
                return;
            }
            MonitorStuff.DisplayInfoCollection displays = MonitorStuff.GetDisplays();

            if (activeSettings.AutoHide > 0 && enabled)
            {
                foreach (MonitorStuff.DisplayInfo display in displays)
                {
                    LocalPInvoke.RECT workArea = display.MonitorArea;
                    workArea.Bottom -= 2;
                    Interaction.SetWorkspace(workArea);
                }
                foreach (Types.Taskbar taskbar in taskbars)
                {
                    LocalPInvoke.SetWindowPos(taskbar.TaskbarHwnd, new IntPtr(-1), 0, 0, 0, 0, LocalPInvoke.SetWindowPosFlags.IgnoreMove | LocalPInvoke.SetWindowPosFlags.IgnoreResize);
                    Taskbar.SetTaskbarState(LocalPInvoke.AppBarStates.AlwaysOnTop, taskbar.TaskbarHwnd);
                }
            }
            else if (!enabled)
            {
                MonitorStuff.DisplayInfo primary = displays.Find(d => d.MonitorArea.Left == 0 && d.MonitorArea.Top == 0) ?? displays[0];
                int taskbarHeight = taskbars[0].TaskbarRect.Bottom - taskbars[0].TaskbarRect.Top;
                bool workAreaMisconfigured = primary.MonitorArea.Bottom - primary.MonitorArea.Top - taskbarHeight > primary.WorkArea.Bottom - primary.WorkArea.Top;

                foreach (Types.Taskbar taskbar in taskbars)
                {
                    LocalPInvoke.SetWindowPos(taskbar.TaskbarHwnd, new IntPtr(-1), 0, 0, 0, 0, LocalPInvoke.SetWindowPosFlags.IgnoreMove | LocalPInvoke.SetWindowPosFlags.IgnoreResize);
                    if (workAreaMisconfigured)
                    {
                        Taskbar.SetTaskbarState(LocalPInvoke.AppBarStates.AutoHide, taskbar.TaskbarHwnd);
                        Taskbar.SetTaskbarState(LocalPInvoke.AppBarStates.AlwaysOnTop, taskbar.TaskbarHwnd);
                    }
                    taskbarHeight = taskbar.TaskbarRect.Bottom - taskbar.TaskbarRect.Top;
                    foreach (MonitorStuff.DisplayInfo display in displays)
                    {
                        LocalPInvoke.RECT workArea = display.MonitorArea;
                        workArea.Bottom -= taskbarHeight;
                        Interaction.SetWorkspace(workArea);
                    }
                }
            }
        }

        #endregion

        #region Insider watermark

        // The toggle, startup and Explorer recovery can all ask at once; the patch (symbol download,
        // dbghelp, a cross-process write) runs on its own thread, one at a time.
        private int watermarkBusy;

        /// <summary>Patches or restores Explorer to match RemoveInsiderWatermark, off the UI thread.</summary>
        public void SyncWatermarkState(int initialDelayMs = 0)
        {
            if (Interlocked.CompareExchange(ref watermarkBusy, 1, 0) != 0)
            {
                return;
            }
            bool enable = activeSettings.RemoveInsiderWatermark;
            Task.Run(() =>
            {
                try
                {
                    if (initialDelayMs > 0)
                    {
                        Thread.Sleep(initialDelayMs);
                    }
                    void Log(string message) => interaction.AddLog($"Watermark: {message}");
                    WatermarkRemover.Result result = enable ? WatermarkRemover.Apply(Log) : WatermarkRemover.Restore(Log);
                    Dispatcher.TryEnqueue(() => flyout?.SetWatermarkStatus(result.Message));
                }
                catch (Exception ex)
                {
                    interaction.AddLog($"Watermark: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref watermarkBusy, 0);
                }
            });
        }

        /// <summary>The patch lives in Explorer's memory, so it has to be re-applied after Explorer restarts.</summary>
        public void OnExplorerRecovered()
        {
            if (activeSettings.RoundButtonHighlights)
            {
                // Give the new taskbar time to build its XAML before attaching to it.
                Task.Delay(2000).ContinueWith(_ => Dispatcher.TryEnqueue(ApplyHighlights));
            }
            if (activeSettings.RemoveInsiderWatermark)
            {
                SyncWatermarkState(initialDelayMs: 1500);
            }
        }

        #endregion

        #region Worker

        /// <summary>
        /// BackgroundWorker swallows exceptions from DoWork and ends the loop, so bring it back,
        /// capped so a permanent fault can't spin forever.
        /// </summary>
        private void TaskbarThread_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled || taskbarsRestored)
            {
                return;
            }
            if (e.Error != null)
            {
                interaction.AddLog($"Taskbar worker faulted: {e.Error}");
                Debug.WriteLine($"Taskbar worker faulted: {e.Error}");
            }

            DateTime now = DateTime.UtcNow;
            if (now - workerRestartWindowStart > WorkerRestartWindow)
            {
                workerRestartWindowStart = now;
                workerRestartCount = 0;
            }
            if (workerRestartCount >= MaxWorkerRestarts)
            {
                SetTrayStatus("RoundedTB - stopped after repeated errors");
                return;
            }
            workerRestartCount++;

            Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!taskbarsRestored && !taskbarThread.IsBusy)
                {
                    StartWorker();
                }
            });
        }

        private void StartWorker()
        {
            workerExited.Reset();
            taskbarThread.RunWorkerAsync();
        }

        public void SetTrayStatus(string status)
        {
            Dispatcher.TryEnqueue(() => tray.SetTooltip(string.IsNullOrEmpty(status) ? "RoundedTB" : status));
        }

        public void TrayIconCheck(bool isForceReset)
        {
            if (isForceReset)
            {
                tray.Ensure();
            }
        }

        #endregion

        #region Run at startup

        public async Task<(bool Enabled, bool CanChange)> GetRunAtStartupAsync()
        {
            if (!IsPackaged)
            {
                return (StartupShortcut.Exists, true);
            }
            StartupTask task = await StartupTask.GetAsync("RTB");
            return task.State switch
            {
                StartupTaskState.Enabled => (true, true),
                StartupTaskState.EnabledByPolicy => (true, false),
                StartupTaskState.Disabled => (false, true),
                _ => (false, false),
            };
        }

        public async Task SetRunAtStartupAsync(bool enabled)
        {
            if (!IsPackaged)
            {
                try
                {
                    StartupShortcut.Set(enabled);
                }
                catch (Exception ex)
                {
                    interaction.AddLog($"Failed to change startup setting: {ex.Message}");
                }
                return;
            }
            StartupTask task = await StartupTask.GetAsync("RTB");
            if (enabled && task.State == StartupTaskState.Disabled)
            {
                await task.RequestEnableAsync();
            }
            else if (!enabled && task.State == StartupTaskState.Enabled)
            {
                task.Disable();
            }
        }

        #endregion

        #region Shutdown

        public void RestoreTaskbars()
        {
            if (taskbarsRestored)
            {
                return;
            }
            taskbarsRestored = true;
            taskbarThread.CancelAsync();

            // Bounded: hanging here would leave the taskbar clipped. The worker sleeps 100ms per tick.
            workerExited.Wait(TimeSpan.FromSeconds(2));

            try
            {
                foreach (Types.Taskbar taskbar in taskbarDetails)
                {
                    Taskbar.ResetTaskbar(taskbar, activeSettings);
                }
                if (activeSettings.AutoHide > 0)
                {
                    AutoHide(false, taskbarDetails);
                }
            }
            catch (InvalidOperationException ex)
            {
                interaction.AddLog($"Taskbar structure changed on exit:\n{ex.Message}");
            }
        }

        public void Quit()
        {
            IsQuitting = true;
            flyout?.FlushPendingChanges();
            RestoreTaskbars();
            interaction.WriteJSON();
            if (highlightsApplied)
            {
                highlights.Apply(-1);
            }
            highlights.Dispose();
            Native.UnregisterHotKey(tray.Handle, HotkeyId);
            tray.Dispose();
            // Everything is restored and saved; XAML teardown can take several seconds, during which a
            // relaunch would find this instance still holding the single-instance mutex.
            Environment.Exit(0);
        }

        #endregion
    }
}
