using System;
using System.Threading;
using Microsoft.UI.Xaml;

namespace RoundedTB
{
    public partial class App : Application
    {
        private Mutex singleInstance;
        private AppHost host;

        public App()
        {
            InitializeComponent();
            DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
            UnhandledException += (_, e) =>
            {
                host?.RestoreTaskbars();
                AppHost.LogError(e.Exception);
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => host?.RestoreTaskbars();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            singleInstance = new Mutex(true, "RoundedTB.SingleInstance", out bool created);
            if (!created)
            {
                IntPtr running = Native.FindWindow(TrayIcon.WindowClass, TrayIcon.WindowClass);
                Native.GetWindowThreadProcessId(running, out uint runningPid);
                Native.AllowSetForegroundWindow(runningPid);
                Native.PostMessage(running, Native.RegisterWindowMessage(TrayIcon.ShowSettingsMessageName), IntPtr.Zero, IntPtr.Zero);
                Exit();
                return;
            }
            host = new AppHost();
        }
    }
}
