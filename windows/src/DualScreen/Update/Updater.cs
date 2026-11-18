using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DualScreen.Core;

namespace DualScreen.Update;

/// <summary>
/// The external updater. Runs from a temporary copy of DualScreen.exe ("DualScreen.Updater.exe") so it never
/// executes from the file it replaces. Steps (each with rollback):
///   1. wait for the old process to exit (kill after 30 s);
///   2. rename DualScreen.exe → DualScreen.exe.old (renaming works even while Windows maps the image);
///   3. copy the verified new exe into place;
///   4. start it with --post-update TOKEN and wait until it signals the named event "it is alive";
///   5. on success delete the downloaded file (the .old file is removed by the new instance);
///      on failure kill the new process, restore the .old file and restart the previous version.
/// </summary>
public static class Updater
{
    public static int Run(string[] args)
    {
        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        int pid = int.TryParse(Arg("--pid"), out var p) ? p : 0;
        string target = Arg("--target") ?? throw new ArgumentException("--target");
        string newFile = Arg("--new") ?? throw new ArgumentException("--new");
        string token = Arg("--token") ?? Guid.NewGuid().ToString("N");
        string backup = target + ".old";
        Log.Info($"Updater: replacing {target} with {newFile}");

        try
        {
            WaitForExit(pid);

            if (!File.Exists(newFile)) throw new FileNotFoundException("Update file missing", newFile);

            Retry(() => { if (File.Exists(backup)) File.Delete(backup); }, "delete stale backup", throwOnFail: false);
            Retry(() => File.Move(target, backup), "rename current exe");
            try
            {
                Retry(() => File.Copy(newFile, target, overwrite: false), "copy new exe");
            }
            catch
            {
                Retry(() => File.Move(backup, target), "restore backup");
                throw;
            }

            using var ok = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\DualScreen.UpdateOk." + token);
            var started = Process.Start(new ProcessStartInfo(target, $"--post-update {token}") { UseShellExecute = true });
            bool alive = ok.WaitOne(TimeSpan.FromSeconds(45));
            if (!alive)
            {
                Log.Error("Updater: new version did not report a healthy start, rolling back");
                try { if (started != null && !started.HasExited) started.Kill(true); } catch { }
                Thread.Sleep(500);
                Retry(() => File.Delete(target), "delete broken exe");
                Retry(() => File.Move(backup, target), "restore backup");
                Process.Start(new ProcessStartInfo(target, "--update-failed") { UseShellExecute = true });
                return 2;
            }

            Retry(() => File.Delete(newFile), "delete downloaded file", throwOnFail: false);
            Log.Info("Updater: update completed");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Updater failed", ex);
            try
            {
                if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target);
                if (File.Exists(target)) Process.Start(new ProcessStartInfo(target, "--update-failed") { UseShellExecute = true });
            }
            catch (Exception ex2) { Log.Error("Updater rollback failed", ex2); }
            return 1;
        }
        finally { Log.Flush(); }
    }

    private static void WaitForExit(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!proc.WaitForExit(30_000))
            {
                Log.Warn("Updater: old process still running, terminating it");
                proc.Kill(true);
                proc.WaitForExit(5_000);
            }
        }
        catch (ArgumentException) { /* already gone */ }
    }

    private static void Retry(Action a, string what, bool throwOnFail = true)
    {
        Exception? last = null;
        for (int i = 0; i < 40; i++)
        {
            try { a(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(250 + i * 50); // antivirus scanners often hold new executables for a moment
            }
        }
        Log.Warn($"Updater: could not {what}: {last?.Message}");
        if (throwOnFail && last != null) throw last;
    }

    /// <summary>Called by a freshly started instance: signals the updater and cleans up leftovers.</summary>
    public static void OnStartup(string[] args)
    {
        int i = Array.IndexOf(args, "--post-update");
        if (i >= 0 && i + 1 < args.Length)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(@"Local\DualScreen.UpdateOk." + args[i + 1], out var ev))
                    using (ev) ev.Set();
            }
            catch (Exception ex) { Log.Warn("Could not signal updater: " + ex.Message); }
        }
        // Remove the previous version and stale updater copies once they are no longer in use.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(5000);
            TryDelete(AppPaths.ExePath + ".old");
            TryDelete(Path.Combine(AppPaths.UpdateDir, "DualScreen.Updater.exe"));
            foreach (var f in Directory.GetFiles(AppPaths.UpdateDir, "DualScreen-*.exe*")) TryDelete(f);
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* still locked; next start will retry */ }
    }

    public static bool IsDirectoryWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".dualscreen-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
