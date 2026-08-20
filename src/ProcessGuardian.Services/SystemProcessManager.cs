using ProcessGuardian.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Services
{
    public class SystemProcessManager : IProcessManager
    {
        public Task<IReadOnlyList<ProcessInfo>> GetProcessesByNameAsync(string processName, CancellationToken ct = default)
        {
            var list = new List<ProcessInfo>();
            try
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var p in processes)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    var info = new ProcessInfo
                    {
                        ProcessId = p.Id,
                        ProcessName = p.ProcessName
                    };

                    try
                    {
                        // Accessing MainModule may throw for protected processes
                        var module = p.MainModule;
                        info.ExecutablePath = module?.FileName;
                        info.IsPathAvailable = info.ExecutablePath != null;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is NotSupportedException)
                    {
                        info.IsPathAvailable = false;
                        info.PathError = ex.Message;
                    }
                    catch
                    {
                        info.IsPathAvailable = false;
                    }

                    list.Add(info);
                }
            }
            catch (Exception)
            {
                // If enumeration fails entirely, return empty list
            }

            return Task.FromResult((IReadOnlyList<ProcessInfo>)list);
        }

        public Task<ProcessStartResult> StartProcessAsync(ProcessStartRequest request, CancellationToken ct = default)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = request.FileName,
                    Arguments = request.Arguments ?? string.Empty,
                    WorkingDirectory = request.WorkingDirectory ?? string.Empty,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    return Task.FromResult(new ProcessStartResult
                    {
                        Started = true,
                        ProcessId = proc.Id
                    });
                }

                return Task.FromResult(new ProcessStartResult { Started = false, ErrorMessage = "Process.Start returned null" });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ProcessStartResult { Started = false, ErrorMessage = ex.Message });
            }
        }
    }
}
