using System;
using System.Diagnostics;
using System.Numerics;
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
#if RTB_EXTERNAL_BACKDROP
using Microsoft.UI.Content;
#endif

namespace RoundedTB.WinUI
{
    public sealed partial class FlyoutWindow : Window
    {
        private const double PanelWidth = 360;
        private const int DefaultRadiusMax = 32, DefaultMarginMax = 20;
        private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(150);

        private enum RevealEdge { Bottom, Top, Left, Right }

        private readonly IntPtr hwnd;
        private readonly Compositor compositor;
        private readonly Visual panelVisual;
        private readonly TintedAcrylicBackdrop? acrylic;
        private readonly TransparentBackdrop clear = new();
#if RTB_EXTERNAL_BACKDROP
        private readonly ContentExternalBackdropLink? backdropLink;
        private readonly DesktopAcrylicController? linkController;
        private readonly SystemBackdropConfiguration linkConfig = new() { IsInputActive = true };
#endif
        private readonly DispatcherQueueTimer saveTimer;

        private Types.Settings settings;
        private bool loading = true; // until the constructor is done: sliders coerce and fire ValueChanged while loading XAML
        private bool isOpen;
        private bool closing;
        private bool sliding;
        private DateTime lastHidden = DateTime.MinValue;
        private RevealEdge revealEdge = RevealEdge.Bottom;
        private Vector3 hiddenTranslation;
        private Stopwatch? openClock;
        private IntPtr targetMonitor;
        private readonly Native.SubclassProc noFrameProc; // kept alive for the native side

        public FlyoutWindow()
        {
            InitializeComponent();
            hwnd = WindowNative.GetWindowHandle(this);

            // Chrome: no border or caption, not resizable, topmost, and a tool window so it has no
            // taskbar button or Alt+Tab entry.
            OverlappedPresenter presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            AppWindow.SetPresenter(presenter);
            AppWindow.IsShownInSwitchers = false;
            // The presenter puts WS_DLGFRAME back whenever the window is shown, which gives it a 3px
            // non-client frame that DWM paints white. Claim the whole window as client area instead.
            noFrameProc = NoFrameSubclassProc;
            Native.SetWindowSubclass(hwnd, noFrameProc, 1, IntPtr.Zero);
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);
            // Without the frame extended into the client area, the clear parts of a XAML window are black.
            Native.DwmExtendFrameIntoClientArea(hwnd, new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 });
            ApplyDwmAttributes();

            compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
            // Static mask at the window edge: the panel slides in from behind it.
            ElementCompositionPreview.GetElementVisual(Root).Clip = compositor.CreateInsetClip();
            ElementCompositionPreview.SetIsTranslationEnabled(Panel, true);
            panelVisual = ElementCompositionPreview.GetElementVisual(Panel);

            if (DesktopAcrylicController.IsSupported())
            {
#if RTB_EXTERNAL_BACKDROP
                // The acrylic is placed inside the panel, so it slides and clips with it.
                backdropLink = ContentExternalBackdropLink.Create(compositor);
                ElementCompositionPreview.SetElementChildVisual(BackdropHost, backdropLink.PlacementVisual);
                BackdropHost.SizeChanged += (_, e) => backdropLink.PlacementVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
                linkController = new DesktopAcrylicController();
                linkController.SetSystemBackdropConfiguration(linkConfig);
                linkController.AddSystemBackdropTarget(backdropLink);
                SystemBackdrop = clear;
#else
                acrylic = new TintedAcrylicBackdrop();
                SystemBackdrop = acrylic;
#endif
            }
            ApplyTheme();

            // The window is sized to the panel; follow it when Advanced (or a mode switch) changes its height.
            ContentStack.SizeChanged += (_, _) =>
            {
                if (isOpen && !sliding)
                {
                    Place(targetMonitor);
                }
            };

            Activated += OnActivated;
            AppWindow.Closing += (_, e) =>
            {
                // Alt+F4 hides rather than destroys.
                e.Cancel = true;
                HideFlyout();
            };
            Root.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    HideFlyout();
                }
            };

            saveTimer = DispatcherQueue.CreateTimer();
            saveTimer.Interval = TimeSpan.FromMilliseconds(250);
            saveTimer.IsRepeating = false;
            saveTimer.Tick += (_, _) => Save();

            settings = SettingsStore.Read();
            loading = false;
        }

        public bool IsOpen => isOpen;

        private static IntPtr NoFrameSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint id, IntPtr refData)
        {
            if (msg == Native.WM_NCCALCSIZE && wParam != IntPtr.Zero)
            {
                return IntPtr.Zero; // client rect = window rect
            }
            return Native.DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        #region Theme and backdrop

        // RTB_THEME=light|dark overrides the shell theme, for checking both without changing it.
        private static bool SystemUsesLightTheme() =>
            Environment.GetEnvironmentVariable("RTB_THEME") is "light" or "dark"
                ? Environment.GetEnvironmentVariable("RTB_THEME") == "light"
                : Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1;

        private void ApplyDwmAttributes()
        {
            int corner = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int border = Native.DWMWA_COLOR_NONE;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }

        /// <summary>
        /// Follows the shell theme (SystemUsesLightTheme) like the taskbar does, rather than the
        /// app theme, and tints the acrylic towards the taskbar's material.
        /// </summary>
        private void ApplyTheme()
        {
            bool light = SystemUsesLightTheme();
            Root.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
            acrylic?.SetTheme(light);
#if RTB_EXTERNAL_BACKDROP
            if (linkController != null)
            {
                linkConfig.Theme = light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
                TintedAcrylicBackdrop.Configure(linkController, light);
            }
#endif
            UpdatePanelBackground();
        }

        private bool UsesSlidingSwap => acrylic != null;

        /// <summary>
        /// The panel's own fill. With window-level acrylic it is clear at rest; while sliding, the
        /// acrylic is switched off (it covers the whole window at once rather than moving with the
        /// panel) and a near-opaque tint travels with the content instead. Without acrylic at all
        /// the panel is solid.
        /// </summary>
        private void UpdatePanelBackground()
        {
            bool light = Root.RequestedTheme == ElementTheme.Light;
            byte shade = light ? (byte)0xEE : (byte)0x1C;
            if (!DesktopAcrylicController.IsSupported())
            {
                Panel.Background = new SolidColorBrush(Color.FromArgb(0xFF, shade, shade, shade));
            }
            else if (UsesSlidingSwap && sliding)
            {
                Panel.Background = new SolidColorBrush(Color.FromArgb(0xF7, shade, shade, shade));
            }
            else
            {
                Panel.Background = null;
            }
        }

        private void SetSliding(bool value)
        {
            sliding = value;
            if (UsesSlidingSwap)
            {
                SystemBackdrop = value ? clear : acrylic;
            }
            UpdatePanelBackground();
        }

        #endregion

        #region Open / close

        /// <summary>Toggles the flyout, the way a tray click should.</summary>
        public void Toggle(Stopwatch clickClock)
        {
            if (isOpen && !closing)
            {
                HideFlyout();
                return;
            }
            // A click on the tray icon deactivates (and so hides) the flyout just before the click
            // itself arrives; don't reopen it straight away.
            if ((DateTime.UtcNow - lastHidden).TotalMilliseconds < 300)
            {
                return;
            }
            ShowFlyout(clickClock);
        }

        public void ShowFlyout(Stopwatch clickClock)
        {
            openClock = clickClock;
            settings = SettingsStore.Read();
            LoadFromSettings();
            ApplyTheme();
            Native.GetCursorPos(out Native.POINT cursor);
            targetMonitor = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
            Place(targetMonitor);

            panelVisual.StopAnimation("Translation");
            panelVisual.Properties.InsertVector3("Translation", hiddenTranslation);
            SetSliding(true);
            isOpen = true;
            closing = false;
            AppWindow.Show(true);
            ApplyDwmAttributes();
            Activate();
            Native.SetForegroundWindow(hwnd);

            // Start the slide on the first frame XAML renders, so a cold first open doesn't
            // spend half the animation before there is anything on screen.
            void OnFirstFrame(object? sender, object e)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFirstFrame;
                Timing.Mark(openClock, "first frame");
                Slide(opening: true, () =>
                {
                    Timing.Mark(openClock, "slide in done");
                    if (isOpen && !closing)
                    {
                        SetSliding(false);
                        Place(targetMonitor);
                    }
                });
            }
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFirstFrame;
        }

        public void HideFlyout()
        {
            if (!isOpen || closing)
            {
                return;
            }
            closing = true;
            if (saveTimer.IsRunning)
            {
                saveTimer.Stop();
                Save();
            }
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

        /// <summary>
        /// Slides the panel in from (or out to) the taskbar edge: a Translation animation on the
        /// panel, masked by the inset clip on the window root. It runs on the compositor, so a busy
        /// UI thread can't stall it.
        /// </summary>
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

        /// <summary>
        /// Sizes the window to the panel's content (capped at what fits beside the taskbar) and
        /// places it 12px (scaled) from the screen edge and away from that monitor's taskbar, all
        /// in that monitor's physical pixels.
        /// </summary>
        private void Place(IntPtr monitor)
        {
            Native.GetMonitor(monitor, out Native.RECT mon, out Native.RECT work, out double scale);
            Native.RECT tb = Native.FindTaskbarRect(monitor, mon, work);

            int gap = (int)Math.Round(12 * scale);
            int tbWidth = tb.Right - tb.Left, tbHeight = tb.Bottom - tb.Top;
            bool horizontal = tbWidth >= tbHeight;
            int available = mon.Bottom - mon.Top - 2 * gap - (horizontal ? tbHeight : 0);

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

            // Translation is in DIPs. Travel the panel's full size so it starts fully behind the edge.
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

        private Types.SegmentSettings MainLayout => settings.IsDynamic ? settings.DynamicAppListLayout : settings.SimpleTaskbarLayout;

        private Types.SegmentSettings SelectedSegmentLayout => SegmentSegmented.SelectedIndex switch
        {
            1 => settings.DynamicTrayLayout,
            2 => settings.DynamicWidgetsLayout,
            3 => settings.DynamicSecondaryClockLayout,
            _ => settings.DynamicAppListLayout,
        };

        private void LoadFromSettings()
        {
            loading = true;
            try
            {
                Types.Settings s = settings;
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
        }

        private static void SetSlider(Slider slider, int value, int defaultMax)
        {
            // Configs written by the old text boxes can hold anything; widen the range rather than
            // let the slider's coercion silently change a saved value.
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
                    int width = SegmentSegmented.SelectedIndex == 2 ? settings.WidgetsWidth : settings.ClockWidth;
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

            bool dynamic = settings.IsDynamic;
            SegmentsSection.Visibility = dynamic ? Visibility.Visible : Visibility.Collapsed;
            SegmentLayoutSection.Visibility = dynamic ? Visibility.Visible : Visibility.Collapsed;
            HoverToggle.IsEnabled = dynamic;

            // Hover mode drives tray/widgets visibility itself.
            ShowTrayToggle.IsEnabled = !settings.ShowSegmentsOnHover;
            ShowWidgetsToggle.IsEnabled = !settings.ShowSegmentsOnHover;
            FillTaskSwitchToggle.IsEnabled = settings.FillOnMaximise;
        }

        private Types.SegmentSettings ReadMainShape()
        {
            int radius = (int)RadiusSlider.Value;
            if (LinkMarginsToggle.IsOn)
            {
                int m = (int)MarginAllSlider.Value;
                return new Types.SegmentSettings { CornerRadius = radius, MarginTop = m, MarginBottom = m, MarginLeft = m, MarginRight = m };
            }
            return new Types.SegmentSettings
            {
                CornerRadius = radius,
                MarginTop = (int)MarginTopSlider.Value,
                MarginBottom = (int)MarginBottomSlider.Value,
                MarginLeft = (int)MarginLeftSlider.Value,
                MarginRight = (int)MarginRightSlider.Value,
            };
        }

        private static Types.SegmentSettings Copy(Types.SegmentSettings s) => new()
        {
            CornerRadius = s.CornerRadius,
            MarginTop = s.MarginTop,
            MarginBottom = s.MarginBottom,
            MarginLeft = s.MarginLeft,
            MarginRight = s.MarginRight,
        };

        /// <summary>The main Shape controls write the same shape to all five layouts.</summary>
        private void SetAllLayouts(Types.SegmentSettings shape)
        {
            settings.SimpleTaskbarLayout = Copy(shape);
            settings.DynamicAppListLayout = Copy(shape);
            settings.DynamicTrayLayout = Copy(shape);
            settings.DynamicWidgetsLayout = Copy(shape);
            settings.DynamicSecondaryClockLayout = Copy(shape);
        }

        private void ScheduleSave()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        private void Save()
        {
            try
            {
                SettingsStore.Write(settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Saving settings failed: {ex.Message}");
            }
        }

        #endregion

        #region Event handlers

        private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loading || ModeSegmented.SelectedIndex < 0)
            {
                return;
            }
            bool dynamic = ModeSegmented.SelectedIndex == 1;
            if (dynamic == settings.IsDynamic)
            {
                return;
            }
            settings.IsDynamic = dynamic;
            if (dynamic && !settings.ShowSegmentsOnHover)
            {
                settings.ShowTray = true;
            }
            else if (!dynamic)
            {
                settings.ShowSegmentsOnHover = false;
            }
            LoadFromSettings();
            ScheduleSave();
        }

        private void MainShape_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (loading)
            {
                return;
            }
            if (ReferenceEquals(sender, MarginAllSlider))
            {
                // Keep the split sliders in step so unlinking starts from the same values.
                loading = true;
                MarginTopSlider.Value = MarginBottomSlider.Value = MarginLeftSlider.Value = MarginRightSlider.Value = MarginAllSlider.Value;
                loading = false;
            }
            SetAllLayouts(ReadMainShape());
            LoadSegment();
            UpdateLabelsAndStates();
            ScheduleSave();
        }

        private void LinkMargins_Toggled(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }
            if (LinkMarginsToggle.IsOn)
            {
                loading = true;
                MarginAllSlider.Value = MarginTopSlider.Value;
                loading = false;
                SetAllLayouts(ReadMainShape());
                ScheduleSave();
            }
            UpdateLabelsAndStates();
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
                settings.WidgetsWidth = (int)SegWidthSlider.Value;
            }
            else if (SegmentSegmented.SelectedIndex == 3)
            {
                settings.ClockWidth = (int)SegWidthSlider.Value;
            }
            UpdateLabelsAndStates();
            ScheduleSave();
        }

        private void Toggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }
            settings.ShowTray = ShowTrayToggle.IsOn;
            settings.ShowWidgets = ShowWidgetsToggle.IsOn;
            settings.ShowSecondaryClock = ShowClockToggle.IsOn;
            settings.FillOnMaximise = FillMaximisedToggle.IsOn;
            settings.FillOnTaskSwitch = FillTaskSwitchToggle.IsOn;
            settings.ShowSegmentsOnHover = HoverToggle.IsOn;
            settings.CompositionCompat = CompositionToggle.IsOn;
            settings.AutoHide = AutoHideToggle.IsOn ? 1 : 0;
            UpdateLabelsAndStates();
            ScheduleSave();
        }

        private void OpenConfig_Click(object sender, RoutedEventArgs e)
        {
            HideFlyout();
            try
            {
                Process.Start(new ProcessStartInfo(SettingsStore.ConfigPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not open settings file: {ex.Message}");
            }
        }

        private void Quit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();

        #endregion
    }

    /// <summary>A clear window background, so only the panel (and its acrylic) is visible.</summary>
    internal sealed partial class TransparentBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop
    {
        private Windows.UI.Composition.Compositor? compositor;

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            // The window-level target lives on the system compositor, not the app one.
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

namespace RoundedTB.WinUI
{
    /// <summary>
    /// Desktop acrylic with a tint close to the taskbar's material. The built-in
    /// DesktopAcrylicBackdrop doesn't expose the tint, so this wraps the controller directly.
    /// </summary>
    internal sealed partial class TintedAcrylicBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop
    {
        private readonly System.Collections.Generic.Dictionary<ICompositionSupportsSystemBackdrop, (DesktopAcrylicController Controller, SystemBackdropConfiguration Config)> targets = new();
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

        public static void Configure(DesktopAcrylicController controller, bool light)
        {
            controller.Kind = DesktopAcrylicKind.Base;
            Color tint = light ? Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE) : Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1C);
            controller.TintColor = tint;
            controller.TintOpacity = 0.6f;
            controller.LuminosityOpacity = 0.85f;
            controller.FallbackColor = tint;
        }

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            DesktopAcrylicController controller = new();
            // A flyout is only visible while active; never fall back to the inactive solid colour.
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
}
