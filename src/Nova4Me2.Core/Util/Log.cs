namespace Nova4Me2.Core.Util;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>Very small process-wide logger with subscribers (UI log panel, CLI console, file).</summary>
public static class Log
{
    private static readonly object _lock = new();
    private static readonly List<Action<LogEntry>> _sinks = new();
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static IDisposable Subscribe(Action<LogEntry> sink)
    {
        lock (_lock) _sinks.Add(sink);
        return new Unsub(sink);
    }

    private sealed class Unsub(Action<LogEntry> s) : IDisposable
    {
        public void Dispose() { lock (_lock) _sinks.Remove(s); }
    }

    public static void Write(LogLevel level, string message)
    {
        if (level < MinLevel) return;
        var e = new LogEntry(DateTime.Now, level, message);
        Action<LogEntry>[] sinks;
        lock (_lock) sinks = _sinks.ToArray();
        foreach (var s in sinks) { try { s(e); } catch { } }
    }

    public static void Debug(string m) => Write(LogLevel.Debug, m);
    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);

    public static IDisposable ToFile(string path)
    {
        var w = new StreamWriter(path, append: true) { AutoFlush = true };
        var sub = Subscribe(e => { lock (w) w.WriteLine($"{e.Time:yyyy-MM-dd HH:mm:ss.fff} [{e.Level}] {e.Message}"); });
        return new Both(sub, w);
    }

    private sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose() { a.Dispose(); b.Dispose(); }
    }
}
