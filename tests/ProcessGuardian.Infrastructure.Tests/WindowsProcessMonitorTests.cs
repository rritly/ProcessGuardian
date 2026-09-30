using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain.ValueObjects;
using ProcessGuardian.Infrastructure.ProcessMonitoring;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

public class WindowsProcessMonitorTests : IDisposable
{
    private readonly WindowsProcessMonitor _monitor;
    private readonly string _notepadPath;
    private readonly ProcessTarget _target;
    private int? _ownedPid;

    public WindowsProcessMonitorTests()
    {
        _monitor = new WindowsProcessMonitor(NullLogger<WindowsProcessMonitor>.Instance);
        _notepadPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(_notepadPath))
            throw new Xunit.Sdk.XunitException($"notepad.exe not found at {_notepadPath}");
        _target = new ProcessTarget(
            _notepadPath,
            Path.GetFileNameWithoutExtension(_notepadPath),
            Path.GetDirectoryName(_notepadPath) ?? string.Empty);
    }

    public void Dispose()
    {
        if (_ownedPid.HasValue)
        {
            try
            {
                using var p = Process.GetProcessById(_ownedPid.Value);
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch
            {
                // Process already gone or inaccessible; ignore
            }
        }
    }

    private void StartOwnNotepad()
    {
        var psi = new ProcessStartInfo(_notepadPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var proc = Process.Start(psi)!;
        _ownedPid = proc.Id;
        proc.Dispose();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {message}");
    }

    [Fact]
    public async Task AfterStart_CheckReturnsRunning_WithProcessId()
    {
        StartOwnNotepad();
        await WaitUntilAsync(
            async () => (await _monitor.CheckAsync(_target, CancellationToken.None)).Outcome == ProcessOutcome.Running,
            "notepad to be detected as Running");

        var result = await _monitor.CheckAsync(_target, CancellationToken.None);
        result.Outcome.Should().Be(ProcessOutcome.Running);
        result.ProcessId.Should().NotBeNull();
    }

    [Fact]
    public async Task FullPathMatch_IsCaseInsensitive()
    {
        StartOwnNotepad();
        await WaitUntilAsync(
            async () => (await _monitor.CheckAsync(_target, CancellationToken.None)).Outcome == ProcessOutcome.Running,
            "notepad to be detected as Running");

        var upperPath = _notepadPath.ToUpperInvariant();
        var targetUpper = new ProcessTarget(upperPath, "notepad", Path.GetDirectoryName(upperPath) ?? string.Empty);
        var result = await _monitor.CheckAsync(targetUpper, CancellationToken.None);
        result.Outcome.Should().Be(ProcessOutcome.Running);
    }

    [Fact]
    public async Task AfterKill_CheckReturnsNotRunning()
    {
        var preRunning = (await _monitor.CheckAsync(_target, CancellationToken.None)).Outcome == ProcessOutcome.Running;

        StartOwnNotepad();
        await WaitUntilAsync(
            async () => (await _monitor.CheckAsync(_target, CancellationToken.None)).Outcome == ProcessOutcome.Running,
            "notepad to be detected as Running");

        using (var proc = Process.GetProcessById(_ownedPid!.Value))
        {
            proc.Kill();
            proc.WaitForExit(3000);
        }
        _ownedPid = null;

        if (!preRunning)
        {
            await WaitUntilAsync(
                async () => (await _monitor.CheckAsync(_target, CancellationToken.None)).Outcome == ProcessOutcome.NotRunning,
                "notepad to be gone after kill");
        }
    }

    [Fact]
    public async Task StartAsyncNonexistentPath_ReturnsFalse_NoThrow()
    {
        var bogus = new ProcessTarget(
            @"C:\DoesNotExist\missing.exe",
            "missing",
            @"C:\DoesNotExist");

        Func<Task<bool>> act = () => _monitor.StartAsync(bogus, CancellationToken.None);
        var result = await act.Should().NotThrowAsync();
        result.Which.Should().BeFalse();
    }
}
