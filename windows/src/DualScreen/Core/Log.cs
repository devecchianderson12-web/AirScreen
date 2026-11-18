using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DualScreen.Core;

/// <summary>Small asynchronous file logger with daily files and 7-day retention.</summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 4096);
    private static readonly Thread Writer;
    public static event Action<string>? LineWritten;

    static Log()
    {
        Writer = new Thread(WriteLoop) { IsBackground = true, Name = "DualScreen.Log", Priority = ThreadPriority.BelowNormal };
        Writer.Start();
        try
        {
            foreach (var f in Directory.GetFiles(AppPaths.LogDir, "*.log")
                         .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)))
                File.Delete(f);
        }
        catch { /* best effort */ }
    }

    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR", ex == null ? msg : $"{msg}: {ex}");
    public static void Debug(string msg)
    {
#if DEBUG
        Write("DBG", msg);
#endif
    }

    private static void Write(string level, string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        Queue.TryAdd(line);
        try { LineWritten?.Invoke(line); } catch { }
    }

    public static void Flush()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Queue.Count > 0 && sw.ElapsedMilliseconds < 1000) Thread.Sleep(10);
    }

    private static void WriteLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                string file = Path.Combine(AppPaths.LogDir, $"dualscreen-{DateTime.Now:yyyyMMdd}.log");
                var sb = new StringBuilder(line).AppendLine();
                while (Queue.TryTake(out var more)) sb.AppendLine(more);
                File.AppendAllText(file, sb.ToString());
            }
            catch { /* never crash because of logging */ }
        }
    }
}
