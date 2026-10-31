using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RoundedTB
{
    /// <summary>
    /// Settings flyout that opens from the tray icon and sits next to the taskbar, like the Windows 11
    /// quick settings and volume flyouts. Every change is written straight into
    /// MainWindow.activeSettings and applied after a short debounce, so there is no Apply button.
    /// </summary>
    public partial class SettingsFlyout : Window
    {
        private readonly MainWindow mw;
        private readonly DispatcherTimer applyTimer;

        // Set while controls are being populated from the settings, so their change events don't
        // write the same values straight back (and so slider coercion can't clamp a saved value).
        private bool loading;

        // A click on the tray icon while the flyout is open deactivates it first, which hides it.
        // Without this the click handler would then immediately open it again.
        private DateTime lastHidden = DateTime.MinValue;
        private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);

        private IntPtr hwnd;
        private IntPtr targetMonitor = IntPtr.Zero;
        private LocalPInvoke.RECT targetTaskbarRect;
        private bool repositioning;

        private const int DefaultRadiusMax = 32;
        private const int DefaultMarginMax = 20;

        public SettingsFlyout(MainWindow mainWindow)
        {
            mw = mainWindow;
            InitializeComponent();

            applyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            applyTimer.Tick += (s, e) => FlushApply();

            if (!mw.isWindows11)
            {
                dynamicModeRadio.Content = "Split";
                dynamicModeRadio.ToolTip = "Split the taskbar into segments. See Help for the setup it needs on Windows 10.";
                fillTaskSwitchToggle.Visibility = Visibility.Collapsed;
            }

            segAppsRadio.IsChecked = true;
            Deactivated += (s, e) => HideFlyout();
            SizeChanged += (s, e) => Reposition();
            DpiChanged += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reposition));
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    HideFlyout();
                    e.Handled = true;
                }
            };
        }

        #region Showing, hiding and placement

        /// <summary>
        /// Opens the flyout, or closes it if it is already open.
        /// </summary>
        public void Toggle()
        {
            if (IsVisible)
            {
                HideFlyout();
            }
            else if (DateTime.UtcNow - lastHidden > ReopenGuard)
            {
                ShowFlyout();
            }
        }

        public void ShowFlyout()
        {
            LoadFromSettings();
            ApplyTheme();
            PickTarget();

            if (!IsVisible)
            {
                // Park the hidden window on the target monitor first so that WPF picks up that
                // monitor's DPI before it is laid out, then reveal it once it has been placed.
                Opacity = 0;
                hwnd = new WindowInteropHelper(this).EnsureHandle();
                MonitorInfo(targetMonitor, out LocalPInvoke.RECT monitorRect, out _);
                Flyout.SetWindowPos(hwnd, IntPtr.Zero, monitorRect.Left + 8, monitorRect.Top + 8, 0, 0,
                    Flyout.SWP_NOSIZE | Flyout.SWP_NOZORDER | Flyout.SWP_NOACTIVATE);
                Show();
            }

            Reposition();
            Activate();
            Flyout.SetForegroundWindow(hwnd);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                Reposition();
                Opacity = 1;
            }));
        }

        public void HideFlyout()
        {
            if (!IsVisible)
            {
                return;
            }
            FlushApply();
            Hide();
            lastHidden = DateTime.UtcNow;
        }

        /// <summary>
        /// Works out which monitor and taskbar to open against: the one under the cursor (where the
        /// tray icon was just clicked), falling back to the primary taskbar.
        /// </summary>
        private void PickTarget()
        {
            LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT cursor);
            targetMonitor = Flyout.MonitorFromPoint(cursor, Flyout.MONITOR_DEFAULTTOPRIMARY);

            Types.Taskbar match = null;
            try
            {
                foreach (Types.Taskbar taskbar in mw.taskbarDetails)
                {
                    if (taskbar.TaskbarHwnd == IntPtr.Zero)
                    {
                        continue;
                    }
                    if (LocalPInvoke.MonitorFromWindow(taskbar.TaskbarHwnd, Flyout.MONITOR_DEFAULTTONEAREST) == targetMonitor)
                    {
                        match = taskbar;
                        break;
                    }
                    match ??= taskbar;
                }
            }
            catch (Exception ex)
            {
                // The worker swaps the list out when it regenerates; just fall back below.
                Debug.WriteLine($"Flyout could not read taskbar list: {ex.Message}");
            }

            if (match != null && LocalPInvoke.GetWindowRect(match.TaskbarHwnd, out LocalPInvoke.RECT liveRect))
            {
                targetTaskbarRect = liveRect;
                targetMonitor = LocalPInvoke.MonitorFromWindow(match.TaskbarHwnd, Flyout.MONITOR_DEFAULTTONEAREST);
            }
            else
            {
                MonitorInfo(targetMonitor, out LocalPInvoke.RECT monitorRect, out LocalPInvoke.RECT workRect);
                targetTaskbarRect = new LocalPInvoke.RECT { Left = monitorRect.Left, Right = monitorRect.Right, Top = workRect.Bottom, Bottom = monitorRect.Bottom };
            }
        }

        /// <summary>
        /// Places the flyout 12px (scaled) in from the screen edge and away from the taskbar, on
        /// whichever edge the taskbar is docked to. Everything is in physical pixels.
        /// </summary>
        private void Reposition()
        {
            if (!IsVisible || hwnd == IntPtr.Zero || repositioning)
            {
                return;
            }
            repositioning = true;
            try
            {
                MonitorInfo(targetMonitor, out LocalPInvoke.RECT mon, out _);
                double scale = Flyout.GetDpiForWindow(hwnd) / 96.0;
                if (scale <= 0)
                {
                    scale = 1;
                }
                int gap = (int)Math.Round(12 * scale);
                LocalPInvoke.RECT tb = targetTaskbarRect;

                bool taskbarIntersects = tb.Right > mon.Left && tb.Left < mon.Right && tb.Bottom > mon.Top && tb.Top < mon.Bottom;
                int tbWidth = tb.Right - tb.Left;
                int tbHeight = tb.Bottom - tb.Top;
                bool horizontal = tbWidth >= tbHeight;

                // Space left for the flyout once the taskbar is taken out.
                int available = mon.Bottom - mon.Top - 2 * gap;
                if (taskbarIntersects && horizontal)
                {
                    available -= tbHeight;
                }
                double maxHeightDip = Math.Max(200, available / scale);
                if (Math.Abs(MaxHeight - maxHeightDip) > 0.5)
                {
                    MaxHeight = maxHeightDip;
                }

                int width = (int)Math.Round(ActualWidth * scale);
                int height = (int)Math.Round(Math.Min(ActualHeight, maxHeightDip) * scale);
                int x = mon.Right - gap - width;
                int y = mon.Bottom - gap - height;

                if (taskbarIntersects)
                {
                    int monMidY = (mon.Top + mon.Bottom) / 2;
                    int monMidX = (mon.Left + mon.Right) / 2;
                    if (horizontal && tb.Top >= monMidY)
                    {
                        y = tb.Top - gap - height;              // Bottom taskbar
                    }
                    else if (horizontal)
                    {
                        y = tb.Bottom + gap;                    // Top taskbar
                    }
                    else if (tb.Left >= monMidX)
                    {
                        x = tb.Left - gap - width;              // Right taskbar
                    }
                    else
                    {
                        x = tb.Right + gap;                     // Left taskbar
                        x = Math.Max(x, mon.Left + gap);
                    }
                }

                x = Math.Max(mon.Left, Math.Min(x, mon.Right - width));
                y = Math.Max(mon.Top, Math.Min(y, mon.Bottom - height));
                Flyout.SetWindowPos(hwnd, Flyout.HWND_TOPMOST, x, y, 0, 0, Flyout.SWP_NOSIZE | Flyout.SWP_NOACTIVATE);
            }
            finally
            {
                repositioning = false;
            }
        }

        private static void MonitorInfo(IntPtr monitor, out LocalPInvoke.RECT monitorRect, out LocalPInvoke.RECT workRect)
        {
            MonitorStuff.MONITORINFO mi = new MonitorStuff.MONITORINFO();
            mi.cbSize = (uint)Marshal.SizeOf(mi);
            if (monitor != IntPtr.Zero && MonitorStuff.GetMonitorInfo(monitor, ref mi))
            {
                monitorRect = mi.rcMonitor;
                workRect = mi.rcWork;
                return;
            }
            System.Drawing.Rectangle b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            System.Drawing.Rectangle w = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            monitorRect = new LocalPInvoke.RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
            workRect = new LocalPInvoke.RECT { Left = w.Left, Top = w.Top, Right = w.Right, Bottom = w.Bottom };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            hwnd = new WindowInteropHelper(this).Handle;

            // Tool window: no taskbar button and no Alt+Tab entry, like the shell's own flyouts.
            int exStyle = Flyout.GetWindowLong(hwnd, Flyout.GWL_EXSTYLE);
            exStyle = (exStyle | Flyout.WS_EX_TOOLWINDOW) & ~Flyout.WS_EX_APPWINDOW;
            Flyout.SetWindowLong(hwnd, Flyout.GWL_EXSTYLE, exStyle);

            // Let DWM's backdrop show through WPF's surface.
            HwndSource source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            int round = Flyout.DWMWCP_ROUND;
            Flyout.DwmSetWindowAttribute(hwnd, Flyout.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            if (SupportsSystemBackdrop)
            {
                int backdrop = Flyout.DWMSBT_TRANSIENTWINDOW;
                Flyout.DwmSetWindowAttribute(hwnd, Flyout.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            }
        }

        // DWMWA_SYSTEMBACKDROP_TYPE arrived in 22621 (22H2). Older builds get a solid background.
        private static bool SupportsSystemBackdrop => Environment.OSVersion.Version.Build >= 22621;

        #endregion

        #region Theme

        /// <summary>
        /// Follows the system (shell) theme rather than the app theme, the same as the taskbar's flyouts.
        /// </summary>
        private void ApplyTheme()
        {
            bool light = false;
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                light = key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
            }
            catch (Exception) { }

            Color accent = light ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x60, 0xCD, 0xFF);
            try
            {
                var ui = new Windows.UI.ViewManagement.UISettings();
                var c = ui.GetColorValue(light ? Windows.UI.ViewManagement.UIColorType.AccentDark1 : Windows.UI.ViewManagement.UIColorType.AccentLight2);
                accent = Color.FromArgb(c.A, c.R, c.G, c.B);
            }
            catch (Exception) { }

            if (light)
            {
                SetBrush("FlyoutText", 0xE4, 0x00, 0x00, 0x00);
                SetBrush("FlyoutTextSecondary", 0x9E, 0x00, 0x00, 0x00);
                SetBrush("FlyoutTextDisabled", 0x5C, 0x00, 0x00, 0x00);
                SetBrush("FlyoutCard", 0xB3, 0xFF, 0xFF, 0xFF);
                SetBrush("FlyoutCardStroke", 0x0F, 0x00, 0x00, 0x00);
                SetBrush("FlyoutControl", 0x05, 0x00, 0x00, 0x00);
                SetBrush("FlyoutControlHover", 0x0A, 0x00, 0x00, 0x00);
                SetBrush("FlyoutControlStroke", 0x72, 0x00, 0x00, 0x00);
                SetBrush("FlyoutTrack", 0x72, 0x00, 0x00, 0x00);
                SetBrush("FlyoutOnAccent", 0xFF, 0xFF, 0xFF, 0xFF);
                SetBrush("FlyoutThumb", 0xFF, 0xFF, 0xFF, 0xFF);
                SetBrush("FlyoutFallback", 0xFF, 0xF3, 0xF3, 0xF3);
                SetBrush("FlyoutFooter", 0x08, 0x00, 0x00, 0x00);
            }
            else
            {
                SetBrush("FlyoutText", 0xFF, 0xFF, 0xFF, 0xFF);
                SetBrush("FlyoutTextSecondary", 0xC5, 0xFF, 0xFF, 0xFF);
                SetBrush("FlyoutTextDisabled", 0x5D, 0xFF, 0xFF, 0xFF);
                SetBrush("FlyoutCard", 0x0D, 0xFF, 0xFF, 0xFF);
                SetBrush("FlyoutCardStroke", 0x19, 0x00, 0x00, 0x00);
                SetBrush("FlyoutControl", 0x0F, 0xFF, 0xFF, 0xFF);
                SetBrush("FlyoutControlHover", 0x15, 0xFF, 0xFF, 0xFF);
                SetBrush("FlyoutControlStroke", 0x9A, 0xFF, 0xFF, 0xFF);
                SetBrush("FlyoutTrack", 0x8B, 0xFF, 0xFF, 0xFF);
                SetBrush("FlyoutOnAccent", 0xFF, 0x00, 0x00, 0x00);
                SetBrush("FlyoutThumb", 0xFF, 0x45, 0x45, 0x45);
                SetBrush("FlyoutFallback", 0xFF, 0x20, 0x20, 0x20);
                SetBrush("FlyoutFooter", 0x30, 0x00, 0x00, 0x00);
            }
            Resources["FlyoutAccent"] = new SolidColorBrush(accent);

            RootBorder.Background = SupportsSystemBackdrop ? Brushes.Transparent : (Brush)Resources["FlyoutFallback"];

            if (hwnd != IntPtr.Zero)
            {
                int dark = light ? 0 : 1;
                Flyout.DwmSetWindowAttribute(hwnd, Flyout.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
        }

        private void SetBrush(string key, byte a, byte r, byte g, byte b)
        {
            Resources[key] = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }

        #endregion

        #region Settings <-> controls

        private Types.Settings Settings => mw.activeSettings;

        /// <summary>
        /// The layout the main Shape controls show. They write to every layout, so any of them would
        /// do; this is the one actually drawn in the current mode.
        /// </summary>
        private Types.SegmentSettings MainLayout => Settings.IsDynamic ? Settings.DynamicAppListLayout : Settings.SimpleTaskbarLayout;

        private void LoadFromSettings()
        {
            loading = true;
            try
            {
                Types.Settings s = Settings;
                basicModeRadio.IsChecked = !s.IsDynamic;
                dynamicModeRadio.IsChecked = s.IsDynamic;

                Types.SegmentSettings main = MainLayout;
                SetSlider(radiusSlider, main.CornerRadius, DefaultRadiusMax);
                bool linked = main.MarginTop == main.MarginBottom && main.MarginTop == main.MarginLeft && main.MarginTop == main.MarginRight;
                linkMarginsToggle.IsChecked = linked;
                SetSlider(marginAllSlider, main.MarginTop, DefaultMarginMax);
                SetSlider(marginTopSlider, main.MarginTop, DefaultMarginMax);
                SetSlider(marginBottomSlider, main.MarginBottom, DefaultMarginMax);
                SetSlider(marginLeftSlider, main.MarginLeft, DefaultMarginMax);
                SetSlider(marginRightSlider, main.MarginRight, DefaultMarginMax);

                showTrayToggle.IsChecked = s.ShowTray;
                showWidgetsToggle.IsChecked = s.ShowWidgets;
                showClockToggle.IsChecked = s.ShowSecondaryClock;
                fillMaximisedToggle.IsChecked = s.FillOnMaximise;
                fillTaskSwitchToggle.IsChecked = s.FillOnTaskSwitch;
                hoverToggle.IsChecked = s.ShowSegmentsOnHover;
                compositionToggle.IsChecked = s.CompositionCompat;
                autoHideToggle.IsChecked = s.AutoHide == 1;

                startupToggle.IsChecked = mw.StartupCheckBox.IsChecked == true;
                startupToggle.IsEnabled = mw.StartupCheckBox.IsEnabled;

                LoadSegment();
            }
            finally
            {
                loading = false;
            }
            UpdateLabelsAndStates();
        }

        private static void SetSlider(Slider slider, int value, int defaultMax)
        {
            // Configs written by the old text boxes can hold anything; widen the range rather than
            // let the slider's coercion silently change a saved value.
            slider.Minimum = Math.Min(0, value);
            slider.Maximum = Math.Max(defaultMax, value);
            slider.Value = value;
        }

        private Types.SegmentSettings SelectedSegmentLayout
        {
            get
            {
                if (segTrayRadio.IsChecked == true) return Settings.DynamicTrayLayout;
                if (segWidgetsRadio.IsChecked == true) return Settings.DynamicWidgetsLayout;
                if (segClockRadio.IsChecked == true) return Settings.DynamicSecondaryClockLayout;
                return Settings.DynamicAppListLayout;
            }
        }

        private void LoadSegment()
        {
            bool wasLoading = loading;
            loading = true;
            try
            {
                Types.SegmentSettings layout = SelectedSegmentLayout;
                if (layout == null)
                {
                    return;
                }
                SetSlider(segRadiusSlider, layout.CornerRadius, DefaultRadiusMax);
                SetSlider(segTopSlider, layout.MarginTop, DefaultMarginMax);
                SetSlider(segBottomSlider, layout.MarginBottom, DefaultMarginMax);
                SetSlider(segLeftSlider, layout.MarginLeft, DefaultMarginMax);
                SetSlider(segRightSlider, layout.MarginRight, DefaultMarginMax);

                bool hasWidth = segWidgetsRadio.IsChecked == true || segClockRadio.IsChecked == true;
                segWidthPanel.Visibility = hasWidth ? Visibility.Visible : Visibility.Collapsed;
                if (hasWidth)
                {
                    int width = segWidgetsRadio.IsChecked == true ? Settings.WidgetsWidth : Settings.ClockWidth;
                    segWidthSlider.Minimum = Math.Min(40, width);
                    segWidthSlider.Maximum = Math.Max(400, width);
                    segWidthSlider.Value = width;
                }
            }
            finally
            {
                loading = wasLoading;
            }
        }

        private void UpdateLabelsAndStates()
        {
            radiusValue.Text = $"{(int)radiusSlider.Value} px";
            marginAllValue.Text = $"{(int)marginAllSlider.Value} px";
            marginTopValue.Text = $"{(int)marginTopSlider.Value}";
            marginBottomValue.Text = $"{(int)marginBottomSlider.Value}";
            marginLeftValue.Text = $"{(int)marginLeftSlider.Value}";
            marginRightValue.Text = $"{(int)marginRightSlider.Value}";
            segRadiusValue.Text = $"{(int)segRadiusSlider.Value} px";
            segTopValue.Text = $"{(int)segTopSlider.Value}";
            segBottomValue.Text = $"{(int)segBottomSlider.Value}";
            segLeftValue.Text = $"{(int)segLeftSlider.Value}";
            segRightValue.Text = $"{(int)segRightSlider.Value}";
            segWidthValue.Text = $"{(int)segWidthSlider.Value} px";

            bool linked = linkMarginsToggle.IsChecked == true;
            linkedMarginPanel.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
            splitMarginPanel.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;

            bool dynamic = Settings.IsDynamic;
            segmentsSection.Visibility = dynamic ? Visibility.Visible : Visibility.Collapsed;
            segmentLayoutHeader.Visibility = dynamic ? Visibility.Visible : Visibility.Collapsed;
            segmentLayoutCard.Visibility = dynamic ? Visibility.Visible : Visibility.Collapsed;
            hoverToggle.IsEnabled = dynamic;

            // Hover mode drives tray/widgets visibility itself.
            bool hover = Settings.ShowSegmentsOnHover;
            showTrayToggle.IsEnabled = !hover;
            showWidgetsToggle.IsEnabled = !hover;

            fillTaskSwitchToggle.IsEnabled = Settings.FillOnMaximise && mw.isWindows11;
        }

        private void ScheduleApply()
        {
            applyTimer.Stop();
            applyTimer.Start();
        }

        private void FlushApply()
        {
            if (!applyTimer.IsEnabled)
            {
                return;
            }
            applyTimer.Stop();
            try
            {
                mw.ApplySettings();
            }
            catch (Exception ex)
            {
                mw.interaction.AddLog($"Applying settings failed: {ex.Message}");
                Debug.WriteLine($"Applying settings failed: {ex}");
            }
        }

        /// <summary>
        /// Called by MainWindow when settings change from outside the flyout (e.g. the Win+F2 hotkey).
        /// </summary>
        public void RefreshFromSettings()
        {
            if (IsVisible)
            {
                LoadFromSettings();
            }
        }

        #endregion

        #region Event handlers

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }
            bool dynamic = dynamicModeRadio.IsChecked == true;
            if (dynamic == Settings.IsDynamic)
            {
                return;
            }

            // Carry the visible shape across, so switching modes doesn't make it jump.
            Types.SegmentSettings current = ReadMainShape();
            Settings.IsDynamic = dynamic;
            if (dynamic)
            {
                if (!Settings.ShowSegmentsOnHover)
                {
                    Settings.ShowTray = true;
                }
                SetAllLayoutsIfUniform(current);
            }
            else
            {
                Settings.ShowSegmentsOnHover = false;
                Settings.SimpleTaskbarLayout = Copy(current);
            }

            LoadFromSettings();
            ScheduleApply();
        }

        /// <summary>
        /// Switching to dynamic only overwrites the per-segment layouts if they were all still the same
        /// as each other, so a hand-tuned set of segment layouts survives a trip through Basic.
        /// </summary>
        private void SetAllLayoutsIfUniform(Types.SegmentSettings shape)
        {
            Types.Settings s = Settings;
            if (Same(s.DynamicAppListLayout, s.DynamicTrayLayout) && Same(s.DynamicAppListLayout, s.DynamicWidgetsLayout)
                && Same(s.DynamicAppListLayout, s.DynamicSecondaryClockLayout))
            {
                s.DynamicAppListLayout = Copy(shape);
                s.DynamicTrayLayout = Copy(shape);
                s.DynamicWidgetsLayout = Copy(shape);
                s.DynamicSecondaryClockLayout = Copy(shape);
            }
        }

        private Types.SegmentSettings ReadMainShape()
        {
            bool linked = linkMarginsToggle.IsChecked == true;
            int all = (int)marginAllSlider.Value;
            return new Types.SegmentSettings
            {
                CornerRadius = (int)radiusSlider.Value,
                MarginTop = linked ? all : (int)marginTopSlider.Value,
                MarginBottom = linked ? all : (int)marginBottomSlider.Value,
                MarginLeft = linked ? all : (int)marginLeftSlider.Value,
                MarginRight = linked ? all : (int)marginRightSlider.Value,
            };
        }

        private void MainShape_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (loading || !IsInitialized)
            {
                return;
            }

            // The main controls are "one shape for the whole taskbar": write it to every layout.
            Types.SegmentSettings shape = ReadMainShape();
            Settings.SimpleTaskbarLayout = Copy(shape);
            Settings.DynamicAppListLayout = Copy(shape);
            Settings.DynamicTrayLayout = Copy(shape);
            Settings.DynamicWidgetsLayout = Copy(shape);
            Settings.DynamicSecondaryClockLayout = Copy(shape);

            LoadSegment();
            UpdateLabelsAndStates();
            ScheduleApply();
        }

        private void LinkMargins_Click(object sender, RoutedEventArgs e)
        {
            loading = true;
            try
            {
                if (linkMarginsToggle.IsChecked == true)
                {
                    SetSlider(marginAllSlider, (int)marginTopSlider.Value, DefaultMarginMax);
                }
                else
                {
                    int all = (int)marginAllSlider.Value;
                    SetSlider(marginTopSlider, all, DefaultMarginMax);
                    SetSlider(marginBottomSlider, all, DefaultMarginMax);
                    SetSlider(marginLeftSlider, all, DefaultMarginMax);
                    SetSlider(marginRightSlider, all, DefaultMarginMax);
                }
            }
            finally
            {
                loading = false;
            }
            // Linking collapses the four sides to one value; write that out.
            MainShape_ValueChanged(sender, null);
        }

        private void Segment_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized)
            {
                return;
            }
            LoadSegment();
            UpdateLabelsAndStates();
        }

        private void SegmentShape_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (loading || !IsInitialized)
            {
                return;
            }
            Types.SegmentSettings layout = SelectedSegmentLayout;
            layout.CornerRadius = (int)segRadiusSlider.Value;
            layout.MarginTop = (int)segTopSlider.Value;
            layout.MarginBottom = (int)segBottomSlider.Value;
            layout.MarginLeft = (int)segLeftSlider.Value;
            layout.MarginRight = (int)segRightSlider.Value;

            if (segWidgetsRadio.IsChecked == true)
            {
                Settings.WidgetsWidth = (int)segWidthSlider.Value;
            }
            else if (segClockRadio.IsChecked == true)
            {
                Settings.ClockWidth = (int)segWidthSlider.Value;
            }

            UpdateLabelsAndStates();
            ScheduleApply();
        }

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            Types.Settings s = Settings;

            if (sender == hoverToggle)
            {
                // Same coupling the old configurator had: hover mode owns tray/widgets visibility.
                bool hover = hoverToggle.IsChecked == true;
                showTrayToggle.IsChecked = !hover;
                showWidgetsToggle.IsChecked = !hover;
            }
            if (sender == fillMaximisedToggle && fillMaximisedToggle.IsChecked != true)
            {
                fillTaskSwitchToggle.IsChecked = false;
            }

            s.ShowTray = showTrayToggle.IsChecked == true;
            s.ShowWidgets = showWidgetsToggle.IsChecked == true;
            s.ShowSecondaryClock = showClockToggle.IsChecked == true;
            s.FillOnMaximise = fillMaximisedToggle.IsChecked == true;
            s.FillOnTaskSwitch = s.FillOnMaximise && fillTaskSwitchToggle.IsChecked == true;
            s.ShowSegmentsOnHover = hoverToggle.IsChecked == true;
            s.CompositionCompat = compositionToggle.IsChecked == true;
            // AutoHide is an index into the old combo box; 2 was a disabled placeholder.
            s.AutoHide = autoHideToggle.IsChecked == true ? 1 : 0;

            UpdateLabelsAndStates();
            ScheduleApply();
        }

        private async void Startup_Click(object sender, RoutedEventArgs e)
        {
            await mw.SetRunAtStartup(startupToggle.IsChecked == true);
            startupToggle.IsChecked = mw.StartupCheckBox.IsChecked == true;
            startupToggle.IsEnabled = mw.StartupCheckBox.IsEnabled;
        }

        private void Advanced_Click(object sender, RoutedEventArgs e)
        {
            advancedPanel.Visibility = advancedToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (advancedToggle.IsChecked == true)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => advancedToggle.BringIntoView()));
            }
        }

        private void Help_Click(object sender, RoutedEventArgs e)
        {
            HideFlyout();
            new AboutWindow().Show();
        }

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            FlushApply();
            mw.Quit();
        }

        #endregion

        private static Types.SegmentSettings Copy(Types.SegmentSettings s)
        {
            return new Types.SegmentSettings
            {
                CornerRadius = s.CornerRadius,
                MarginTop = s.MarginTop,
                MarginBottom = s.MarginBottom,
                MarginLeft = s.MarginLeft,
                MarginRight = s.MarginRight,
            };
        }

        private static bool Same(Types.SegmentSettings a, Types.SegmentSettings b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }
            return a.CornerRadius == b.CornerRadius && a.MarginTop == b.MarginTop && a.MarginBottom == b.MarginBottom
                && a.MarginLeft == b.MarginLeft && a.MarginRight == b.MarginRight;
        }

        /// <summary>
        /// Win32 bits only the flyout needs.
        /// </summary>
        private static class Flyout
        {
            public const int GWL_EXSTYLE = -20;
            public const int WS_EX_TOOLWINDOW = 0x00000080;
            public const int WS_EX_APPWINDOW = 0x00040000;
            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;
            public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
            public const uint MONITOR_DEFAULTTOPRIMARY = 1;
            public const uint MONITOR_DEFAULTTONEAREST = 2;

            public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
            public const int DWMWCP_ROUND = 2;
            public const int DWMSBT_TRANSIENTWINDOW = 3;

            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
            public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
            public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern IntPtr MonitorFromPoint(LocalPInvoke.POINT pt, uint dwFlags);

            [DllImport("user32.dll")]
            public static extern uint GetDpiForWindow(IntPtr hwnd);

            [DllImport("dwmapi.dll")]
            public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        }
    }
}
