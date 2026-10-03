using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

        var hash = await _service.ComputeHashAsync(path);

        // Well-known SHA-256 of "abc".
        hash.Should().Be("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
    }

    [Fact]
    public async Task ComputeHashAsync_IsDeterministic()
    {
        var path = Path.Combine(_dir, "target.exe");
        await File.WriteAllTextAsync(path, "stable content");

        var first = await _service.ComputeHashAsync(path);
        var second = await _service.ComputeHashAsync(path);

        first.Should().NotBeNullOrEmpty();
        second.Should().Be(first);
    }

    [Fact]
    public async Task ComputeHashAsync_MissingFile_ReturnsNull()
    {
        var hash = await _service.ComputeHashAsync(Path.Combine(_dir, "missing.exe"));

        hash.Should().BeNull();
    }
}
