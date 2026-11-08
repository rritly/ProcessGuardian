using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using ProcessGuardian.Infrastructure.Autostart;
using ProcessGuardian.Infrastructure.Security;

namespace ProcessGuardian.Infrastructure.Tests;

/// <summary>
/// A realistic task-definition export shared by the autostart suites. Defaults match the
/// expected autostart task; overrides simulate a specific (Misconfigured) registration.
/// </summary>
internal static class ScheduledTaskTestXml
{
    public static string TaskXml(
        string? command = null,
        string arguments = "--background",
        string? disallow = "false",
        string? stop = "false",
        string? enabled = "true")
    {
        var disallowEl = disallow is null
            ? string.Empty
            : $"<DisallowStartIfOnBatteries>{disallow}</DisallowStartIfOnBatteries>";
        var stopEl = stop is null
            ? string.Empty
            : $"<StopIfGoingOnBatteries>{stop}</StopIfGoingOnBatteries>";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger />
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>DOMAIN\\user</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Actions Context="Author">
                <Exec>
                  <Command>"{command ?? Process.GetCurrentProcess().MainModule?.FileName ?? "C:\\Unknown\\app.exe"}"</Command>
                  <Arguments>{arguments}</Arguments>
                </Exec>
              </Actions>
              <Settings>
                {disallowEl}
                {stopEl}
                <Enabled>{enabled}</Enabled>
              </Settings>
            </Task>
            """;
    }
}

/// <summary>
/// Shared fabricated <see cref="IProtectedTaskXmlStagingStore"/> for the autostart suites
/// (R-AS-XML-IMPORT-SECURITY). Records every operation in call order and every requested
/// security artifact so the tests can assert the exact protocol production must follow. No
/// live elevated ACL is required (CI runs non-elevated) — the real owner/DACL behavior stays
/// a manual elevated Windows acceptance check.
/// </summary>
internal sealed class FakeStagingStore : IProtectedTaskXmlStagingStore
{
    public bool RootExists = true;
    public bool RootIsReparse;
    public ProtectionClassification RootClassification = ProtectionClassification.Trusted;
    public ProtectionClassification FileClassification = ProtectionClassification.Trusted;
    public bool FailCreate;
    public Exception? DeleteException;

    public List<string> Operations { get; } = new();
    public string? RootChecked { get; private set; }
    public List<(string Path, FileSecurity Security, byte[] Content)> Creates { get; } = new();
    public List<(string Path, bool IsDirectory)> Classifications { get; } = new();
    public List<string> Deleted { get; } = new();

    // Collision sentinel: on a simulated exclusive-create refusal a pre-existing object is
    // recorded at the requested path; nothing in production is allowed to delete or modify it.
    public string? SentinelPath { get; private set; }
    public string SentinelContent { get; private set; } = "ORIGINAL";

    public bool ExistsAndIsNotReparse(string directoryPath)
    {
        Operations.Add("root-checked");
        RootChecked = directoryPath;
        return RootExists && !RootIsReparse;
    }

    public ProtectionClassification Classify(string path, bool isDirectory)
    {
        Operations.Add(isDirectory ? "root-classified" : "file-classified");
        Classifications.Add((path, isDirectory));
        return isDirectory ? RootClassification : FileClassification;
    }

    public void CreateExclusiveProtected(string path, FileSecurity security, byte[] content, out bool created)
    {
        Operations.Add("created");
        if (FailCreate)
        {
            // Simulated CREATE_NEW collision: the pre-existing sentinel is never overwritten.
            created = false;
            SentinelPath = path;
            throw new Win32Exception(183, "simulated CREATE_NEW collision");
        }
        Creates.Add((path, security, content));
        created = true;
    }

    public void Delete(string path)
    {
        Operations.Add("deleted");
        Deleted.Add(path);
        if (DeleteException is not null)
            throw DeleteException;
    }
}
