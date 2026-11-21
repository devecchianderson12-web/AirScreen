using System;
using System.Threading;
using System.Windows;
using DualScreen.Core;
using DualScreen.UI;

namespace DualScreen;

public partial class App : Application
{
    private AppController? _controller;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private bool _exiting;

    public string[] Args { get; init; } = Array.Empty<string>();
    public EventWaitHandle? ShowSignal { init => _showSignal = value; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };

        try
        {
            _controller = new AppController();
            _controller.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            MessageBox.Show("DualScreen no pudo iniciarse:\n" + ex.Message, "DualScreen", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _tray = new TrayIcon(this);
        _controller.Notify += (title, text) => Dispatcher.BeginInvoke(() => _tray.ShowNotification(title, text));
        _controller.Updates.UpdateAvailable += rel => Dispatcher.BeginInvoke(() =>
            _tray.ShowNotification("Actualización disponible",
                $"DualScreen {rel.Version} está disponible (tienes {AppInfo.VersionString}). Haz clic para actualizar.", "updates"));
        _controller.StateChanged += () => Dispatcher.BeginInvoke(() => _tray?.UpdateState());
        // When the full-screen viewer closes (Esc, the other PC stopped, or an error) bring the app back.
        Receiver.ReceiverController.Instance.Ended += error => Dispatcher.BeginInvoke(() =>
        {
            _tray?.UpdateState();
            if (error != null || _window != null) ShowMainWindow("receive");
        });
        Receiver.ReceiverController.Instance.Changed += () => Dispatcher.BeginInvoke(() => _tray?.UpdateState());

        // A second launch of DualScreen.exe just asks this instance to show its window.
        if (_showSignal != null)
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(() => ShowMainWindow()), null, -1, false);

        Update.Updater.OnStartup(Args);
        if (Array.IndexOf(Args, "--update-failed") >= 0)
            _tray.ShowNotification("Actualización revertida", "La nueva versión no se inició correctamente; se restauró la anterior.");
        else if (Array.IndexOf(Args, "--post-update") >= 0)
            _tray.ShowNotification("DualScreen actualizado", $"Ahora usas la versión {AppInfo.VersionString}.");

        // Starts in the tray ("Iniciar minimizado", default) or with the window open.
        if (!SettingsStore.Current.StartMinimized) ShowMainWindow();
        Log.Info($"DualScreen {AppInfo.VersionString} started (data: {AppPaths.DataDir})");
    }

    public void ShowMainWindow(string? page = null)
    {
        if (_window == null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) =>
            {
                _window = null;
                // The window is destroyed (not hidden) to release its visual tree while idle in the tray.
                GC.Collect(2, GCCollectionMode.Optimized, false);
            };
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        if (page != null) _window.Navigate(page);
    }

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exiting");
        // Watchdog: whatever happens during cleanup, the process must end quickly (the updater is waiting to
        // replace the exe, and a lingering process blocks the next launch through the single-instance mutex).
        new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(8));
            Log.Warn("Cleanup took too long; forcing exit");
            Log.Flush();
            Environment.Exit(0);
        }) { IsBackground = true, Name = "DualScreen.ExitWatchdog" }.Start();
        _window?.Close();
        _tray?.Dispose();
        _showWait?.Unregister(null);
        try { Receiver.ReceiverController.Instance.Dispose(); } catch (Exception ex) { Log.Error("Receiver shutdown error", ex); }
        try { _controller?.Dispose(); } catch (Exception ex) { Log.Error("Shutdown error", ex); }
        Log.Info("Shutdown complete");
        Log.Flush();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting)
        {
            _tray?.Dispose();
            _controller?.Dispose();
        }
        base.OnExit(e);
    }
}
