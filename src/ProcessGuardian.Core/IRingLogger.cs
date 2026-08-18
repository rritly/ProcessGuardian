using System;

namespace ProcessGuardian.Core
{
    public interface IRingLogger
    {
        void Log(string message);
        void LogError(string message, Exception? ex = null);
    }
}
