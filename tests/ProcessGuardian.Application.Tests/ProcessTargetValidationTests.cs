using FluentAssertions;
using ProcessGuardian.Domain.ValueObjects;
using Xunit;

namespace ProcessGuardian.Application.Tests;

// Validation gate for monitored targets: only existing local-drive .exe files
// may ever reach the orchestrator's launch path.
public class ProcessTargetValidationTests : IDisposable
{
    private readonly string _dir;

    public ProcessTargetValidationTests()
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

    private string CreateTargetFile(string name = "target.exe")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespacePath_Rejected(string path)
    {
        var ok = ProcessTarget.TryCreate(path, out var target, out var error);

        ok.Should().BeFalse();
        target.Should().BeNull();
        error.Should().Be("path is empty");
    }

    [Fact]
    public void RelativePath_Rejected()
    {
        var ok = ProcessTarget.TryCreate("App\\target.exe", out var target, out var error);

        ok.Should().BeFalse();
        target.Should().BeNull();
        error.Should().Be("path is not absolute");
    }

    [Fact]
    public void NonExeExtension_Rejected()
    {
        var path = Path.Combine(_dir, "target.bat");
        File.WriteAllText(path, string.Empty);

        var ok = ProcessTarget.TryCreate(path, out var target, out var error);

        ok.Should().BeFalse();
        target.Should().BeNull();
        error.Should().Contain(".exe");
    }

    [Fact]
    public void UncPath_Rejected()
    {
        var ok = ProcessTarget.TryCreate(@"\\server\share\app.exe", out var target, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("local drive");
    }

    [Fact]
    public void DevicePath_Rejected()
    {
        var ok = ProcessTarget.TryCreate(@"\\?\C:\Windows\notepad.exe", out var target, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("local drive");
    }

    [Fact]
    public void MissingFile_Rejected()
    {
        var path = Path.Combine(_dir, "missing.exe");

        var ok = ProcessTarget.TryCreate(path, out var target, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("does not exist");
    }

    [Fact]
    public void ValidPath_CreatesTarget_WithDerivedNameAndDirectory()
    {
        var path = CreateTargetFile("my tool.exe");

        var ok = ProcessTarget.TryCreate(path, out var target, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        target.Should().NotBeNull();
        target!.FullPath.Should().Be(path);
        target.NameWithoutExtension.Should().Be("my tool");
        target.WorkingDirectory.Should().Be(_dir);
    }

    [Fact]
    public void ValidPath_ExtensionCaseInsensitive()
    {
        var path = CreateTargetFile("upper.EXE");

        var ok = ProcessTarget.TryCreate(path, out var target, out _);

        ok.Should().BeTrue();
        target!.NameWithoutExtension.Should().Be("upper");
    }
}
