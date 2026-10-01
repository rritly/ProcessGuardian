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

    // File output is throttled: Emit only enqueues; a background timer appends pending
    // entries at most once per interval. LogUpdated also fires from the timer.
    private const int FlushIntervalMs = 500;

    // History is append-only so it survives restarts; at the size cap the file rotates
    // to a single archive, bounding disk usage to roughly two generations.
    private const long MaxFileBytes = 512 * 1024;
    private const string ArchiveFileName = "logs.1.log";

    private readonly ConcurrentQueue<string> _messages = new();
    private readonly ConcurrentQueue<string> _pendingWrite = new();
    private readonly string _logFilePath;
    private readonly long _maxFileBytes;
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
        _maxFileBytes = MaxFileBytes;
        _flushTimer = new Timer(FlushTimerCallback, null, FlushIntervalMs, FlushIntervalMs);
    }

    // Test seam: hermetic file path and size cap; production always uses the other ctor.
    internal InMemoryLogSink(string logFilePath, long maxFileBytes = MaxFileBytes)
    {
        _logFilePath = logFilePath;
        _maxFileBytes = maxFileBytes;
        _flushTimer = new Timer(FlushTimerCallback, null, FlushIntervalMs, FlushIntervalMs);
    }

    public void Emit(LogEvent logEvent)
    {
        try
        {
            var msg = Format(logEvent);
            _messages.Enqueue(msg);
            while (_messages.Count > MaxBufferSize && _messages.TryDequeue(out _)) { }
            _pendingWrite.Enqueue(msg);
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
            while (_pendingWrite.TryDequeue(out _)) { }
            _dirty = false;
            TruncateFile();
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

    // Appends everything queued since the last flush. Pending entries are dequeued
    // inside the lock so a concurrent Clear() cannot interleave with the append.
    private void WriteBuffer()
    {
        if (_pendingWrite.IsEmpty)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
        lock (_writeLock)
        {
            if (_pendingWrite.IsEmpty)
                return;
            var sb = new StringBuilder();
            while (_pendingWrite.TryDequeue(out var line))
                sb.AppendLine(line);
            RotateIfNeeded();
            File.AppendAllText(_logFilePath, sb.ToString());
        }
    }

    // Best-effort: if the file is locked (e.g. open in a viewer) appending still
    // proceeds and the next flush retries the rotation.
    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_logFilePath);
            if (!info.Exists || info.Length < _maxFileBytes)
                return;
            var archive = Path.Combine(Path.GetDirectoryName(_logFilePath)!, ArchiveFileName);
            if (File.Exists(archive))
                File.Delete(archive);
            File.Move(_logFilePath, archive);
        }
        catch
        {
            // Appending still proceeds; the next flush retries the rotation.
        }
    }

    private void TruncateFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
            lock (_writeLock)
            {
                File.WriteAllText(_logFilePath, string.Empty);
            }
        }
        catch
        {
            // Best-effort; a locked file can be cleared again later.
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
