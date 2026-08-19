using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProcessGuardian.Core.Logging;

namespace ProcessGuardian.Services.Logging
{
    public sealed class RingLogger : IRingLogger
    {
        private readonly ProcessGuardian.Services.IFileStorage _storage;
        private readonly int _capacity;
        private readonly TimeSpan _flushInterval;
        private readonly string _logFileName = "log.txt";

        private readonly object _bufferLock = new();
        private LogEntry[] _buffer;
        private int _head = 0; // index of oldest
        private int _count = 0;
        // sequence numbers for entries. start from -1 so first Interlocked.Increment yields 0
        private long _nextSequence = -1;
        // signal used to request immediate flush from background loop (capacity 1)
        private readonly SemaphoreSlim _flushSignal = new(0, 1);

        private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private Task? _backgroundTask;
        private bool _isShuttingDown = false;

        public bool IsEnabled { get; private set; }

        public RingLogger(ProcessGuardian.Services.IFileStorage storage, int capacity, TimeSpan? flushInterval = null, bool enabled = true)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _flushInterval = flushInterval ?? TimeSpan.FromSeconds(5);
            _buffer = new LogEntry[_capacity];
            IsEnabled = enabled;

            // start background flush loop
            _backgroundTask = Task.Run(() => BackgroundLoopAsync(_cts.Token));
        }

        public void Log(LogLevel level, string message, Exception? exception = null)
        {
            if (!IsEnabled || _isShuttingDown) return;

            var entry = CreateEntry(level, message, exception);
            AddToBuffer(entry);
        }

        public Task LogAsync(LogLevel level, string message, Exception? exception = null, CancellationToken ct = default)
        {
            if (!IsEnabled || _isShuttingDown) return Task.CompletedTask;
            var entry = CreateEntry(level, message, exception);
            AddToBuffer(entry);
            return Task.CompletedTask;
        }

        private LogEntry CreateEntry(LogLevel level, string message, Exception? exception)
        {
            return new LogEntry
            {
                Sequence = Interlocked.Increment(ref _nextSequence),
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = level,
                Message = message ?? string.Empty,
                ExceptionText = FormatException(exception)
            };
        }

        private static string? FormatException(Exception? ex)
        {
            if (ex == null) return null;
            var sb = new StringBuilder();
            var current = ex;
            while (current != null)
            {
                sb.AppendLine($"{current.GetType()}: {current.Message}");
                if (!string.IsNullOrEmpty(current.StackTrace))
                {
                    sb.AppendLine(current.StackTrace);
                }
                current = current.InnerException;
            }
            return sb.ToString();
        }

        private void AddToBuffer(LogEntry entry)
        {
            lock (_bufferLock)
            {
                // insert at tail position
                int tail = (_head + _count) % _capacity;
                _buffer[tail] = entry;
                if (_count == _capacity)
                {
                    // overwrite oldest
                    _head = (_head + 1) % _capacity;
                }
                else
                {
                    _count++;
                }
            }
            // if buffer filled, request immediate flush (non-blocking signal)
            if (_count >= _capacity)
            {
                SignalFlush();
            }
        }

        private void SignalFlush()
        {
            try
            {
                _flushSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // already signalled; no-op
            }
        }

        public async Task FlushAsync(CancellationToken ct = default)
        {
            // Ensure only one flush at a time
            if (!IsEnabled) return;

            try
            {
                await _flushSemaphore.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // cancellation requested by caller while waiting for flush lock - treat as graceful no-op
                return;
            }
            try
            {
                List<LogEntry> snapshot = SnapshotAndGetForFlush();
                if (snapshot.Count == 0) return;

                // perform I/O outside buffer lock
                await _storage.EnsureFolderExistsAsync(ct).ConfigureAwait(false);

                string? existing = null;
                try
                {
                    existing = await _storage.ReadAllTextAsync(_logFileName, ct).ConfigureAwait(false);
                }
                catch
                {
                    // reading failure -> proceed as if no existing
                    existing = null;
                }

                var existingEntries = ParseEntries(existing);
                var newEntries = FormatEntries(snapshot);

                var combined = new List<string>(existingEntries.Count + newEntries.Count);
                combined.AddRange(existingEntries);
                combined.AddRange(newEntries);

                // keep only last _capacity entries
                int toKeep = Math.Min(_capacity, combined.Count);
                var final = combined.GetRange(combined.Count - toKeep, toKeep);

                var finalText = string.Join(Environment.NewLine, final);

                try
                {
                    await _storage.WriteAllTextAtomicAsync(_logFileName, finalText, ct).ConfigureAwait(false);
                }
                catch
                {
                    // write failed; do not remove buffer entries
                    return;
                }

                // On success, remove flushed entries from RAM buffer
                RemoveFlushedFromBuffer(snapshot);
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        private List<LogEntry> SnapshotAndGetForFlush()
        {
            lock (_bufferLock)
            {
                var list = new List<LogEntry>(_count);
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head + i) % _capacity;
                    list.Add(_buffer[idx]);
                }
                return list;
            }
        }

        private void RemoveFlushedFromBuffer(List<LogEntry> flushed)
        {
            if (flushed.Count == 0) return;
            long lastSeq = flushed[flushed.Count - 1].Sequence;
            lock (_bufferLock)
            {
                // remove entries from head while their sequence <= lastSeq
                while (_count > 0)
                {
                    var e = _buffer[_head];
                    if (e.Sequence <= lastSeq)
                    {
                        // drop
                        _head = (_head + 1) % _capacity;
                        _count--;
                    }
                    else break;
                }
            }
        }

        private static List<string> ParseEntries(string? content)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(content)) return result;
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (var raw in lines)
            {
                var line = raw;
                if (IsEntryHeader(line))
                {
                    if (sb.Length > 0)
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                    }
                    sb.Append(line);
                }
                else
                {
                    // continuation
                    if (sb.Length > 0) sb.AppendLine().Append(line);
                    else sb.Append(line);
                }
            }
            if (sb.Length > 0) result.Add(sb.ToString());
            return result;
        }

        private static bool IsEntryHeader(string line)
        {
            // Very small heuristic: starts with ISO date and contains [
            if (line.Length < 20) return false;
            // e.g. 2026-08-19T12:34:56.1234Z [Information]
            return line.Contains("Z [") && (line.Contains("[Information]") || line.Contains("[Warning]") || line.Contains("[Error]") );
        }

        private static List<string> FormatEntries(IEnumerable<LogEntry> entries)
        {
            var list = new List<string>();
            foreach (var e in entries)
            {
                var sb = new StringBuilder();
                sb.Append(e.TimestampUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffff'Z'"));
                sb.Append(" [").Append(e.Level.ToString()).Append("] ");
                sb.Append(e.Message);
                if (!string.IsNullOrEmpty(e.ExceptionText))
                {
                    var exLines = e.ExceptionText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var l in exLines)
                    {
                        sb.AppendLine();
                        sb.Append("    ").Append(l);
                    }
                }
                list.Add(sb.ToString());
            }
            return list;
        }

        private async Task BackgroundLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Task delayTask = Task.Delay(_flushInterval, ct);
                    Task signalTask = _flushSignal.WaitAsync(ct);

                    Task completed = await Task.WhenAny(delayTask, signalTask).ConfigureAwait(false);

                    // attempt flush either when timer elapsed or when signalled
                    try
                    {
                        await FlushAsync(ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // swallow exceptions from flush to avoid crashing the background loop
                    }
                }
            }
            finally
            {
                // background stopping
            }
        }

        public async Task ShutdownAsync(CancellationToken ct = default)
        {
            _isShuttingDown = true;
            try
            {
                _cts.Cancel();
            }
            catch { }

            // wait for background to exit
            if (_backgroundTask != null)
            {
                try
                {
                    await Task.WhenAny(_backgroundTask, Task.Delay(TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false);
                }
                catch { }
            }

            // final flush
            try
            {
                await FlushAsync(ct).ConfigureAwait(false);
            }
            catch { }
        }
    }
}
