using System;
using System.Threading;
using System.Windows;

namespace GameTracker
{
    public partial class App : Application
    {
        // Per-user single-instance guard. Without this, launching the app again
        // stacks up extra processes — and each tries (and fails) to bind the overlay
        // port. Instead, a second launch tells the running instance to come forward
        // and then exits, so PIDs/ports never accumulate.
        private const string MutexName = "Local\\TequilasTavern.SingleInstance";
        private const string ShowEventName = "Local\\TequilasTavern.Show";

        private Mutex? _instanceMutex;
        private EventWaitHandle? _showEvent;
        private RegisteredWaitHandle? _showRegistration;
        private bool _isPrimary;

        protected override void OnStartup(StartupEventArgs e)
        {
            _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out _isPrimary);

            if (!_isPrimary)
            {
                // Already running: nudge the live instance to surface, then bow out.
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                    {
                        existing.Set();
                        existing.Dispose();
                    }
                }
                catch { /* best-effort */ }
                Shutdown();
                return;
            }

            // Primary instance: listen for "show" pings from any later launches.
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                _showRegistration = ThreadPool.RegisterWaitForSingleObject(
                    _showEvent, (_, _) => BringToFront(), null, Timeout.Infinite, executeOnlyOnce: false);
            }
            catch { /* focusing the existing window is best-effort */ }

            base.OnStartup(e);

            // One-time settings migrations, then the saved colour theme.
            try { Services.SettingsService.RunMigrations(); } catch { /* keep whatever's saved */ }
            try { Services.ThemeService.Initialize(); } catch { /* fall back to XAML defaults */ }

            // Hidden docs mode: render window screenshots for the How-to guide, then exit.
            var docsDir = Services.DocsCapture.OutDirFromArgs(e.Args);
            if (docsDir != null)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher.InvokeAsync(async () =>
                {
                    try { await Services.DocsCapture.RunAsync(docsDir); } catch { }
                    Shutdown();
                });
                return;
            }

            // License gate: block the app until a valid, machine-bound key is activated.
            // The activation window is the ONLY window open at this point, so with the default
            // "shut down when the last window closes" the app would exit the moment it closes —
            // before MainWindow is shown. Suspend that until MainWindow is up.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (!Services.LicenseService.IsActivated(out _))
            {
                var gate = new Views.ActivationWindow();
                if (gate.ShowDialog() != true)   // user quit without activating
                {
                    Shutdown();
                    return;
                }
            }
            else
            {
                // Already activated: quietly re-check revocation in the background (no-op offline).
                _ = Services.LicenseService.RevalidateAsync();
            }

            // Start the live mic morph chain if the streamer has it enabled.
            try { Services.VoiceMorphService.Start(); } catch { /* engine is optional */ }

            var win = new MainWindow();
            MainWindow = win;
            win.Show();

            // MainWindow is up — restore normal shutdown (exit when all windows are closed).
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }

        private void BringToFront()
        {
            Dispatcher.Invoke(() =>
            {
                var w = MainWindow;
                if (w == null) return;
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Show();
                w.Activate();
                // Brief topmost flip to pull it above other windows, then release.
                w.Topmost = true;
                w.Topmost = false;
                w.Focus();
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Free the overlay port and release the single-instance guard so the next
            // launch starts clean.
            try { Services.VoiceMorphService.Stop(); } catch { }
            try { Services.OverlayServer.Stop(); } catch { }
            try { _showRegistration?.Unregister(null); } catch { }
            try { _showEvent?.Dispose(); } catch { }
            if (_isPrimary)
            {
                try { _instanceMutex?.ReleaseMutex(); } catch { }
            }
            try { _instanceMutex?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
