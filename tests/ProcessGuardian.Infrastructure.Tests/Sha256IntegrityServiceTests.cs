using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain.ValueObjects;
using ProcessGuardian.Infrastructure.ProcessMonitoring;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

public class Sha256IntegrityServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly Sha256IntegrityService _service;

    public Sha256IntegrityServiceTests()
    {
        _service = new Sha256IntegrityService(NullLogger<Sha256IntegrityService>.Instance);
        _dir = Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; never throw from Dispose
        }
    }

    [Fact]
    public async Task ComputeHashAsync_KnownContent_ReturnsUppercaseHex()
    {
        var path = Path.Combine(_dir, "target.exe");
        await File.WriteAllTextAsync(path, "abc");

        var result = await _service.ComputeHashAsync(path);

        // Well-known SHA-256 of "abc".
        result.IsComputed.Should().BeTrue();
        result.Hash.Should().Be("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
    }

    [Fact]
    public async Task ComputeHashAsync_IsDeterministic()
    {
        var path = Path.Combine(_dir, "target.exe");
        await File.WriteAllTextAsync(path, "stable content");

        var first = await _service.ComputeHashAsync(path);
        var second = await _service.ComputeHashAsync(path);

        first.IsComputed.Should().BeTrue();
        second.Hash.Should().Be(first.Hash);
    }

    [Fact]
    public async Task ComputeHashAsync_MissingFile_ReturnsMissing()
    {
        var result = await _service.ComputeHashAsync(Path.Combine(_dir, "missing.exe"));

        // A clean "no file" is a distinct outcome (Missing), not a read failure (K/E).
        result.Status.Should().Be(TargetHashStatus.Missing);
        result.Hash.Should().BeNull();
    }

    [Fact]
    public async Task ComputeHashAsync_MissingDirectoryInPath_ReturnsMissing()
    {
        var path = Path.Combine(_dir, "no-such-dir", "target.exe");

        var result = await _service.ComputeHashAsync(path);

        // A missing intermediate directory is also a clean "no file" outcome.
        result.Status.Should().Be(TargetHashStatus.Missing);
        result.Hash.Should().BeNull();
    }

    [Fact]
    public async Task ComputeHashAsync_DirectoryAsFile_ReturnsReadError()
    {
        // Opening a directory as a file throws UnauthorizedAccessException on Windows:
        // a permission/IO failure is a distinct ReadError, not Missing and not Mismatch.
        var dirPath = Path.Combine(_dir, "adir");
        Directory.CreateDirectory(dirPath);

        var result = await _service.ComputeHashAsync(dirPath);

        result.Status.Should().Be(TargetHashStatus.ReadError);
        result.Hash.Should().BeNull();
    }
}
