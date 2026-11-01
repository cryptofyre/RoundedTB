using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Xaml;

namespace RoundedTB.WinUI
{
    public partial class App : Application
    {
        private Mutex? singleInstance;
        private TrayIcon? tray;
        private FlyoutWindow? flyout;

        public App()
        {
            InitializeComponent();
            // The app lives in the tray; hiding the flyout must not end it.
            DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
            UnhandledException += (_, e) => System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RoundedTB.WinUI.crash.log"), $"{DateTime.Now:O} {e.Exception}{Environment.NewLine}");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            singleInstance = new Mutex(true, "RoundedTB.WinUI.Spike", out bool created);
            if (!created)
            {
                Exit();
                return;
            }

            // RTB_LAZY=1 defers creating the flyout window to the first click, to compare idle
            // memory against a slower first open.
            if (Environment.GetEnvironmentVariable("RTB_LAZY") != "1")
            {
                flyout = new FlyoutWindow();
            }

            tray = new TrayIcon("RoundedTB (WinUI spike)");
            tray.Clicked += () =>
            {
                Stopwatch clock = Stopwatch.StartNew();
                if (flyout == null)
                {
                    // First open in lazy mode: let the new window finish setting up before showing it.
                    FlyoutWindow created = flyout = new FlyoutWindow();
                    created.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => created.Toggle(clock));
                    return;
                }
                flyout.Toggle(clock);
            };
            tray.QuitRequested += Quit;
        }

        public void Quit()
        {
            tray?.Dispose();
            tray = null;
            singleInstance?.ReleaseMutex();
            Exit();
        }
    }
}
