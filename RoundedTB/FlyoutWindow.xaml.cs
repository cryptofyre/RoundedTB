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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace RoundedTB
{
    /// <summary>
    /// The settings flyout. While open, this window keeps a fixed size (the height available beside
    /// the taskbar) and is clear apart from the card, so every height change and page transition is a
    /// composition animation inside it. The acrylic lives in <see cref="BackdropWindow"/> behind the
    /// card; while anything moves, the card is filled with a near-opaque tint instead and the
    /// backdrop catches up once it settles.
    /// </summary>
    public sealed partial class FlyoutWindow : Window
    {
        private const float CardWidth = 360;
        private const float CornerRadius = 8;
        private const float ShapeMargin = 4000;
        private const int DefaultRadiusMax = 32, DefaultMarginMax = 20;
        private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(220);

        private enum Edge { Bottom, Top, Left, Right }

        private readonly AppHost host;
        private readonly IntPtr hwnd;
        private readonly Compositor compositor;
        private readonly Visual cardVisual;
        private readonly InsetClip bodyClip;
        private readonly CompositionRoundedRectangleGeometry cardGeometry;
        private readonly CompositionSpriteShape cardShape;
        private readonly BackdropWindow backdrop;
        private readonly DispatcherQueueTimer applyTimer;

        // Sliders coerce and fire ValueChanged while the XAML loads, before the fields are set.
        private bool loading = true;
        private bool isOpen;
        private bool closing;
        private bool sliding;
        private bool light;
        private int motions;
        private readonly DispatcherQueueTimer settleTimer;
        private DateTime lastHidden = DateTime.MinValue;
        private Edge edge = Edge.Bottom;
        private IntPtr targetMonitor;
        private RectInt32 windowBounds;
        private double scale = 1;

        private Types.Settings Settings => host.activeSettings;

        public FlyoutWindow(AppHost host)
        {
            this.host = host;
            InitializeComponent();
            hwnd = PopupChrome.Apply(this, clickThrough: false);
            // A tall clear window: no rounding, and no DWM shadow around its whole rect.
            PopupChrome.SetAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
            PopupChrome.SetAttribute(hwnd, Native.DWMWA_NCRENDERING_POLICY, Native.DWMNCRP_DISABLED);
            SystemBackdrop = new TransparentBackdrop();

            compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
            ElementCompositionPreview.GetElementVisual(Root).Clip = compositor.CreateInsetClip();
            foreach (UIElement element in new UIElement[] { Card, Body, Footer, SettingsPage, AboutPage })
            {
                ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            }
            cardVisual = ElementCompositionPreview.GetElementVisual(Card);
            bodyClip = compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(Body).Clip = bodyClip;

            // The card's fill and outline. Drawn in a visual much taller than the card so the shape can
            // extend past the card's layout bounds while a height change animates.
            cardGeometry = compositor.CreateRoundedRectangleGeometry();
            cardGeometry.CornerRadius = new Vector2(CornerRadius, CornerRadius);
            cardShape = compositor.CreateSpriteShape(cardGeometry);
            cardShape.StrokeThickness = 1;
            ShapeVisual shapes = compositor.CreateShapeVisual();
            shapes.Shapes.Add(cardShape);
            shapes.Offset = new Vector3(0, -ShapeMargin, 0);
            shapes.Size = new Vector2(CardWidth, 2 * ShapeMargin);
            ElementCompositionPreview.SetElementChildVisual(ShapeHost, shapes);
            Card.HeightChanging += Card_HeightChanging;

            if (DesktopAcrylicController.IsSupported())
            {
                backdrop = new BackdropWindow();
            }
            ApplyTheme();

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

            settleTimer = DispatcherQueue.CreateTimer();
            settleTimer.Interval = TimeSpan.FromMilliseconds(150);
            settleTimer.IsRepeating = false;
            settleTimer.Tick += (_, _) => SetFill();

            loading = false;
        }

        #region Theme and card shape

        // RTB_THEME=light|dark overrides the shell theme, for testing.
        private static bool SystemUsesLightTheme() =>
            Environment.GetEnvironmentVariable("RTB_THEME") is "light" or "dark"
                ? Environment.GetEnvironmentVariable("RTB_THEME") == "light"
                : Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1;

        /// <summary>Follows the shell theme (SystemUsesLightTheme), like the taskbar, not the app theme.</summary>
        private void ApplyTheme()
        {
            light = SystemUsesLightTheme();
            Root.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
            backdrop?.SetTheme(light);
            cardShape.StrokeBrush = compositor.CreateColorBrush(light ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            SetFill();
        }

        /// <summary>
        /// A strong tint over the acrylic, like the shell flyouts, made near-opaque while the card moves:
        /// the acrylic window only catches up afterwards (and draws nothing for a moment while it
        /// resizes). The fill changes in the same composition frame as the motion, so it is never late.
        /// </summary>
        private void SetFill()
        {
            byte alpha = backdrop == null ? (byte)0xFF : motions > 0 ? (byte)0xF7 : (byte)0xD9;
            cardShape.FillBrush = compositor.CreateColorBrush(Tint(alpha));
        }

        private void BeginMotion()
        {
            if (motions++ == 0)
            {
                settleTimer.Stop();
                SetFill();
            }
        }

        private void EndMotion()
        {
            if (--motions > 0)
            {
                return;
            }
            RevealBackdrop(Card.ActualHeight);
            settleTimer.Start();
        }

        private Color Tint(byte alpha)
        {
            byte shade = light ? (byte)0xEE : (byte)0x1C;
            return Color.FromArgb(alpha, shade, shade, shade);
        }

        /// <summary>Sets the card shape relative to the card's own layout box.</summary>
        private void SetShape(float top, float height)
        {
            cardGeometry.Offset = new Vector2(0.5f, ShapeMargin + top + 0.5f);
            cardGeometry.Size = new Vector2(CardWidth - 1, Math.Max(0, height - 1));
        }

        /// <summary>Puts the acrylic behind a card of <paramref name="height"/> (DIPs), anchored to the taskbar side.</summary>
        private void RevealBackdrop(double height)
        {
            if (backdrop == null || !isOpen || closing)
            {
                return;
            }
            int h = (int)Math.Round(height * scale);
            int y = edge == Edge.Top ? windowBounds.Y : windowBounds.Y + windowBounds.Height - h;
            backdrop.Reveal(new RectInt32(windowBounds.X, y, windowBounds.Width, h), hwnd);
        }

        private void AfterFrames(int frames, Action action)
        {
            void OnRendering(object sender, object e)
            {
                if (--frames > 0)
                {
                    return;
                }
                CompositionTarget.Rendering -= OnRendering;
                action();
            }
            CompositionTarget.Rendering += OnRendering;
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
            // The XAML tree can't be laid out until the window has been shown once, so the first
            // open shows it cloaked and carries on when it has loaded.
            if (!Root.IsLoaded)
            {
                PopupChrome.SetCloaked(hwnd, true);
                Root.Loaded += OnFirstLoad;
                AppWindow.Show(false);
                void OnFirstLoad(object sender, RoutedEventArgs e)
                {
                    Root.Loaded -= OnFirstLoad;
                    AppWindow.Hide();
                    PopupChrome.SetCloaked(hwnd, false);
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
            PlaceWindow();

            // Park the card fully outside the window until the first frame, when its size is known.
            float park = (float)(windowBounds.Height / scale + windowBounds.Width / scale);
            cardVisual.StopAnimation("Translation");
            cardVisual.Properties.InsertVector3("Translation", HiddenOffset(park));
            isOpen = true;
            closing = false;
            sliding = true;
            BeginMotion();
            AppWindow.Show(true);
            Activate();
            Native.SetForegroundWindow(hwnd);

            AfterFrames(1, () =>
            {
                SetShape(0, (float)Card.ActualHeight);
                Slide(opening: true, () =>
                {
                    sliding = false;
                    EndMotion();
                });
            });
        }

        public void HideFlyout()
        {
            if (!isOpen || closing)
            {
                return;
            }
            closing = true;
            sliding = true;
            FlushPendingChanges();
            BeginMotion();
            backdrop?.Conceal();
            Slide(opening: false, () =>
            {
                AppWindow.Hide();
                backdrop?.HideWindow();
                sliding = false;
                motions = 0;
                settleTimer.Stop();
                SetFill();
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

        /// <summary>The window is larger than the card; clicks on the clear part dismiss it.</summary>
        private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, Root))
            {
                HideFlyout();
            }
        }

        private Vector3 HiddenOffset(float distance) => edge switch
        {
            Edge.Top => new Vector3(0, -distance, 0),
            Edge.Left => new Vector3(-distance, 0, 0),
            Edge.Right => new Vector3(distance, 0, 0),
            _ => new Vector3(0, distance, 0),
        };

        private void Slide(bool opening, Action completed)
        {
            float distance = edge is Edge.Left or Edge.Right ? CardWidth + 2 : (float)Card.ActualHeight + 2;
            CubicBezierEasingFunction easing = opening
                ? compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f))
                : compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0f), new Vector2(1f, 0.5f));
            Vector3KeyFrameAnimation animation = compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(0f, opening ? HiddenOffset(distance) : Vector3.Zero);
            animation.InsertKeyFrame(1f, opening ? Vector3.Zero : HiddenOffset(distance), easing);
            animation.Duration = opening ? OpenDuration : CloseDuration;
            Animate(cardVisual, "Translation", animation, completed);
        }

        private void Animate(CompositionObject target, string property, CompositionAnimation animation, Action completed)
        {
            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            target.StartAnimation(property, animation);
            batch.End();
            batch.Completed += (_, _) => completed();
        }

        /// <summary>
        /// Raised from the card's arrange pass, so animations started here begin in the same frame as the new size:
        /// the card shape morphs from the old box to the new one, and the content on the taskbar's far
        /// side is pushed back to where it was and eased into place.
        /// </summary>
        private void Card_HeightChanging(float from, float to)
        {
            if (!isOpen || sliding || from <= 0)
            {
                SetShape(0, to);
                return;
            }

            float delta = to - from;
            bool anchoredBottom = edge != Edge.Top;
            Visual moving = ElementCompositionPreview.GetElementVisual(anchoredBottom ? Body : Footer);
            moving.Properties.InsertVector3("Translation", new Vector3(0, anchoredBottom ? delta : -delta, 0));
            SetShape(anchoredBottom ? delta : 0, from);
            // The acrylic never sticks out of the card: when shrinking it goes to the final size now,
            // when growing it stays inside the card and catches up at the end.
            if (delta < 0)
            {
                RevealBackdrop(to);
            }
            BeginMotion();

            CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
            Vector3KeyFrameAnimation settle = compositor.CreateVector3KeyFrameAnimation();
            settle.InsertKeyFrame(1f, Vector3.Zero, easing);
            settle.Duration = MorphDuration;
            Vector2KeyFrameAnimation offset = compositor.CreateVector2KeyFrameAnimation();
            offset.InsertKeyFrame(1f, new Vector2(0.5f, ShapeMargin + 0.5f), easing);
            offset.Duration = MorphDuration;
            Vector2KeyFrameAnimation size = compositor.CreateVector2KeyFrameAnimation();
            size.InsertKeyFrame(1f, new Vector2(CardWidth - 1, to - 1), easing);
            size.Duration = MorphDuration;

            cardGeometry.StartAnimation("Offset", offset);
            cardGeometry.StartAnimation("Size", size);
            if (delta > 0)
            {
                // Content pushed back over the footer would show through it.
                bodyClip.BottomInset = delta;
                ScalarKeyFrameAnimation unclip = compositor.CreateScalarKeyFrameAnimation();
                unclip.InsertKeyFrame(1f, 0f, easing);
                unclip.Duration = MorphDuration;
                bodyClip.StartAnimation("BottomInset", unclip);
            }
            Animate(moving, "Translation", settle, EndMotion);
        }

        /// <summary>
        /// Gives the window the full height available beside the taskbar on the target monitor, 12px
        /// from the screen edge and the taskbar, and anchors the card to the taskbar side.
        /// </summary>
        private void PlaceWindow()
        {
            Native.GetMonitor(targetMonitor, out Native.RECT mon, out Native.RECT work, out scale);
            Native.RECT tb = Native.FindTaskbarRect(targetMonitor, mon, work);

            int gap = (int)Math.Round(12 * scale);
            int width = (int)Math.Ceiling(CardWidth * scale);
            bool horizontal = tb.Right - tb.Left >= tb.Bottom - tb.Top;
            int x = mon.Right - gap - width;
            int top = mon.Top + gap;
            int bottom = mon.Bottom - gap;
            edge = Edge.Bottom;
            if (horizontal && tb.Top >= (mon.Top + mon.Bottom) / 2)
            {
                bottom = tb.Top - gap;
            }
            else if (horizontal)
            {
                edge = Edge.Top;
                top = tb.Bottom + gap;
            }
            else if (tb.Left >= (mon.Left + mon.Right) / 2)
            {
                edge = Edge.Right;
                x = tb.Left - gap - width;
            }
            else
            {
                edge = Edge.Left;
                x = tb.Right + gap;
            }

            windowBounds = new RectInt32(x, top, width, bottom - top);
            AppWindow.MoveAndResize(windowBounds);
            Card.VerticalAlignment = edge == Edge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            Footer.Measure(new Windows.Foundation.Size(CardWidth, double.PositiveInfinity));
            Body.MaxHeight = Math.Max(200, windowBounds.Height / scale - Footer.DesiredSize.Height);
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
                RoundHighlightsToggle.IsOn = s.RoundButtonHighlights;
                WatermarkToggle.IsOn = s.RemoveInsiderWatermark;

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
            s.RoundButtonHighlights = RoundHighlightsToggle.IsOn;
            UpdateLabelsAndStates();
            ScheduleApply();
        }

        // Doesn't touch the taskbar, so it skips the redraw path: save it and patch Explorer in the background.
        private void Watermark_Toggled(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }
            Settings.RemoveInsiderWatermark = WatermarkToggle.IsOn;
            host.interaction.WriteJSON();
            SetWatermarkStatus(WatermarkToggle.IsOn ? "Removing the watermark…" : "Restoring the watermark…");
            host.SyncWatermarkState();
        }

        public void SetWatermarkStatus(string message)
        {
            WatermarkStatus.Text = message;
            WatermarkStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
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

        private void Advanced_Click(object sender, RoutedEventArgs e)
        {
            bool expand = AdvancedPanel.Visibility != Visibility.Visible;
            AdvancedPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;

            Visual chevron = ElementCompositionPreview.GetElementVisual(AdvancedChevron);
            chevron.CenterPoint = new Vector3((float)AdvancedChevron.ActualWidth / 2, (float)AdvancedChevron.ActualHeight / 2, 0);
            ScalarKeyFrameAnimation rotate = compositor.CreateScalarKeyFrameAnimation();
            rotate.InsertKeyFrame(1f, expand ? 180f : 0f);
            rotate.Duration = TimeSpan.FromMilliseconds(200);
            chevron.StartAnimation("RotationAngleInDegrees", rotate);

            if (expand)
            {
                FadeIn(AdvancedPanel);
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => AdvancedPanel.StartBringIntoView());
            }
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
            if (isOpen)
            {
                EnterPage(show ? AboutPage : SettingsPage, forward: show);
            }
            HelpButtonIcon.Glyph = show ? "\uE72B" : "\uE946";
            HelpButtonText.Text = show ? "Back" : "Help & about";
        }

        /// <summary>Slides a page in from the right (forward) or left (back) while it fades in.</summary>
        private void EnterPage(UIElement page, bool forward)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(page);
            CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
            Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, new Vector3(forward ? 60 : -60, 0, 0));
            slide.InsertKeyFrame(1f, Vector3.Zero, easing);
            slide.Duration = TimeSpan.FromMilliseconds(260);
            visual.StartAnimation("Translation", slide);
            FadeIn(page);
        }

        private void FadeIn(UIElement element)
        {
            if (!isOpen)
            {
                return;
            }
            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(150);
            ElementCompositionPreview.GetElementVisual(element).StartAnimation("Opacity", fade);
        }

        private void AboutTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AboutTabPanel.Visibility = AboutTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            HelpTabPanel.Visibility = AboutTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            LinksTabPanel.Visibility = AboutTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            FadeIn(AboutTabs.SelectedIndex switch { 1 => HelpTabPanel, 2 => LinksTabPanel, _ => AboutTabPanel });
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
            controller.TintOpacity = 0.3f;
            controller.LuminosityOpacity = 0.6f;
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
