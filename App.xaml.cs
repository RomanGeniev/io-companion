using System;
using System.Linq;
using System.Threading;
using System.Windows;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    public partial class App : Application
    {
        private const string MutexName = "GraphiteCompanion.SingleInstance";
        private const string ShowEventName = "GraphiteCompanion.Show";

        private Mutex? mutex;
        private EventWaitHandle? showEvent;
        private EventWaitHandle? flyoutEvent;
        private const string FlyoutEventName = "GraphiteCompanion.Flyout";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (_, args) =>
            {
                SettingsStore.LogError(args.Exception);
                MessageBox.Show("Произошла ошибка: " + args.Exception.Message +
                                "\n\nПодробности записаны в %APPDATA%\\GraphiteCompanion\\error.log",
                                "IO Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };

            bool wantFlyout = e.Args.Any(a => a.Equals("--flyout", StringComparison.OrdinalIgnoreCase));
            mutex = new Mutex(true, MutexName, out bool isFirst);
            if (!isFirst)
            {
                // Already running: ask the first instance to show its window (or the tray panel).
                try { EventWaitHandle.OpenExisting(wantFlyout ? FlyoutEventName : ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }

            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            flyoutEvent = new EventWaitHandle(false, EventResetMode.AutoReset, FlyoutEventName);
            bool startInTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            var window = new MainWindow();
            MainWindow = window;

            var listener = new Thread(() =>
            {
                while (showEvent.WaitOne())
                    Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
            }) { IsBackground = true, Name = "ShowListener" };
            listener.Start();
            var flyoutListener = new Thread(() =>
            {
                while (flyoutEvent.WaitOne())
                    Dispatcher.BeginInvoke(new Action(window.ToggleFlyout));
            }) { IsBackground = true, Name = "FlyoutListener" };
            flyoutListener.Start();

            if (!startInTray) window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            mutex?.Dispose();
            showEvent?.Dispose();
            base.OnExit(e);
        }
    }
}
