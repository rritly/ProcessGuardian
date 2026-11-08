using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Infrastructure.Autostart;
using ProcessGuardian.Infrastructure.Configuration;
using ProcessGuardian.Infrastructure.Security;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// R-AS-XML-IMPORT-SECURITY regression tests: the elevated Task Scheduler definition XML is never
// staged in an interactive-user-writable location. It is staged inside the protected elevated
// data root (%ProgramData%\{AppEdition.DataDirName} — the same root as JsonConfigurationStore),
// created exclusively with its born-protected security descriptor supplied at creation
// (CREATE_NEW, protected DACL, Administrators owner, Administrators + SYSTEM FullControl only),
// re-classified as trusted after the handle is closed and before the elevated schtasks.exe opens
// it, and deleted in a finally block by the invocation that owns it. Every gate is fail closed.
//
// The staging boundary runs through the IProtectedTaskXmlStagingStore seam; these tests validate
// the control flow and the REQUESTED security operations (descriptor contents, exclusive create,
// classification gates, ownership-scoped cleanup) — they do NOT prove a real Windows ACL/owner.
// The live elevated owner/DACL behavior remains a manual elevated Windows acceptance check.
public class ScheduledTaskAutostartStagingTests
{
    // A realistic task export with controllable values (same shape as the other autostart suites)
    // so /Query /XML results can be scripted. Defaults match the expected autostart task; an
    // override simulates a Misconfigured registration.
    private static string TaskXml(
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
                  <Command>"{command ?? ExpectedCommand()}"</Command>
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

    private static string ExpectedCommand() =>
        Process.GetCurrentProcess().MainModule?.FileName ?? "C:\\Unknown\\app.exe";

    private static ScheduledTaskAutostartService.SchTasksCommandResult Result(
        int exitCode, string stdout, string stderr, Exception? exception) =>
        new(exitCode, stdout, stderr, exception);

    // Fabricated staging boundary. Records every operation in call order and every requested
    // security artifact so the tests can assert the exact protocol production must follow.
    // Minimal capturing logger for the never-logs-the-XML assertion (all levels).
    private sealed class CapturingLogger : ILogger<ScheduledTaskAutostartService>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    // Builds a service with a scripted invoker that walks the full SetEnabledAsync(true) flow:
    // the pre-state read reports a battery-restricted (Misconfigured) registration so the create
    // path runs, and the post-create /Query /XML verification reports a compliant Enabled task.
    // Snapshots whether the staging file was already deleted when the verification query ran.
    private static (
        ScheduledTaskAutostartService Service,
        FakeStagingStore Store,
        List<string> InvokedArguments,
        CapturingLogger Logger,
        Func<bool> DeletedBeforeVerification) CreateSuccessHarness(FakeStagingStore? store = null)
    {
        store ??= new FakeStagingStore();
        var invoked = new List<string>();
        var logger = new CapturingLogger();
        bool deletedBeforeVerification = false;
        var queryCalls = 0;

        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                queryCalls++;
                if (queryCalls == 2)
                    deletedBeforeVerification = store.Operations.Contains("deleted");
                return System.Threading.Tasks.Task.FromResult(queryCalls == 1
                    ? Result(0, TaskXml(disallow: "true", stop: "true"), string.Empty, null)
                    : Result(0, TaskXml(), string.Empty, null));
            }

            invoked.Add(arguments);
            store.Operations.Add("invoked");
            return System.Threading.Tasks.Task.FromResult(Result(0, "SUCCESS", string.Empty, null));
        };

        var service = new ScheduledTaskAutostartService(logger, invoker, store);
        return (service, store, invoked, logger, () => deletedBeforeVerification);
    }

    // ------------------------------------------------------------------
    // Staging root derivation (R-AS-XML-IMPORT-SECURITY 1-2)
    // ------------------------------------------------------------------

    [Fact]
    public void ElevatedDataRoot_IsProgramDataRoot_SharedByConfigAndStager()
    {
        var root = ElevatedDataPaths.ElevatedRoot;

        root.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppEdition.DataDirName));
        Path.GetDirectoryName(root).Should().Be(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Path.GetFileName(root).Should().Be(AppEdition.DataDirName);
    }

    [Fact]
    public async Task Staging_RootIsTheSharedElevatedDataRoot_NeverTheUserTemp()
    {
        var (service, store, _, _, _) = CreateSuccessHarness();

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeTrue();
        store.RootChecked.Should().Be(ElevatedDataPaths.ElevatedRoot,
            "the staging root must be derived exactly like JsonConfigurationStore's elevated root");
        var createPath = store.Creates.Should().ContainSingle().Subject.Path;
        Path.GetDirectoryName(createPath).Should().Be(ElevatedDataPaths.ElevatedRoot,
            "the definition file lives directly inside the protected root");
        createPath.Should().NotContain(Path.GetTempPath(),
            "an interactive-user-writable location is the security hole this closes");
        store.RootChecked.Should().NotContain(Path.GetTempPath());
    }

    // ------------------------------------------------------------------
    // Exclusive born-protected create (R-AS-XML-IMPORT-SECURITY 4)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Staging_RequestsBornProtectedDescriptor_ExclusiveCreate()
    {
        var (service, store, _, _, _) = CreateSuccessHarness();

        (await service.SetEnabledAsync(true)).Succeeded.Should().BeTrue();

        var create = store.Creates.Should().ContainSingle().Subject;
        create.Security.Should().NotBeNull();

        // The requested descriptor must already be the born-protected model: protected DACL
        // (inheritance disabled), Administrators owner, Administrators + SYSTEM FullControl only.
        create.Security.AreAccessRulesProtected.Should().BeTrue("the DACL must be protected from inheritance");
        var owner = create.Security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        owner.Should().NotBeNull("the descriptor owner must resolve");
        owner!.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null))
            .Should().BeTrue("the file owner must be BUILTIN\\Administrators from birth");
        ElevatedSecurity.ClassifyFileSystemSecurity(create.Security)
            .Should().Be(ProtectionClassification.Trusted);
    }

    [Fact]
    public async Task Staging_ContentIsUtf16LeWithPreamble_MatchingUtf16Declaration()
    {
        var (service, store, _, _, _) = CreateSuccessHarness();

        (await service.SetEnabledAsync(true)).Succeeded.Should().BeTrue();

        var content = store.Creates.Should().ContainSingle().Subject.Content;

        // The staged bytes must be exactly what File.WriteAllTextAsync(Encoding.Unicode) used to
        // produce: the UTF-16 LE BOM followed by the definition text, whose declared encoding is
        // utf-16 — the parser and the byte stream stay consistent.
        var preamble = Encoding.Unicode.GetPreamble();
        content.Length.Should().BeGreaterThan(preamble.Length);
        content.AsSpan(0, preamble.Length).ToArray().Should().Equal(preamble);

        var decoded = Encoding.Unicode.GetString(content, preamble.Length, content.Length - preamble.Length);
        decoded.Should().Be(ScheduledTaskAutostartService.BuildTaskDefinitionXml(ExpectedCommand()));
        decoded.Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>");
    }

    // ------------------------------------------------------------------
    // Fail-closed gates (R-AS-XML-IMPORT-SECURITY 3)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Staging_RootMissing_FailsClosed_NeverInvokesSchTasks()
    {
        var store = new FakeStagingStore { RootExists = false };
        var (service, _, invoked, _, _) = CreateSuccessHarness(store);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Misconfigured);
        result.ErrorMessage.Should().Contain("staged securely");
        invoked.Should().BeEmpty("schtasks.exe must never run when staging is refused");
        store.Creates.Should().BeEmpty();
        store.Deleted.Should().BeEmpty("nothing was created, so nothing is cleaned up");
    }

    [Fact]
    public async Task Staging_RootIsReparsePoint_FailsClosed_NeverInvokesSchTasks()
    {
        var store = new FakeStagingStore { RootIsReparse = true };
        var (service, _, invoked, _, _) = CreateSuccessHarness(store);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        invoked.Should().BeEmpty();
        store.Creates.Should().BeEmpty();
        store.Deleted.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProtectionClassification.Untrusted)]
    [InlineData(ProtectionClassification.UnsafeLocation)]
    public async Task Staging_RootNotTrusted_FailsClosed_NeverInvokesSchTasks(
        ProtectionClassification classification)
    {
        var store = new FakeStagingStore { RootClassification = classification };
        var (service, _, invoked, _, _) = CreateSuccessHarness(store);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not classified as trusted");
        invoked.Should().BeEmpty();
        store.Creates.Should().BeEmpty();
        store.Deleted.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Collision semantics (R-AS-XML-IMPORT-SECURITY 4-5)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Staging_CreateCollision_NeverOverwritesPreExistingFile_NeverInvokesSchTasks()
    {
        var store = new FakeStagingStore { FailCreate = true };
        var (service, _, invoked, _, _) = CreateSuccessHarness(store);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("could not be created");
        invoked.Should().BeEmpty("a collision must never lead to an elevated import");
        store.Creates.Should().BeEmpty("CREATE_NEW never overwrites");

        // Ownership: the current invocation created nothing, so the cleanup must never run —
        // the pre-existing sentinel file remains untouched at its original path.
        store.SentinelPath.Should().NotBeNull();
        store.SentinelContent.Should().Be("ORIGINAL", "the pre-existing file must remain unchanged");
        store.Deleted.Should().BeEmpty("the pre-existing object is never deleted by this invocation");
    }

    // ------------------------------------------------------------------
    // Post-create classification and ordering (R-AS-XML-IMPORT-SECURITY 4)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Staging_FileNotTrustedAfterClose_FailsClosed_DeletesOwnedFile()
    {
        var store = new FakeStagingStore { FileClassification = ProtectionClassification.Untrusted };
        var (service, _, invoked, _, _) = CreateSuccessHarness(store);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not classified as trusted");
        invoked.Should().BeEmpty("an untrusted staged file is never handed to schtasks.exe");
        store.Deleted.Should().ContainSingle("the file this invocation created is still cleaned up");
        store.Deleted[0].Should().Be(store.Creates[0].Path);
    }

    [Fact]
    public async Task Staging_OperationOrder_CreatedThenClassifiedThenInvokedThenDeleted()
    {
        var (service, store, _, _, deletedBeforeVerification) = CreateSuccessHarness();

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeTrue();
        store.Operations.Should().Equal(
            "root-checked", "root-classified", "created", "file-classified", "invoked", "deleted");

        // The cleanup has already completed before the post-create /Query /XML verification runs.
        deletedBeforeVerification().Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Cleanup on every outcome, never masking the primary result
    // ------------------------------------------------------------------

    [Fact]
    public async Task Staging_CreateInvocationFailure_StillDeletesStagedFile()
    {
        var store = new FakeStagingStore();

        ScheduledTaskAutostartService.SchTasksInvoker failingInvoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
                return System.Threading.Tasks.Task.FromResult(
                    Result(0, TaskXml(disallow: "true", stop: "true"), string.Empty, null));
            return System.Threading.Tasks.Task.FromResult(Result(
                -1, string.Empty, string.Empty, new InvalidOperationException("adapter fault")));
        };
        var service = new ScheduledTaskAutostartService(
            NullLogger<ScheduledTaskAutostartService>.Instance, failingInvoker, store);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        store.Deleted.Should().ContainSingle("cleanup runs on an invocation failure too");
    }

    [Fact]
    public async Task Staging_CreateNonZeroExit_StillDeletesStagedFile()
    {
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
                return System.Threading.Tasks.Task.FromResult(
                    Result(0, TaskXml(disallow: "true", stop: "true"), string.Empty, null));
            return System.Threading.Tasks.Task.FromResult(Result(2, string.Empty, "ERROR", null));
        };
        var store = new FakeStagingStore();
        var service = new ScheduledTaskAutostartService(
            NullLogger<ScheduledTaskAutostartService>.Instance, invoker, store);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        store.Deleted.Should().ContainSingle("cleanup runs on a nonzero /Create exit too");
    }

    [Fact]
    public async Task Staging_CreateCancellation_StillDeletesStagedFile()
    {
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
                return System.Threading.Tasks.Task.FromResult(
                    Result(0, TaskXml(disallow: "true", stop: "true"), string.Empty, null));
            throw new OperationCanceledException();
        };
        var store = new FakeStagingStore();
        var service = new ScheduledTaskAutostartService(
            NullLogger<ScheduledTaskAutostartService>.Instance, invoker, store);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.SetEnabledAsync(true));

        store.Deleted.Should().ContainSingle("cleanup runs on cancellation alike");
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(SecurityException))]
    public async Task Staging_CleanupFailure_NeverMasksThePrimaryOutcome(Type cleanupException)
    {
        // Success path: the create/verify result survives a failing cleanup untouched.
        var successStore = new FakeStagingStore
        {
            DeleteException = (Exception)Activator.CreateInstance(cleanupException)!,
        };
        var (service, _, _, _, _) = CreateSuccessHarness(successStore);
        (await service.SetEnabledAsync(true)).Succeeded.Should().BeTrue();

        // Refusal path: the StagingFailed outcome survives a failing cleanup untouched too.
        var refusalStore = new FakeStagingStore
        {
            FileClassification = ProtectionClassification.Untrusted,
            DeleteException = (Exception)Activator.CreateInstance(cleanupException)!,
        };
        var (refusalService, _, _, _, _) = CreateSuccessHarness(refusalStore);
        var refusal = await refusalService.SetEnabledAsync(true);

        refusal.Succeeded.Should().BeFalse();
        refusal.ErrorMessage.Should().Contain("not classified as trusted",
            "the primary staging refusal, not the cleanup error, is reported");
    }

    // ------------------------------------------------------------------
    // No full task XML in logs (R-AS-XML-IMPORT-SECURITY 7)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Staging_FullTaskXmlIsNeverLogged()
    {
        var xml = BuildDefinition();
        var (service, _, _, logger, _) = CreateSuccessHarness();

        await service.SetEnabledAsync(true);

        logger.Messages.Should().NotBeEmpty();
        logger.Messages.Should().NotContain(m => m.Contains("<Task", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m => m.Contains("RunLevel", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m => m.Contains(xml, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Staging_RefusalPathsNeverLogTheTaskXml()
    {
        var xml = BuildDefinition();
        var store = new FakeStagingStore { FileClassification = ProtectionClassification.Untrusted };
        var (service, _, _, logger, _) = CreateSuccessHarness(store);

        await service.SetEnabledAsync(true);

        logger.Messages.Should().NotContain(m => m.Contains("<Task", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m => m.Contains(xml, StringComparison.Ordinal));
    }

    // The exact definition text production must produce (content invariance is asserted by the
    // dedicated byte test below and the existing XML suites); used only to prove it never leaks.
    private static string BuildDefinition() =>
        ScheduledTaskAutostartService.BuildTaskDefinitionXml(ExpectedCommand());
}
