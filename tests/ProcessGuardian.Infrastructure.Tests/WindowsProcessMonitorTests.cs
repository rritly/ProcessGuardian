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
    public void TryStart_NonexistentPath_ReturnsMissing_NoThrow()
    {
        var bogus = new ProcessTarget(
            @"C:\DoesNotExist\missing.exe",
            "missing",
            @"C:\DoesNotExist");

        var result = _monitor.TryStart(bogus, CancellationToken.None);

        result.Status.Should().Be(ProcessStartStatus.Missing);
    }

    [Fact]
    public void EvaluateCandidates_ReadableMatch_IsRunning()
    {
        var candidates = new[]
        {
            new WindowsProcessMonitor.ProcessCandidate(101, @"C:\App\other.exe", Unreadable: false),
            new WindowsProcessMonitor.ProcessCandidate(202, @"C:\App\target.exe", Unreadable: false),
        };

        var result = WindowsProcessMonitor.EvaluateCandidates(@"C:\app\target.exe", candidates);

        result.Outcome.Should().Be(ProcessOutcome.Running);
        result.ProcessId.Should().Be(202);
    }

    [Fact]
    public void EvaluateCandidates_AllReadableNoMatch_IsNotRunning()
    {
        var candidates = new[]
        {
            new WindowsProcessMonitor.ProcessCandidate(101, @"C:\App\other.exe", Unreadable: false),
            new WindowsProcessMonitor.ProcessCandidate(202, @"C:\App\another.exe", Unreadable: false),
        };

        var result = WindowsProcessMonitor.EvaluateCandidates(@"C:\App\target.exe", candidates);

        result.Outcome.Should().Be(ProcessOutcome.NotRunning);
    }

    [Fact]
    public void EvaluateCandidates_UnreadableNoReadableMatch_IsUnknown_NotNotRunning()
    {
        // E discipline: at least one unreadable candidate with no readable match is Unknown,
        // never NotRunning (which would trigger a duplicate relaunch of a possibly-running target).
        var candidates = new[]
        {
            new WindowsProcessMonitor.ProcessCandidate(101, null, Unreadable: true),
        };

        var result = WindowsProcessMonitor.EvaluateCandidates(@"C:\App\target.exe", candidates);

        result.Outcome.Should().Be(ProcessOutcome.Unknown);
    }

    [Fact]
    public void EvaluateCandidates_UnreadablePlusReadableMatch_IsRunning_NotDowngraded()
    {
        // A single unreadable module must never downgrade a readable match (E).
        var candidates = new[]
        {
            new WindowsProcessMonitor.ProcessCandidate(101, null, Unreadable: true),
            new WindowsProcessMonitor.ProcessCandidate(202, @"C:\App\target.exe", Unreadable: false),
        };

        var result = WindowsProcessMonitor.EvaluateCandidates(@"C:\App\target.exe", candidates);

        result.Outcome.Should().Be(ProcessOutcome.Running);
        result.ProcessId.Should().Be(202);
    }

    // R-KE: document and verify the native Win32 error-code mapping at the launch boundary.
    [Theory]
    [InlineData(2, ProcessStartStatus.Missing)]  // ERROR_FILE_NOT_FOUND
    [InlineData(3, ProcessStartStatus.Missing)]  // ERROR_PATH_NOT_FOUND
    [InlineData(5, ProcessStartStatus.ReadError)] // ERROR_ACCESS_DENIED
    [InlineData(32, ProcessStartStatus.ReadError)] // ERROR_SHARING_VIOLATION
    [InlineData(193, ProcessStartStatus.Failed)]  // ERROR_BAD_EXE_FORMAT
    [InlineData(740, ProcessStartStatus.Failed)]  // ERROR_ELEVATION_REQUIRED
    [InlineData(206, ProcessStartStatus.Failed)]  // ERROR_FILENAME_EXCED_RANGE
    public void ClassifyWin32Error_MapsDocumentedCodes(int nativeCode, ProcessStartStatus expected)
    {
        WindowsProcessMonitor.ClassifyWin32Error(nativeCode).Should().Be(expected);
    }
}
