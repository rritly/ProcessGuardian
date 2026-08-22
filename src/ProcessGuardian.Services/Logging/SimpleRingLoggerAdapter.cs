using System;
using ProcessGuardian.Core.Logging;

namespace ProcessGuardian.Services.Logging
{
    public sealed class SimpleRingLoggerAdapter : ProcessGuardian.Core.IRingLogger
    {
        private readonly ProcessGuardian.Core.Logging.IRingLogger _inner;

        public SimpleRingLoggerAdapter(ProcessGuardian.Core.Logging.IRingLogger inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public void Log(string message)
        {
            _inner.Log(LogLevel.Information, message);
        }

        public void LogError(string message, Exception? ex = null)
        {
            _inner.Log(LogLevel.Error, message, ex);
        }
    }
}
