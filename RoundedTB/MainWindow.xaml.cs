using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Reflection;
using System.Windows.Threading;
using System.Windows.Interop;
using DesktopBridge;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using System.Diagnostics;
using Microsoft.Win32;
using System.Text;
using WPFUI;
using System.Windows.Media;
using System.Linq;
using System.Windows.Controls;

namespace RoundedTB
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// 
    /// Many thanks to
    ///  - FloatingMilkshake
    ///  - cardin
    ///  - cleverActon0126
    ///  for your gracious donations! 💖
    ///  
    /// </summary>
    public partial class MainWindow : Window
    {
        public bool isWindows11;
        public List<Types.Taskbar> taskbarDetails = new List<Types.Taskbar>();
        public bool shouldReallyDieNoReally = false;
        /// <summary>
        /// %LOCALAPPDATA%\RoundedTB. Versions up to now dropped rtb.json and rtb.log loose in the
        /// root of LocalApplicationData; Interaction.FileSystem moves them in here on first run.
        /// </summary>
        public static readonly string settingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoundedTB");
        public string configPath = Path.Combine(settingsFolder, "rtb.json");
        public string logPath = Path.Combine(settingsFolder, "rtb.log");
        public Types.Settings activeSettings = new Types.Settings();
        public BackgroundWorker taskbarThread = new BackgroundWorker();
        public IntPtr hwndDesktopButton = IntPtr.Zero;
        public int lastDynDistance = 0;
        public int numberToForceRefresh = 0;
        public bool isCentred = false;
        public bool isAlreadyRunning = false;
        private bool taskbarsRestored = false;

        // Restart bookkeeping for the taskbar worker. An unhandled exception used to end the
        // loop silently, leaving the app running but doing nothing at all.
        private const int MaxWorkerRestarts = 5;
        private static readonly TimeSpan WorkerRestartWindow = TimeSpan.FromMinutes(1);
        private int workerRestartCount = 0;
        private DateTime workerRestartWindowStart = DateTime.MinValue;
        private object workerArguments = null;

        /// <summary>
        /// Set in the constructor so App.OnExit can reach the window during shutdown.
        /// Application.Current.MainWindow is not a dependable route at that point - the
        /// windows collection has already been torn down. Phase 4 should replace this
        /// with proper lifetime ownership rather than ambient statics.
        /// </summary>
        internal static MainWindow? Instance { get; private set; }
        public Background background;
        public Interaction interaction;
        private HwndSource source;
        private SettingsFlyout flyout;
        private int appliedAutoHide = -1;
        /// <summary>
        /// Versions:
        /// -1: Canary
        ///  0: R3.0
        ///  1: P3.1B
        ///  2: R3.1
        ///  3: R4
        /// </summary>
        public int version = -1;

        public MainWindow()
        {
            Instance = this;

            InitializeComponent();


            // Check OS build, as behaviours rather-annoyingly differ between Windows 11 and Windows 10
            RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var buildNumber = registryKey.GetValue("CurrentBuild").ToString();
            if (Convert.ToInt32(buildNumber) >= 21996)
            {
                isWindows11 = true;
            }
            else
            {
                isWindows11 = false;
                activeSettings.IsWindows11 = false;
            }

            // Initialise functions
            background = new Background();
            interaction = new Interaction();

            // Check if RoundedTB is already running, and if it is, do nothing.
            Process[] matchingProcesses = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName);

            if (matchingProcesses.Length > 1)
            {
                List<IntPtr> windowList = Interaction.GetTopLevelWindows();
                foreach (IntPtr hwnd in windowList)
                {
                    StringBuilder windowClass = new StringBuilder(1024);
                    StringBuilder windowTitle = new StringBuilder(1024);
                    try
                    {
                        LocalPInvoke.GetClassName(hwnd, windowClass, 1024);
                        LocalPInvoke.GetWindowText(hwnd, windowTitle, 1024);

                        if (windowClass.ToString().StartsWith("HwndWrapper[RoundedTB") && windowTitle.ToString() == "RoundedTB")
                        {
                            LocalPInvoke.SetWindowText(hwnd, "RoundedTB_SettingsRequest");
                        }
                    }
                    catch (Exception) { }
                }
                shouldReallyDieNoReally = true;
                isAlreadyRunning = true;
                Close();
                return;
            }
            TrayIconCheck(isForceReset:true);

            if (IsRunningAsUWP())
            {
#pragma warning disable CS4014
                StartupInit(true);
                configPath = Path.Combine(Windows.Storage.ApplicationData.Current.RoamingFolder.Path, "rtb.json");
                logPath = Path.Combine(Windows.Storage.ApplicationData.Current.RoamingFolder.Path, "rtb.log");
            }

            if (System.IO.File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "RoundedTB.lnk")) && !IsRunningAsUWP())
            {
                StartupCheckBox.IsChecked = true;
                RepairStartupShortcut();
            }
            taskbarThread.WorkerSupportsCancellation = true;
            taskbarThread.WorkerReportsProgress = true;
            taskbarThread.DoWork += background.DoWork;
            taskbarThread.RunWorkerCompleted += TaskbarThread_RunWorkerCompleted;

            // Load settings into memory/UI
            interaction.FileSystem();
            if (!IsRunningAsUWP())
            {
                interaction.AddLog($"RoundedTB started!");
            }
            else
            {
                interaction.AddLog($"RoundedTB started in UWP mode!");
            }
            activeSettings = interaction.ReadJSON();

            if (isWindows11)
            {
                activeSettings.IsWindows11 = true;
            }
            else
            {
                activeSettings.IsWindows11 = false;
            }
            // Default settings
            if (activeSettings == null)
            {

                if (isWindows11) // Default settings for Windows 11
                {
                    activeSettings = new Types.Settings()
                    {
                        SimpleTaskbarLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                        DynamicAppListLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                        DynamicTrayLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                        DynamicWidgetsLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                        WidgetsWidth = 168,
                        ClockWidth = 110,
                        IsDynamic = false,
                        IsCentred = false,
                        IsWindows11 = true,
                        ShowTray = false,
                        ShowWidgets = false,
                        ShowSecondaryClock = false,
                        CompositionCompat = false,
                        IsNotFirstLaunch = false,
                        FillOnMaximise = true,
                        FillOnTaskSwitch = true,
                        ShowSegmentsOnHover = false,
                        AutoHide = 0
                    };
                }
                else // Default settings for Windows 10
                {
                    activeSettings = new Types.Settings()
                    {
                        SimpleTaskbarLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                        DynamicAppListLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                        DynamicTrayLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                        DynamicWidgetsLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                        WidgetsWidth = 168,
                        ClockWidth = 110,
                        IsDynamic = false,
                        IsCentred = false,
                        IsWindows11 = false,
                        ShowTray = false,
                        ShowWidgets = false,
                        ShowSecondaryClock = false,
                        CompositionCompat = false,
                        IsNotFirstLaunch = false,
                        FillOnMaximise = true,
                        FillOnTaskSwitch = false,
                        ShowSegmentsOnHover = false,
                        AutoHide = 0
                    };
                }
            }

            if (version != activeSettings.Version && version != -1)
            {
                activeSettings.IsNotFirstLaunch = false;
            }
            activeSettings.Version = version;


            interaction.AddLog($"Settings loaded:");
            interaction.AddLog(
                $"SimpleTaskbarLayout: {activeSettings.SimpleTaskbarLayout}\n" +
                $"DynamicAppListLayout: {activeSettings.DynamicAppListLayout}\n" +
                $"DynamicTrayLayout: {activeSettings.DynamicTrayLayout}\n" +
                $"DynamicWidgetsLayout: {activeSettings.DynamicWidgetsLayout}\n" +
                $"DynamicSecondaryClockLayout: {activeSettings.DynamicSecondaryClockLayout}\n" +
                $"IsDynamic: {activeSettings.IsDynamic}\n" +
                $"IsCentred: {activeSettings.IsCentred}\n" +
                $"ShowTray: {activeSettings.ShowTray}\n" +
                $"ShowWidgets: {activeSettings.ShowWidgets}\n" +
                $"ShowSecondaryClock: {activeSettings.ShowSecondaryClock}\n" +
                $"CompositionCompat: {activeSettings.CompositionCompat}\n" +
                $"IsNotFirstLaunch: {activeSettings.IsNotFirstLaunch}\n" +
                $"FillOnMaximise: {activeSettings.FillOnMaximise}\n" +
                $"FillOnTaskSwitch: {activeSettings.FillOnTaskSwitch}\n" +
                $"ShowTrayOnHover: {activeSettings.ShowSegmentsOnHover}\n"
                );

            // Get whether or not taskbar is centred
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced"))
                {
                    if (key != null)
                    {
                        int val = (int)key.GetValue("TaskbarAl");
                        if (val == 1)
                        {
                            isCentred = true;
                        }
                        else
                        {
                            isCentred = false;
                        }
                        interaction.AddLog($"Taskbar centred? {isCentred}");
                    }
                }
            }
            catch (Exception aaaa)
            {
                interaction.AddLog(aaaa.Message);
            }
            if (!isWindows11)
            {
                activeSettings.IsCentred = false;
            }

            if (!activeSettings.FillOnMaximise)
            {
                activeSettings.FillOnTaskSwitch = false;
            }
            // Only 0 (always show) and 1 (auto-hide) were ever selectable; 2 was a disabled placeholder.
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

            if (activeSettings.IsNotFirstLaunch != true)
            {
                activeSettings.IsNotFirstLaunch = true;
                // First run: open the flyout on its Help tab so people find out where settings live.
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => ShowSettings(helpPage: true)));
            }

            AutoHide(true, taskbarDetails);
        }

        /// <summary>
        /// Opens the settings flyout next to the taskbar.
        /// </summary>
        public void ShowSettings()
        {
            ShowSettings(helpPage: false);
        }

        public void ShowSettings(bool helpPage)
        {
            if (shouldReallyDieNoReally || taskbarsRestored)
            {
                return;
            }
            flyout ??= new SettingsFlyout(this);
            flyout.ShowFlyout(helpPage ? SettingsFlyout.Page.Help : SettingsFlyout.Page.Settings);
        }

        private void MainTitleBar_NotifyIconClick(object sender, RoutedEventArgs e)
        {
            if (shouldReallyDieNoReally || taskbarsRestored)
            {
                return;
            }
            flyout ??= new SettingsFlyout(this);
            flyout.Toggle();
        }

        public void AutoHide(bool enabled, List<Types.Taskbar> taskbarDetails)
        {
            int workingHeight = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea.Height;
            int boundsHeight = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
            int taskbarHeight = taskbarDetails[0].TaskbarRect.Bottom - taskbarDetails[0].TaskbarRect.Top;
            bool workAreaMisconfigured = false;

            if (boundsHeight - taskbarHeight > workingHeight)
            {
                workAreaMisconfigured = true;
            }

            if (activeSettings.AutoHide > 0 && enabled)
            {
                MonitorStuff.DisplayInfoCollection Displays = MonitorStuff.GetDisplays();

                foreach (MonitorStuff.DisplayInfo display in Displays)
                {
                    LocalPInvoke.RECT workArea = display.MonitorArea;
                    workArea.Bottom = workArea.Bottom - 2;
                    Interaction.SetWorkspace(workArea);
                }
                foreach (Types.Taskbar taskbar in taskbarDetails)
                {
                    LocalPInvoke.SetWindowPos(taskbar.TaskbarHwnd, new IntPtr(-1), 0, 0, 0, 0, LocalPInvoke.SetWindowPosFlags.IgnoreMove | LocalPInvoke.SetWindowPosFlags.IgnoreResize);
                    Taskbar.SetTaskbarState(LocalPInvoke.AppBarStates.AlwaysOnTop, taskbar.TaskbarHwnd);
                }
            }
            else if (!enabled)
            {
                foreach (Types.Taskbar taskbar in taskbarDetails)
                {
                    LocalPInvoke.SetWindowPos(taskbar.TaskbarHwnd, new IntPtr(-1), 0, 0, 0, 0, LocalPInvoke.SetWindowPosFlags.IgnoreMove | LocalPInvoke.SetWindowPosFlags.IgnoreResize);
                    if (workAreaMisconfigured)
                    {
                        Taskbar.SetTaskbarState(LocalPInvoke.AppBarStates.AutoHide, taskbar.TaskbarHwnd);
                        Taskbar.SetTaskbarState(LocalPInvoke.AppBarStates.AlwaysOnTop, taskbar.TaskbarHwnd);
                    }

                    MonitorStuff.DisplayInfoCollection Displays = MonitorStuff.GetDisplays();

                    foreach (MonitorStuff.DisplayInfo display in Displays)
                    {
                        taskbarHeight = taskbar.TaskbarRect.Bottom - taskbar.TaskbarRect.Top;
                        LocalPInvoke.RECT workArea = display.MonitorArea;
                        workArea.Bottom = workArea.Bottom - taskbarHeight;
                        Interaction.SetWorkspace(workArea);
                    }
                }
            }
        }

        /// <summary>
        /// Starts the taskbar worker, remembering its arguments so it can be restarted after a fault.
        /// </summary>
        private void StartTaskbarWorker(object arguments)
        {
            workerArguments = arguments;
            taskbarThread.RunWorkerAsync(arguments);
        }

        /// <summary>
        /// BackgroundWorker swallows exceptions thrown from DoWork and surfaces them here. Without
        /// this handler the loop just stopped and RoundedTB sat there doing nothing, so record the
        /// fault and bring the loop back - with a cap so a permanent fault can't spin forever.
        /// </summary>
        private void TaskbarThread_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled || shouldReallyDieNoReally || taskbarsRestored)
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
                interaction.AddLog("Taskbar worker restarted too often - giving up.");
                Debug.WriteLine("Taskbar worker restarted too often - giving up");
                SetTrayStatus("RoundedTB - stopped after repeated errors");
                return;
            }
            workerRestartCount++;

            // Restart off the current callback so the worker is no longer marked busy.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (shouldReallyDieNoReally || taskbarsRestored || taskbarThread.IsBusy)
                {
                    return;
                }
                try
                {
                    taskbarThread.RunWorkerAsync(workerArguments);
                }
                catch (Exception ex)
                {
                    interaction.AddLog($"Failed to restart taskbar worker: {ex.Message}");
                    Debug.WriteLine($"Failed to restart taskbar worker: {ex.Message}");
                }
            }));
        }

        /// <summary>
        /// Surfaces a degraded state on the tray icon, or clears it when passed null.
        /// Safe to call from the worker thread.
        /// </summary>
        public void SetTrayStatus(string status)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    mainTitleBar.NotifyIconTooltip = string.IsNullOrEmpty(status) ? "RoundedTB" : status;
                    mainTitleBar.ResetIcon();
                }
                catch (Exception)
                {
                    // Cosmetic only - never let this take the app down.
                }
            }));
        }

        public void TrayIconCheck(bool isForceReset)
        {
            Uri resLight = new("pack://application:,,,/res/traylight.ico");
            Uri resDark = new("pack://application:,,,/res/traydark.ico");

            if (false)
            {
                // TODO: Show system theme mode icon.
                bool cuurentIsLightMode = IsThemeLightMode();
                if (cuurentIsLightMode)
                {
                    mainTitleBar.NotifyIconImage = System.Windows.Media.Imaging.BitmapFrame.Create(
                        new System.Windows.Media.Imaging.BitmapImage(resLight));
                }
                else
                {
                    mainTitleBar.NotifyIconImage = System.Windows.Media.Imaging.BitmapFrame.Create(
                        new System.Windows.Media.Imaging.BitmapImage(resDark));
                }
            }
            if (isForceReset)
            {

                mainTitleBar.ResetIcon();
            }
        }

        public bool IsThemeLightMode()
        {
            if (this.isWindows11)
            {
                return IsThemeLightModeForWin11();
            }
            else
            {
                // To be removed in the future.
                WPFUI.Theme.Style style = WPFUI.Theme.Manager.GetSystemTheme();
                return (style == WPFUI.Theme.Style.Light);
            }
        }
        private static bool IsThemeLightModeForWin11()
        {
            RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            int sysLightTheme = (int)key.GetValue("SystemUsesLightTheme");

            bool isLight = (sysLightTheme == 1);
            return isLight;
        }


        /// <summary>
        /// Live-applies activeSettings after an edit in the settings flyout (or the Win+F2 hotkey).
        /// Rather than redrawing from the UI thread and restarting the worker, this asks the running
        /// worker for a one-off redraw, so it stays the only thing touching the taskbar regions and
        /// the fill-on-maximise state is respected.
        /// </summary>
        public void ApplySettings()
        {
            if (shouldReallyDieNoReally || taskbarsRestored)
            {
                return;
            }
            activeSettings.IsCentred = Taskbar.CheckIfCentred();
            if (!activeSettings.FillOnMaximise)
            {
                activeSettings.FillOnTaskSwitch = false;
            }

            background.RequestSettingsRedraw();
            if (!taskbarThread.IsBusy)
            {
                StartTaskbarWorker(null);
            }

            if (activeSettings.AutoHide != appliedAutoHide)
            {
                AutoHide(activeSettings.AutoHide > 0, taskbarDetails);
                appliedAutoHide = activeSettings.AutoHide;
            }
            interaction.WriteJSON();
        }

        /// <summary>
        /// Draws every taskbar straight away from activeSettings and (re)starts the worker. Used at startup.
        /// </summary>
        public void ApplySettingsNow()
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
            catch (InvalidOperationException aaaa)
            {
                interaction.AddLog(aaaa.Message);
            }


            if (taskbarThread.IsBusy == false)
            {
                StartTaskbarWorker(null);
            }
            else
            {
                taskbarThread.CancelAsync();
                while (taskbarThread.IsBusy == true)
                {
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(100);
                }
                StartTaskbarWorker(null);
            }

            AutoHide(activeSettings.AutoHide > 0, taskbarDetails);
            appliedAutoHide = activeSettings.AutoHide;
            interaction.WriteJSON();
            TrayIconCheck(isForceReset: true);
        }

        /// <summary>
        /// Settings changed from somewhere other than the flyout (the Win+F2 hotkey); apply them and
        /// refresh the flyout if it is open.
        /// </summary>
        public void SettingsChangedExternally()
        {
            ApplySettings();
            flyout?.RefreshFromSettings();
        }

        // Guards against overlapping watermark work: the toggle, startup and Explorer recovery can
        // all ask at once, and the patch (symbol download + dbghelp + a cross-process write) must
        // never run on the UI or worker thread.
        private int watermarkBusy = 0;

        /// <summary>
        /// Brings the Insider watermark into line with <see cref="Types.Settings.RemoveInsiderWatermark"/>:
        /// patches Explorer's shell32 when enabled, restores it when disabled. Runs on a background
        /// thread and reports the outcome to the log and (if open) the flyout.
        /// </summary>
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
                    void Log(string msg) => interaction.AddLog($"Watermark: {msg}");
                    WatermarkRemover.Result result = enable
                        ? WatermarkRemover.Apply(Log)
                        : WatermarkRemover.Restore(Log);
                    if (!result.Success)
                    {
                        interaction.AddLog($"Watermark: {result.Message}");
                    }
                    Dispatcher.BeginInvoke(new Action(() => flyout?.SetWatermarkStatus(enable, result)));
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

        /// <summary>
        /// Called by the worker when Explorer has restarted. The watermark patch lives in Explorer's
        /// memory and is gone with it, so re-apply it (after a short settle so the desktop is up).
        /// </summary>
        public void OnExplorerRecovered()
        {
            if (activeSettings.RemoveInsiderWatermark)
            {
                interaction.AddLog("Watermark: Explorer restarted - re-applying.");
                SyncWatermarkState(initialDelayMs: 1500);
            }
        }

        public void Quit()
        {
            CloseMenuItem_Click(null, null);
        }

        /// <summary>
        /// Stops the background worker and returns every taskbar to its unmodified state.
        ///
        /// Safe to call more than once, because there are two ways out of the app and only
        /// one of them goes through OnClosing's "really closing" branch: the title bar's
        /// close button has ApplicationNavigation="True", so WPF-UI calls
        /// Application.Shutdown() directly and OnClosing still sees shouldReallyDieNoReally
        /// as false. App.OnExit therefore calls this as a backstop - without it, the window
        /// regions survive the process and the taskbar stays clipped.
        /// </summary>
        public void RestoreTaskbars()
        {
            if (taskbarsRestored)
            {
                return;
            }
            taskbarsRestored = true;

            try
            {
                taskbarThread.CancelAsync();
            }
            catch (Exception aaaa)
            {
                interaction.AddLog(aaaa.Message);
            }

            // Bounded wait: a hang here would leave the taskbar clipped, which is exactly
            // the failure we are trying to prevent. The worker sleeps 100ms per iteration.
            DateTime waitUntil = DateTime.UtcNow.AddSeconds(2);
            while (taskbarThread.IsBusy && DateTime.UtcNow < waitUntil)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(50);
            }

            try
            {
                foreach (var tbDeets in taskbarDetails)
                {
                    Taskbar.ResetTaskbar(tbDeets, activeSettings);
                }
                if (activeSettings.AutoHide > 0)
                {
                    AutoHide(false, taskbarDetails);
                }
            }
            catch (InvalidOperationException aaaa)
            {
                interaction.AddLog($"Taskbar structure changed on exit:\n{aaaa.Message}");
            }
            interaction.AddLog("Exiting RoundedTB.");
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (shouldReallyDieNoReally == false)
            {
                e.Cancel = true;
                Visibility = Visibility.Hidden;
            }
            else
            {
                RestoreTaskbars();
            }
            if (!isAlreadyRunning)
            {
                interaction.WriteJSON();
            }
        }

        private void CloseMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Close any popups - leave main window for now
            for (int windowCount = App.Current.Windows.Count - 1; windowCount >= 0; windowCount--)
            {
                App.Current.Windows[windowCount].Close();
            }

            shouldReallyDieNoReally = true;

            Close();
        }

        public void ShowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowSettings();
        }

        private async void Startup_Clicked(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Startup toggled");
            await SetRunAtStartup(StartupCheckBox.IsChecked == true);
        }

        /// <summary>
        /// Turns run-at-startup on or off, then brings the tray menu's check box in line with reality.
        /// </summary>
        public async Task SetRunAtStartup(bool enabled)
        {
            if (IsRunningAsUWP())
            {
                StartupTask startupTask = await StartupTask.GetAsync("RTB");
                bool isEnabled = startupTask.State == StartupTaskState.Enabled;
                if (isEnabled != enabled)
                {
                    await StartupToggle();
                }
                await StartupInit(false);
                return;
            }

            string startupLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "RoundedTB.lnk");
            try
            {
                if (enabled && !System.IO.File.Exists(startupLink))
                {
                    EnableStartup();
                }
                else if (!enabled && System.IO.File.Exists(startupLink))
                {
                    System.IO.File.Delete(startupLink);
                }
            }
            catch (Exception ex)
            {
                interaction.AddLog($"Failed to change startup setting: {ex.Message}");
            }
            StartupCheckBox.IsChecked = System.IO.File.Exists(startupLink);
        }

        public void EnableStartup()
        {
            try
            {
                string shortcutFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                if (!Directory.Exists(shortcutFolder))
                {
                    Directory.CreateDirectory(shortcutFolder);
                }
                string rtbStartupLink = Path.Combine(shortcutFolder, "RoundedTB.lnk");
                // Not GetCommandLineArgs()[0]: on .NET Core that is RoundedTB.dll, which the shortcut
                // can't launch. ProcessPath is the apphost, RoundedTB.exe.
                string rtbExecutable = Environment.ProcessPath;
                ShellLink.CreateShortcut(
                    linkPath: rtbStartupLink,
                    targetPath: rtbExecutable,
                    iconPath: rtbExecutable,
                    arguments: "",
                    description: "Start RoundedTB");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Earlier .NET 10 builds wrote the startup shortcut (or its icon) against RoundedTB.dll.
        /// Point it at the RoundedTB.exe beside that DLL instead, keeping it on whichever build the
        /// user set up rather than moving it to the one that happens to be running.
        /// </summary>
        private void RepairStartupShortcut()
        {
            string startupLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "RoundedTB.lnk");
            if (!System.IO.File.Exists(startupLink) || !ShellLink.TryReadShortcut(startupLink, out string target, out string icon))
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
                ShellLink.CreateShortcut(startupLink, fixedTarget, string.IsNullOrEmpty(fixedIcon) ? fixedTarget : fixedIcon, "", "Start RoundedTB");
                interaction.AddLog($"Repaired startup shortcut to point at {fixedTarget}");
            }
            catch (Exception ex)
            {
                interaction.AddLog($"Failed to repair startup shortcut: {ex.Message}");
            }
        }

        private static string DllToExe(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
            string exe = Path.ChangeExtension(path, ".exe");
            return System.IO.File.Exists(exe) ? exe : path;
        }

        async Task StartupToggle()
        {
            StartupTask startupTask = await StartupTask.GetAsync("RTB"); // Pass the task ID you specified in the appxmanifest file
            switch (startupTask.State)
            {
                case StartupTaskState.Disabled:
                    StartupTaskState newState = await startupTask.RequestEnableAsync();
                    StartupCheckBox.IsEnabled = true;
                    break;

                case StartupTaskState.DisabledByUser:
                    StartupCheckBox.IsEnabled = false;
                    break;

                case StartupTaskState.EnabledByPolicy:
                    StartupCheckBox.IsEnabled = false;
                    break;

                case StartupTaskState.DisabledByPolicy:
                    StartupCheckBox.IsEnabled = false;
                    break;

                case StartupTaskState.Enabled:
                    startupTask.Disable();
                    StartupCheckBox.IsEnabled = true;
                    break;
            }
        }

        async Task StartupInit(bool clean)
        {
            StartupTask startupTask = await StartupTask.GetAsync("RTB");
            switch (startupTask.State)
            {
                case StartupTaskState.Disabled:
                    StartupCheckBox.IsChecked = false;
                    StartupCheckBox.IsEnabled = true;
                    if (clean)
                    {
                        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowSettings));
                    }
                    StartupCheckBox.Content = "Run at startup";
                    break;

                case StartupTaskState.DisabledByUser:
                    StartupCheckBox.IsChecked = false;
                    StartupCheckBox.IsEnabled = false;
                    if (clean)
                    {
                        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowSettings));
                    }
                    StartupCheckBox.Content = "Startup unavailable";
                    break;

                case StartupTaskState.EnabledByPolicy:
                    StartupCheckBox.IsChecked = true;
                    StartupCheckBox.IsEnabled = false;
                    StartupCheckBox.Content = "Startup mandatory";
                    break;

                case StartupTaskState.DisabledByPolicy:
                    StartupCheckBox.IsChecked = false;
                    StartupCheckBox.IsEnabled = false;
                    if (clean)
                    {
                        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowSettings));
                    }
                    StartupCheckBox.Content = "Startup unavailable";
                    break;

                case StartupTaskState.Enabled:
                    StartupCheckBox.IsChecked = true;
                    StartupCheckBox.IsEnabled = true;
                    StartupCheckBox.Content = "Run at startup";
                    break;
            }
        }

        // Checks if running as a UWP app
        public bool IsRunningAsUWP()
        {
            try
            {
                Helpers helpers = new Helpers();
                return helpers.IsRunningAsUwp();
            }
            catch (Exception)
            {
                return false;
            }

        }

        private async void ContextMenu_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (IsRunningAsUWP())
            {
                await StartupInit(false);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            Debug.WriteLine("AAAAA");
            base.OnSourceInitialized(e);


            IntPtr handle = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(handle);
            source.AddHook(interaction.HwndHook);
            bool wtf = LocalPInvoke.RegisterHotKey(handle, 9000, 0x8, 0x71);
            Debug.WriteLine("KEY: " + wtf);
            Debug.WriteLine(handle);
            Debug.WriteLine((int)Types.KeyModifier.WinKey);
            Debug.WriteLine(System.Windows.Forms.Keys.J.GetHashCode());
            Visibility = Visibility.Hidden;
            Opacity = 1;
        }
    }
}
