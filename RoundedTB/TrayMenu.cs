using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Win32;
using Windows.Graphics;

namespace RoundedTB
{
    /// <summary>
    /// The tray icon's context menu: a WinUI MenuFlyout, hosted by a one-pixel window at the cursor
    /// because a flyout needs a window to belong to and to take focus for light dismiss.
    /// </summary>
    internal sealed class TrayMenu : Window
    {
        private readonly AppHost host;
        private readonly IntPtr hwnd;
        private readonly Grid root = new();
        private readonly MenuFlyout menu = new() { ShouldConstrainToRootBounds = false };
        private readonly MenuFlyoutItem trayItem;

        public TrayMenu(AppHost host)
        {
            this.host = host;
            Content = root;
            hwnd = PopupChrome.Apply(this);
            SystemBackdrop = new TransparentBackdrop();

            menu.Items.Add(Item("Settings", "\uE713", () => host.ShowSettings()));
            menu.Items.Add(Item("Help & about", "\uE946", () => host.ShowSettings(helpPage: true)));
            menu.Items.Add(new MenuFlyoutSeparator());
            trayItem = Item("Hide system tray", "\uE8A9", host.ToggleTray);
            trayItem.KeyboardAcceleratorTextOverride = "Win+F2";
            menu.Items.Add(trayItem);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Quit RoundedTB", "\uE7E8", host.Quit));
            menu.Closed += (_, _) => AppWindow.Hide();
        }

        private static MenuFlyoutItem Item(string text, string glyph, Action action)
        {
            // Compact, like the shell's own context menus.
            MenuFlyoutItem item = new()
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph, FontSize = 14 },
                FontSize = 13,
                Padding = new Thickness(10, 5, 12, 6),
            };
            item.Click += (_, _) => action();
            return item;
        }

        /// <summary>Opens the menu at a point in physical screen pixels.</summary>
        public void ShowAt(int x, int y)
        {
            bool light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1;
            root.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
            trayItem.Text = host.activeSettings.ShowTray ? "Hide system tray" : "Show system tray";
            trayItem.IsEnabled = host.activeSettings.IsDynamic && !host.activeSettings.ShowSegmentsOnHover;

            AppWindow.MoveAndResize(new RectInt32(x, y, 1, 1));
            AppWindow.Show(true);
            Activate();
            Native.SetForegroundWindow(hwnd);

            if (root.IsLoaded)
            {
                Open();
            }
            else
            {
                root.Loaded += OnLoaded;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            root.Loaded -= OnLoaded;
            Open();
        }

        private void Open() => menu.ShowAt(root, new FlyoutShowOptions { Position = new Windows.Foundation.Point(0, 0) });
    }
}
