using ProcessGuardian.Core;
using System.Collections.Concurrent;
using System.Linq;

namespace ProcessGuardian.Tests.Fakes
{
    internal class MockLogger : IRingLogger
    {
        private readonly ConcurrentQueue<string> _messages = new();
        private readonly ConcurrentQueue<(string, System.Exception?)> _errors = new();

        public void Log(string message)
        {
            _messages.Enqueue(message);
        }

        public void LogError(string message, System.Exception? ex = null)
        {
            _errors.Enqueue((message, ex));
        }

        public string[] GetMessages() => _messages.ToArray();
        public (string, System.Exception?)[] GetErrors() => _errors.ToArray();
    }
}
