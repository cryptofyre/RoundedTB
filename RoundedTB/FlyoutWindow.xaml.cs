using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace RoundedTB
{
    public sealed partial class FlyoutWindow : Window
    {
        private const double PanelWidth = 360;
        private const int DefaultRadiusMax = 32, DefaultMarginMax = 20;
        private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(150);

        private enum RevealEdge { Bottom, Top, Left, Right }

        private readonly AppHost host;
        private readonly IntPtr hwnd;
        private readonly Compositor compositor;
        private readonly Visual panelVisual;
        private readonly TintedAcrylicBackdrop acrylic;
        private readonly TransparentBackdrop clear = new();
        private readonly DispatcherQueueTimer applyTimer;
        private readonly Native.SubclassProc noFrameProc;

        // Sliders coerce and fire ValueChanged while the XAML loads, before the fields are set.
        private bool loading = true;
        private bool isOpen;
        private bool closing;
        private bool sliding;
        private DateTime lastHidden = DateTime.MinValue;
        private RevealEdge revealEdge = RevealEdge.Bottom;
        private Vector3 hiddenTranslation;
        private IntPtr targetMonitor;

        private Types.Settings Settings => host.activeSettings;

        public FlyoutWindow(AppHost host)
        {
            this.host = host;
            InitializeComponent();
            hwnd = WindowNative.GetWindowHandle(this);

            OverlappedPresenter presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            AppWindow.SetPresenter(presenter);
            AppWindow.IsShownInSwitchers = false;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);

            // The presenter re-adds WS_DLGFRAME on every show, leaving a 3px frame that DWM paints
            // white. Claim the whole window as client area instead of fighting over styles.
            noFrameProc = NoFrameSubclassProc;
            Native.SetWindowSubclass(hwnd, noFrameProc, 1, IntPtr.Zero);
            // Without this the clear parts of the window render black.
            Native.DwmExtendFrameIntoClientArea(hwnd, new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 });
            ApplyDwmAttributes();

            compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
            ElementCompositionPreview.GetElementVisual(Root).Clip = compositor.CreateInsetClip();
            ElementCompositionPreview.SetIsTranslationEnabled(Panel, true);
            panelVisual = ElementCompositionPreview.GetElementVisual(Panel);

            if (DesktopAcrylicController.IsSupported())
            {
                acrylic = new TintedAcrylicBackdrop();
                SystemBackdrop = acrylic;
            }
            ApplyTheme();

            SettingsStack.SizeChanged += (_, _) => FollowContentSize();
            AboutStack.SizeChanged += (_, _) => FollowContentSize();

            Activated += OnActivated;
            AppWindow.Closing += (_, e) =>
            {
                if (!host.IsQuitting)
                {
                    e.Cancel = true;
                    HideFlyout();
                }
            };
            Root.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    HideFlyout();
                }
            };

            applyTimer = DispatcherQueue.CreateTimer();
            applyTimer.Interval = TimeSpan.FromMilliseconds(250);
            applyTimer.IsRepeating = false;
            applyTimer.Tick += (_, _) => host.ApplySettings();

            loading = false;
        }

        private static IntPtr NoFrameSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint id, IntPtr refData)
        {
            if (msg == Native.WM_NCCALCSIZE && wParam != IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            return Native.DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        #region Theme and backdrop

        // RTB_THEME=light|dark overrides the shell theme, for testing.
        private static bool SystemUsesLightTheme() =>
            Environment.GetEnvironmentVariable("RTB_THEME") is "light" or "dark"
                ? Environment.GetEnvironmentVariable("RTB_THEME") == "light"
                : Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1;

        private void SetCloaked(bool cloaked)
        {
            int value = cloaked ? 1 : 0;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_CLOAK, ref value, sizeof(int));
        }

        private void ApplyDwmAttributes()
        {
            int corner = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int border = Native.DWMWA_COLOR_NONE;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }

        /// <summary>Follows the shell theme (SystemUsesLightTheme), like the taskbar, not the app theme.</summary>
        private void ApplyTheme()
        {
            bool light = SystemUsesLightTheme();
            Root.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
            acrylic?.SetTheme(light);
            UpdatePanelBackground();
        }

        /// <summary>
        /// Window-level acrylic covers the whole window at once instead of moving with the panel, so
        /// it is swapped for a near-opaque tint while the panel slides.
        /// </summary>
        private void UpdatePanelBackground()
        {
            byte shade = Root.RequestedTheme == ElementTheme.Light ? (byte)0xEE : (byte)0x1C;
            if (acrylic == null)
            {
                Panel.Background = new SolidColorBrush(Color.FromArgb(0xFF, shade, shade, shade));
            }
            else
            {
                Panel.Background = sliding ? new SolidColorBrush(Color.FromArgb(0xF7, shade, shade, shade)) : null;
            }
        }

        private void SetSliding(bool value)
        {
            sliding = value;
            if (acrylic != null)
            {
                SystemBackdrop = value ? clear : acrylic;
            }
            UpdatePanelBackground();
        }

        #endregion

        #region Open / close

        public void Toggle()
        {
            if (isOpen && !closing)
            {
                HideFlyout();
                return;
            }
            // Clicking the tray icon deactivates (and so hides) the flyout just before the click arrives.
            if ((DateTime.UtcNow - lastHidden).TotalMilliseconds < 300)
            {
                return;
            }
            ShowFlyout(helpPage: false);
        }

        public void ShowFlyout(bool helpPage)
        {
            // The XAML tree can't be measured until the window has been shown once, so the first
            // open shows it cloaked and carries on when it has loaded.
            if (!Root.IsLoaded)
            {
                SetCloaked(true);
                Root.Loaded += OnFirstLoad;
                AppWindow.Show(false);
                void OnFirstLoad(object sender, RoutedEventArgs e)
                {
                    Root.Loaded -= OnFirstLoad;
                    AppWindow.Hide();
                    SetCloaked(false);
                    ShowFlyout(helpPage);
                }
                return;
            }

            ShowAboutPage(helpPage);
            if (helpPage)
            {
                AboutTabs.SelectedIndex = 1;
            }
            if (isOpen && !closing)
            {
                Activate();
                return;
            }

            LoadFromSettings();
            ApplyTheme();
            Native.GetCursorPos(out Native.POINT cursor);
            targetMonitor = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
            Place();

            panelVisual.StopAnimation("Translation");
            panelVisual.Properties.InsertVector3("Translation", hiddenTranslation);
            SetSliding(true);
            isOpen = true;
            closing = false;
            AppWindow.Show(true);
            ApplyDwmAttributes();
            Activate();
            Native.SetForegroundWindow(hwnd);

            // Start on the first rendered frame, so a cold open doesn't lose half the slide.
            void OnFirstFrame(object sender, object e)
            {
                CompositionTarget.Rendering -= OnFirstFrame;
                Slide(opening: true, () =>
                {
                    if (isOpen && !closing)
                    {
                        SetSliding(false);
                        Place();
                    }
                });
            }
            CompositionTarget.Rendering += OnFirstFrame;
        }

        public void HideFlyout()
        {
            if (!isOpen || closing)
            {
                return;
            }
            closing = true;
            FlushPendingChanges();
            SetSliding(true);
            Slide(opening: false, () =>
            {
                AppWindow.Hide();
                isOpen = false;
                closing = false;
                lastHidden = DateTime.UtcNow;
            });
        }

        private void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated && isOpen)
            {
                lastHidden = DateTime.UtcNow;
                HideFlyout();
            }
        }

        private void Slide(bool opening, Action completed)
        {
            CubicBezierEasingFunction easing = opening
                ? compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f))
                : compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0f), new Vector2(1f, 0.5f));
            Vector3KeyFrameAnimation animation = compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(0f, opening ? hiddenTranslation : Vector3.Zero);
            animation.InsertKeyFrame(1f, opening ? Vector3.Zero : hiddenTranslation, easing);
            animation.Duration = opening ? OpenDuration : CloseDuration;

            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            panelVisual.StartAnimation("Translation", animation);
            batch.End();
            batch.Completed += (_, _) => completed();
        }

        private void FollowContentSize()
        {
            if (isOpen && !sliding)
            {
                Place();
            }
        }

        /// <summary>
        /// Sizes the window to the panel (capped to what fits beside the taskbar) and places it 12px
        /// from the screen edge and the taskbar, in the target monitor's physical pixels.
        /// </summary>
        private void Place()
        {
            Native.GetMonitor(targetMonitor, out Native.RECT mon, out Native.RECT work, out double scale);
            Native.RECT tb = Native.FindTaskbarRect(targetMonitor, mon, work);

            int gap = (int)Math.Round(12 * scale);
            bool horizontal = tb.Right - tb.Left >= tb.Bottom - tb.Top;
            int available = mon.Bottom - mon.Top - 2 * gap - (horizontal ? tb.Bottom - tb.Top : 0);

            Panel.MaxHeight = available / scale;
            Panel.Measure(new Windows.Foundation.Size(PanelWidth, double.PositiveInfinity));
            double desired = Panel.DesiredSize.Height > 0 ? Panel.DesiredSize.Height : 600;
            int width = (int)Math.Ceiling(PanelWidth * scale);
            int height = Math.Min(available, (int)Math.Ceiling(desired * scale));

            int x = mon.Right - gap - width;
            int y = mon.Bottom - gap - height;
            revealEdge = RevealEdge.Bottom;
            if (horizontal && tb.Top >= (mon.Top + mon.Bottom) / 2)
            {
                y = tb.Top - gap - height;
            }
            else if (horizontal)
            {
                revealEdge = RevealEdge.Top;
                y = tb.Bottom + gap;
            }
            else if (tb.Left >= (mon.Left + mon.Right) / 2)
            {
                revealEdge = RevealEdge.Right;
                x = tb.Left - gap - width;
            }
            else
            {
                revealEdge = RevealEdge.Left;
                x = tb.Right + gap;
            }
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

            // Translation is in DIPs; travel the full size so the panel starts behind the edge.
            float travelY = (float)(height / scale) + 2;
            float travelX = (float)PanelWidth + 2;
            hiddenTranslation = revealEdge switch
            {
                RevealEdge.Top => new Vector3(0, -travelY, 0),
                RevealEdge.Left => new Vector3(-travelX, 0, 0),
                RevealEdge.Right => new Vector3(travelX, 0, 0),
                _ => new Vector3(0, travelY, 0),
            };
        }

        #endregion

        #region Settings <-> controls

        /// <summary>The main Shape controls write every layout; this is the one drawn in the current mode.</summary>
        private Types.SegmentSettings MainLayout => Settings.IsDynamic ? Settings.DynamicAppListLayout : Settings.SimpleTaskbarLayout;

        private Types.SegmentSettings SelectedSegmentLayout => SegmentSegmented.SelectedIndex switch
        {
            1 => Settings.DynamicTrayLayout,
            2 => Settings.DynamicWidgetsLayout,
            3 => Settings.DynamicSecondaryClockLayout,
            _ => Settings.DynamicAppListLayout,
        };

        public void RefreshFromSettings()
        {
            if (isOpen)
            {
                LoadFromSettings();
            }
        }

        public void FlushPendingChanges()
        {
            if (applyTimer.IsRunning)
            {
                applyTimer.Stop();
                host.ApplySettings();
            }
        }

        private void ScheduleApply()
        {
            applyTimer.Stop();
            applyTimer.Start();
        }

        private async void LoadFromSettings()
        {
            loading = true;
            try
            {
                Types.Settings s = Settings;
                ModeSegmented.SelectedIndex = s.IsDynamic ? 1 : 0;

                Types.SegmentSettings main = MainLayout;
                SetSlider(RadiusSlider, main.CornerRadius, DefaultRadiusMax);
                LinkMarginsToggle.IsOn = main.MarginTop == main.MarginBottom && main.MarginTop == main.MarginLeft && main.MarginTop == main.MarginRight;
                SetSlider(MarginAllSlider, main.MarginTop, DefaultMarginMax);
                SetSlider(MarginTopSlider, main.MarginTop, DefaultMarginMax);
                SetSlider(MarginBottomSlider, main.MarginBottom, DefaultMarginMax);
                SetSlider(MarginLeftSlider, main.MarginLeft, DefaultMarginMax);
                SetSlider(MarginRightSlider, main.MarginRight, DefaultMarginMax);

                ShowTrayToggle.IsOn = s.ShowTray;
                ShowWidgetsToggle.IsOn = s.ShowWidgets;
                ShowClockToggle.IsOn = s.ShowSecondaryClock;
                FillMaximisedToggle.IsOn = s.FillOnMaximise;
                FillTaskSwitchToggle.IsOn = s.FillOnTaskSwitch;
                HoverToggle.IsOn = s.ShowSegmentsOnHover;
                CompositionToggle.IsOn = s.CompositionCompat;
                AutoHideToggle.IsOn = s.AutoHide == 1;

                if (SegmentSegmented.SelectedIndex < 0)
                {
                    SegmentSegmented.SelectedIndex = 0;
                }
                LoadSegment();
            }
            finally
            {
                loading = false;
            }
            UpdateLabelsAndStates();

            (bool enabled, bool canChange) = await host.GetRunAtStartupAsync();
            loading = true;
            StartupToggle.IsOn = enabled;
            StartupToggle.IsEnabled = canChange;
            loading = false;
        }

        /// <summary>Widens the range rather than letting the slider silently clamp a saved value.</summary>
        private static void SetSlider(Slider slider, int value, int defaultMax)
        {
            slider.Minimum = Math.Min(0, value);
            slider.Maximum = Math.Max(defaultMax, value);
            slider.Value = value;
        }

        private void LoadSegment()
        {
            bool wasLoading = loading;
            loading = true;
            try
            {
                Types.SegmentSettings layout = SelectedSegmentLayout;
                SetSlider(SegRadiusSlider, layout.CornerRadius, DefaultRadiusMax);
                SetSlider(SegTopSlider, layout.MarginTop, DefaultMarginMax);
                SetSlider(SegBottomSlider, layout.MarginBottom, DefaultMarginMax);
                SetSlider(SegLeftSlider, layout.MarginLeft, DefaultMarginMax);
                SetSlider(SegRightSlider, layout.MarginRight, DefaultMarginMax);

                bool hasWidth = SegmentSegmented.SelectedIndex is 2 or 3;
                SegWidthPanel.Visibility = hasWidth ? Visibility.Visible : Visibility.Collapsed;
                if (hasWidth)
                {
                    int width = SegmentSegmented.SelectedIndex == 2 ? Settings.WidgetsWidth : Settings.ClockWidth;
                    SegWidthSlider.Minimum = Math.Min(40, width);
                    SegWidthSlider.Maximum = Math.Max(400, width);
                    SegWidthSlider.Value = width;
                }
            }
            finally
            {
                loading = wasLoading;
            }
        }

        private void UpdateLabelsAndStates()
        {
            RadiusValue.Text = $"{(int)RadiusSlider.Value} px";
            MarginAllValue.Text = $"{(int)MarginAllSlider.Value} px";
            SegRadiusValue.Text = $"{(int)SegRadiusSlider.Value} px";
            SegWidthValue.Text = $"{(int)SegWidthSlider.Value} px";

            bool linked = LinkMarginsToggle.IsOn;
            LinkedMarginPanel.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
            SplitMarginPanel.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;

            bool dynamic = Settings.IsDynamic;
            SegmentsSection.Visibility = dynamic ? Visibility.Visible : Visibility.Collapsed;
            SegmentLayoutSection.Visibility = dynamic ? Visibility.Visible : Visibility.Collapsed;
            HoverToggle.IsEnabled = dynamic;

            // Hover mode drives tray and widget visibility itself.
            ShowTrayToggle.IsEnabled = !Settings.ShowSegmentsOnHover;
            ShowWidgetsToggle.IsEnabled = !Settings.ShowSegmentsOnHover;
            FillTaskSwitchToggle.IsEnabled = Settings.FillOnMaximise && host.isWindows11;
        }

        private Types.SegmentSettings ReadMainShape()
        {
            bool linked = LinkMarginsToggle.IsOn;
            int all = (int)MarginAllSlider.Value;
            return new Types.SegmentSettings
            {
                CornerRadius = (int)RadiusSlider.Value,
                MarginTop = linked ? all : (int)MarginTopSlider.Value,
                MarginBottom = linked ? all : (int)MarginBottomSlider.Value,
                MarginLeft = linked ? all : (int)MarginLeftSlider.Value,
                MarginRight = linked ? all : (int)MarginRightSlider.Value,
            };
        }

        private void SetAllLayouts(Types.SegmentSettings shape)
        {
            Settings.SimpleTaskbarLayout = Copy(shape);
            Settings.DynamicAppListLayout = Copy(shape);
            Settings.DynamicTrayLayout = Copy(shape);
            Settings.DynamicWidgetsLayout = Copy(shape);
            Settings.DynamicSecondaryClockLayout = Copy(shape);
        }

        /// <summary>Keeps hand-tuned segment layouts when switching to dynamic.</summary>
        private void SetDynamicLayoutsIfUniform(Types.SegmentSettings shape)
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

        private static Types.SegmentSettings Copy(Types.SegmentSettings s) => new()
        {
            CornerRadius = s.CornerRadius,
            MarginTop = s.MarginTop,
            MarginBottom = s.MarginBottom,
            MarginLeft = s.MarginLeft,
            MarginRight = s.MarginRight,
        };

        private static bool Same(Types.SegmentSettings a, Types.SegmentSettings b) =>
            a == null || b == null
                ? a == b
                : a.CornerRadius == b.CornerRadius && a.MarginTop == b.MarginTop && a.MarginBottom == b.MarginBottom
                    && a.MarginLeft == b.MarginLeft && a.MarginRight == b.MarginRight;

        #endregion

        #region Event handlers

        private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loading || ModeSegmented.SelectedIndex < 0)
            {
                return;
            }
            bool dynamic = ModeSegmented.SelectedIndex == 1;
            if (dynamic == Settings.IsDynamic)
            {
                return;
            }

            // Carry the visible shape across so switching modes doesn't make it jump.
            Types.SegmentSettings current = ReadMainShape();
            Settings.IsDynamic = dynamic;
            if (dynamic)
            {
                if (!Settings.ShowSegmentsOnHover)
                {
                    Settings.ShowTray = true;
                }
                SetDynamicLayoutsIfUniform(current);
            }
            else
            {
                Settings.ShowSegmentsOnHover = false;
                Settings.SimpleTaskbarLayout = Copy(current);
            }
            LoadFromSettings();
            ScheduleApply();
        }

        private void MainShape_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (loading)
            {
                return;
            }
            SetAllLayouts(ReadMainShape());
            LoadSegment();
            UpdateLabelsAndStates();
            ScheduleApply();
        }

        private void LinkMargins_Toggled(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }
            loading = true;
            if (LinkMarginsToggle.IsOn)
            {
                SetSlider(MarginAllSlider, (int)MarginTopSlider.Value, DefaultMarginMax);
            }
            else
            {
                int all = (int)MarginAllSlider.Value;
                SetSlider(MarginTopSlider, all, DefaultMarginMax);
                SetSlider(MarginBottomSlider, all, DefaultMarginMax);
                SetSlider(MarginLeftSlider, all, DefaultMarginMax);
                SetSlider(MarginRightSlider, all, DefaultMarginMax);
            }
            loading = false;
            MainShape_ValueChanged(sender, null);
        }

        private void Segment_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loading)
            {
                return;
            }
            LoadSegment();
            UpdateLabelsAndStates();
        }

        private void SegmentShape_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (loading)
            {
                return;
            }
            Types.SegmentSettings layout = SelectedSegmentLayout;
            layout.CornerRadius = (int)SegRadiusSlider.Value;
            layout.MarginTop = (int)SegTopSlider.Value;
            layout.MarginBottom = (int)SegBottomSlider.Value;
            layout.MarginLeft = (int)SegLeftSlider.Value;
            layout.MarginRight = (int)SegRightSlider.Value;
            if (SegmentSegmented.SelectedIndex == 2)
            {
                Settings.WidgetsWidth = (int)SegWidthSlider.Value;
            }
            else if (SegmentSegmented.SelectedIndex == 3)
            {
                Settings.ClockWidth = (int)SegWidthSlider.Value;
            }
            UpdateLabelsAndStates();
            ScheduleApply();
        }

        private void Toggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }
            loading = true;
            if (ReferenceEquals(sender, HoverToggle))
            {
                ShowTrayToggle.IsOn = !HoverToggle.IsOn;
                ShowWidgetsToggle.IsOn = !HoverToggle.IsOn;
            }
            if (ReferenceEquals(sender, FillMaximisedToggle) && !FillMaximisedToggle.IsOn)
            {
                FillTaskSwitchToggle.IsOn = false;
            }
            loading = false;

            Types.Settings s = Settings;
            s.ShowTray = ShowTrayToggle.IsOn;
            s.ShowWidgets = ShowWidgetsToggle.IsOn;
            s.ShowSecondaryClock = ShowClockToggle.IsOn;
            s.FillOnMaximise = FillMaximisedToggle.IsOn;
            s.FillOnTaskSwitch = s.FillOnMaximise && FillTaskSwitchToggle.IsOn;
            s.ShowSegmentsOnHover = HoverToggle.IsOn;
            s.CompositionCompat = CompositionToggle.IsOn;
            s.AutoHide = AutoHideToggle.IsOn ? 1 : 0;
            UpdateLabelsAndStates();
            ScheduleApply();
        }

        private async void Startup_Toggled(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }
            await host.SetRunAtStartupAsync(StartupToggle.IsOn);
            (bool enabled, bool canChange) = await host.GetRunAtStartupAsync();
            loading = true;
            StartupToggle.IsOn = enabled;
            StartupToggle.IsEnabled = canChange;
            loading = false;
        }

        private void Help_Click(object sender, RoutedEventArgs e) => ShowAboutPage(AboutPage.Visibility != Visibility.Visible);

        private void ShowAboutPage(bool show)
        {
            if (show)
            {
                PopulateAbout();
                if (AboutTabs.SelectedIndex < 0)
                {
                    AboutTabs.SelectedIndex = 0;
                }
            }
            SettingsPage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            AboutPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            HelpButtonIcon.Glyph = show ? "" : "";
            HelpButtonText.Text = show ? "Back" : "Help & about";
        }

        private void AboutTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AboutTabPanel.Visibility = AboutTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            HelpTabPanel.Visibility = AboutTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            LinksTabPanel.Visibility = AboutTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PopulateAbout()
        {
            Version version = typeof(FlyoutWindow).Assembly.GetName().Version;
            InfoVersion.Text = version == null || version == new Version(1, 0, 0, 0) ? "Development build" : version.ToString(3);
            InfoMode.Text = Settings.IsDynamic ? (host.isWindows11 ? "Dynamic" : "Split") : "Basic";
            InfoArch.Text = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

            try
            {
                List<string> scales = host.taskbarDetails.Select(t => $"{Math.Round(t.ScaleFactor * 100)}%").ToList();
                InfoTaskbars.Text = $"{scales.Count} ({string.Join(", ", scales)} scale)";
            }
            catch (InvalidOperationException)
            {
                InfoTaskbars.Text = "Unknown";
            }

            using RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string display = key?.GetValue("DisplayVersion") as string;
            string build = key?.GetValue("CurrentBuild") as string;
            object ubr = key?.GetValue("UBR");
            string fullBuild = ubr != null ? $"{build}.{ubr}" : build;
            InfoWindows.Text = string.IsNullOrEmpty(display) ? $"Build {fullBuild}" : $"{display} (build {fullBuild})";
        }

        private void OpenConfig_Click(object sender, RoutedEventArgs e) => ShellOpen(host.configPath);

        private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) => ShellOpen(Path.GetDirectoryName(host.configPath));

        private void ShellOpen(string target)
        {
            HideFlyout();
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                host.interaction.AddLog($"Failed to open {target}: {ex.Message}");
            }
        }

        private void Quit_Click(object sender, RoutedEventArgs e) => host.Quit();

        #endregion
    }

    /// <summary>Desktop acrylic tinted towards the taskbar's material; DesktopAcrylicBackdrop has no tint.</summary>
    internal sealed partial class TintedAcrylicBackdrop : SystemBackdrop
    {
        private readonly Dictionary<ICompositionSupportsSystemBackdrop, (DesktopAcrylicController Controller, SystemBackdropConfiguration Config)> targets = new();
        private bool light;

        public void SetTheme(bool useLight)
        {
            light = useLight;
            foreach (var (controller, config) in targets.Values)
            {
                config.Theme = light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
                Configure(controller, light);
            }
        }

        private static void Configure(DesktopAcrylicController controller, bool light)
        {
            Color tint = light ? Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE) : Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1C);
            controller.Kind = DesktopAcrylicKind.Base;
            controller.TintColor = tint;
            controller.TintOpacity = 0.6f;
            controller.LuminosityOpacity = 0.85f;
            controller.FallbackColor = tint;
        }

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            DesktopAcrylicController controller = new();
            // The flyout is only visible while active, so never fall back to the inactive solid colour.
            SystemBackdropConfiguration config = new() { IsInputActive = true, Theme = light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark };
            Configure(controller, light);
            controller.SetSystemBackdropConfiguration(config);
            controller.AddSystemBackdropTarget(connectedTarget);
            targets[connectedTarget] = (controller, config);
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            base.OnTargetDisconnected(disconnectedTarget);
            if (targets.Remove(disconnectedTarget, out var entry))
            {
                entry.Controller.RemoveSystemBackdropTarget(disconnectedTarget);
                entry.Controller.Dispose();
            }
        }
    }

    internal sealed partial class TransparentBackdrop : SystemBackdrop
    {
        private Windows.UI.Composition.Compositor compositor;

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            // The window-level target is on the system compositor, not the app one.
            Native.EnsureSystemDispatcherQueue();
            compositor ??= new Windows.UI.Composition.Compositor();
            connectedTarget.SystemBackdrop = compositor.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            disconnectedTarget.SystemBackdrop = null;
            base.OnTargetDisconnected(disconnectedTarget);
        }
    }
}
