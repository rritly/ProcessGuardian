using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace ProcessGuardian.Infrastructure.Logging;

public sealed class InMemoryLogSink : ILogEventSink
{
    private const int MaxBufferSize = 600;
    private const int DefaultRecentCount = 200;

    // File output is throttled: Emit only enqueues; a background timer writes the
    // full file at most once per interval. LogUpdated also fires from the timer.
    private const int FlushIntervalMs = 500;

    private readonly ConcurrentQueue<string> _messages = new();
    private readonly string _logFilePath;
    private readonly object _writeLock = new();
    private readonly Timer _flushTimer;
    private volatile bool _dirty;

    public string LogFilePath => _logFilePath;
    public event EventHandler? LogUpdated;

    public InMemoryLogSink()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProcessGuardian");
        _logFilePath = Path.Combine(dir, "logs.log");
        _flushTimer = new Timer(FlushTimerCallback, null, FlushIntervalMs, FlushIntervalMs);
    }

    // Test seam: hermetic file path; production always uses the parameterless constructor.
    internal InMemoryLogSink(string logFilePath)
    {
        _logFilePath = logFilePath;
        _flushTimer = new Timer(FlushTimerCallback, null, FlushIntervalMs, FlushIntervalMs);
    }

    public void Emit(LogEvent logEvent)
    {
        try
        {
            var msg = Format(logEvent);
            _messages.Enqueue(msg);
            while (_messages.Count > MaxBufferSize && _messages.TryDequeue(out _)) { }
            _dirty = true;
        }
        catch (Exception)
        {
            // Sink must never throw - Serilog will disable it if it does.
        }
    }

    public string GetRecentLogs(int count = DefaultRecentCount)
    {
        try
        {
            // ConcurrentQueue has no TakeLast(); snapshot to array first.
            var array = _messages.ToArray();
            return string.Join(Environment.NewLine, array.TakeLast(count));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public void Clear()
    {
        try
        {
            while (_messages.TryDequeue(out _)) { }
            _dirty = false;
            WriteBuffer();
            LogUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // Clear must never throw.
        }
    }

    // Forces a pending buffer to disk immediately (used on shutdown so the last
    // entries are not lost to the throttle). No-op when nothing is pending.
    public void Flush()
    {
        try
        {
            if (!_dirty)
                return;
            _dirty = false;
            WriteBuffer();
        }
        catch (Exception)
        {
            // Flush must never throw.
        }
    }

    private void FlushTimerCallback(object? state)
    {
        try
        {
            if (!_dirty)
                return;
            _dirty = false;
            WriteBuffer();
            LogUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // The flush callback must never throw.
        }
    }

    private void WriteBuffer()
    {
        var lines = _messages.ToArray();
        var sb = new StringBuilder(lines.Length * 64);
        foreach (var line in lines)
            sb.AppendLine(line);
        Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
        lock (_writeLock)
        {
            File.WriteAllText(_logFilePath, sb.ToString());
        }
    }

    private static string Format(LogEvent logEvent)
    {
        var timestamp = logEvent.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var message = logEvent.RenderMessage();
        if (logEvent.Exception is not null)
            message += Environment.NewLine + logEvent.Exception;

        return $"{timestamp} {MapLevel(logEvent.Level)}  {message}";
    }

    private static string MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INFO",
        LogEventLevel.Warning => "WARN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FATAL",
        _ => level.ToString().ToUpperInvariant(),
    };
}
