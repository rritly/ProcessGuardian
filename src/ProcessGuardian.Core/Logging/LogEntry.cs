using System;

namespace ProcessGuardian.Core.Logging
{
    // Internal DTO representing an in-memory log entry
    public sealed class LogEntry
    {
        public long Sequence { get; init; }
        public DateTimeOffset TimestampUtc { get; init; }
        public LogLevel Level { get; init; }
        public string Message { get; init; } = string.Empty;
        public string? ExceptionText { get; init; }
    }
}
