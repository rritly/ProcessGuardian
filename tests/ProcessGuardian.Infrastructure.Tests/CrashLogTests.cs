using System.IO;
using FluentAssertions;
using ProcessGuardian.Infrastructure.Logging;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

public class CrashLogTests : IDisposable
{
    private readonly string _dir;

    public CrashLogTests()
    {
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
    public void Append_CreatesDirectoryAndFile_WritesMessage()
    {
        var dir = Path.Combine(_dir, "logs");

        CrashLog.Append(dir, "crash.log", "[t] boom\n");

        var path = Path.Combine(dir, "crash.log");
        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Contain("[t] boom");
    }

    [Fact]
    public void Append_CalledTwice_AppendsBothEntries()
    {
        CrashLog.Append(_dir, "crash.log", "first\n");
        CrashLog.Append(_dir, "crash.log", "second\n");

        var content = File.ReadAllText(Path.Combine(_dir, "crash.log"));
        content.Should().Contain("first");
        content.Should().Contain("second");
    }

    [Fact]
    public void Append_WhenFileExceedsLimit_RotatesAndStartsFresh()
    {
        var path = Path.Combine(_dir, "crash.log");
        var oversized = new string('x', 200);
        File.WriteAllText(path, oversized);

        CrashLog.Append(_dir, "crash.log", "fresh entry", maxBytes: 16);

        File.ReadAllText(path).Should().Be("fresh entry");
    }

    [Fact]
    public void Append_UnderLimit_KeepsExistingContent()
    {
        CrashLog.Append(_dir, "crash.log", "first\n", maxBytes: 1024);
        CrashLog.Append(_dir, "crash.log", "second\n", maxBytes: 1024);

        var content = File.ReadAllText(Path.Combine(_dir, "crash.log"));
        content.Should().Contain("first");
        content.Should().Contain("second");
    }
}
