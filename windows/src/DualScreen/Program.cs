using System;
using System.Threading;
using DualScreen.Core;

namespace DualScreen;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Special modes run without any UI and exit.
        if (Array.IndexOf(args, "--apply-update") >= 0)
            return Update.Updater.Run(args);

        int d = Array.IndexOf(args, "--driver");
        if (d >= 0 && d + 1 < args.Length)
            return Display.DriverInstaller.ElevatedMain(args[d + 1]);

        // Single instance per user session.
        using var mutex = new Mutex(true, @"Local\DualScreen.SingleInstance", out bool first);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DualScreen.Show");
        if (!first)
        {
            // During an update the old instance may still be shutting down: wait for it briefly.
            bool postUpdate = Array.IndexOf(args, "--post-update") >= 0 || Array.IndexOf(args, "--update-failed") >= 0;
            if (!(postUpdate && mutex.WaitOne(TimeSpan.FromSeconds(30))))
            {
                if (Array.IndexOf(args, "--background") < 0) show.Set();
                return 0;
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Fatal error", e.ExceptionObject as Exception);
            Log.Flush();
        };

        var app = new App { Args = args, ShowSignal = show };
        app.InitializeComponent();
        int code = app.Run();
        Log.Flush();
        // Terminate even if a library left a foreground thread running.
        Environment.Exit(code);
        return code;
    }
}
