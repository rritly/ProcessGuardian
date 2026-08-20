using ProcessGuardian.Core;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Tests.Fakes
{
    internal class FakeProcessManager : IProcessManager
    {
        private readonly List<ProcessInfo> _processes = new();
        private int _nextId = 1000;

        public void AddProcess(string filePath)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(filePath);
            _processes.Add(new ProcessInfo { ProcessId = _nextId++, ProcessName = name, ExecutablePath = filePath, IsPathAvailable = true });
        }

        public void RemoveByPath(string filePath)
        {
            _processes.RemoveAll(p => string.Equals(p.ExecutablePath, filePath, System.StringComparison.OrdinalIgnoreCase));
        }

        public void AddSameNameUnavailable(string processName)
        {
            _processes.Add(new ProcessInfo { ProcessId = _nextId++, ProcessName = processName, ExecutablePath = null, IsPathAvailable = false });
        }

        public Task<IReadOnlyList<ProcessInfo>> GetProcessesByNameAsync(string processName, CancellationToken ct = default)
        {
            IReadOnlyList<ProcessInfo> result;
            lock (_processes)
            {
                result = _processes.Where(p => string.Equals(p.ProcessName, processName, System.StringComparison.OrdinalIgnoreCase)).Select(p => new ProcessInfo
                {
                    ProcessId = p.ProcessId,
                    ProcessName = p.ProcessName,
                    ExecutablePath = p.ExecutablePath,
                    IsPathAvailable = p.IsPathAvailable,
                    PathError = p.PathError
                }).ToList();
            }

            return Task.FromResult(result);
        }

        public Task<ProcessStartResult> StartProcessAsync(ProcessStartRequest request, CancellationToken ct = default)
        {
            // Simulate quick start: add process to list
            lock (_processes)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(request.FileName);
                var pid = _nextId++;
                _processes.Add(new ProcessInfo { ProcessId = pid, ProcessName = name, ExecutablePath = request.FileName, IsPathAvailable = true });
                return Task.FromResult(new ProcessStartResult { Started = true, ProcessId = pid });
            }
        }
    }
}
