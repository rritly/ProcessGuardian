using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;

namespace ProcessGuardian.Infrastructure.Autostart;

// HKCU Run keys cannot auto-elevate (UAC would prompt on every logon). The admin
// edition therefore registers a scheduled task (ONLOGON, RunLevel HIGHEST), which
// starts elevated silently. Created via schtasks.exe: no COM/WMI dependency.
public sealed class ScheduledTaskAutostartService : IAutostartService
{
    // Task Scheduler 2.0 export XML namespace.
    internal static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private readonly ILogger<ScheduledTaskAutostartService> _logger;
    private readonly SchTasksInvoker? _invoker;

    public ScheduledTaskAutostartService(ILogger<ScheduledTaskAutostartService> logger)
        : this(logger, null)
    {
    }

    // Internal seam so CI can script schtasks.exe results (exit code, output, exceptions)
    // without touching the real scheduled-task store; production leaves it null and runs
    // the real process.
    internal ScheduledTaskAutostartService(ILogger<ScheduledTaskAutostartService> logger, SchTasksInvoker? invoker)
    {
        _logger = logger;
        _invoker = invoker;
    }

    internal delegate Task<(int ExitCode, string Output)> SchTasksInvoker(string arguments, CancellationToken ct);

    // The expected <Command> of the autostart task: the current executable. A task that points
    // at a different (e.g. moved, old) install path is Misconfigured, not Enabled.
    private static string ExpectedCommand =>
        Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    public async Task<AutostartState> ReadStateAsync(CancellationToken ct = default)
    {
        var (exitCode, output) = await RunSchTasksAsync(BuildQueryXmlArguments(), ct).ConfigureAwait(false);
        if (exitCode == 0)
        {
            var config = ParseTaskXml(output);
            if (config is null)
            {
                _logger.LogWarning("Autostart task query succeeded but the task XML could not be parsed");
                return AutostartState.Unknown;
            }

            return Evaluate(config.Value, ExpectedCommand);
        }

        // A nonzero /Query exit code is NOT proof the task is absent: schtasks returns nonzero
        // for a missing task, for access-denied, and for other query failures alike. Only a
        // clean "task not found" result is Absent; every other failure is Unknown so the caller
        // can never mistake a read error for a verified "off" registration.
        if (LooksLikeMissingTask(output))
            return AutostartState.Absent;

        _logger.LogWarning(
            "Autostart task query failed (exit {Code}) and the cause is not a clean missing task; state cannot be verified",
            exitCode);
        return AutostartState.Unknown;
    }

    public async Task<AutostartResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        // Idempotent fast path over the typed state: only an already-Enabled task short-circuits
        // enable; only a reliably Absent registration short-circuits disable. Disabled /
        // Misconfigured / other registrations are repaired (re-created with /F) or removed —
        // never assumed already in the desired state.
        var state = await ReadStateAsync(ct).ConfigureAwait(false);
        if (state == AutostartState.Unknown)
        {
            _logger.LogWarning("Cannot set autostart={Enabled}: the current state could not be read reliably", enabled);
            return AutostartResult.Failure(
                "the current autostart state could not be read reliably; no change was made",
                AutostartState.Unknown);
        }

        if (enabled)
        {
            if (state == AutostartState.Enabled)
                return AutostartResult.Success(AutostartState.Enabled);

            var exePath = ExpectedCommand;
            // An empty path would create a task pointing nowhere.
            if (string.IsNullOrWhiteSpace(exePath))
            {
                _logger.LogError("Cannot enable autostart: current executable path is unavailable");
                return AutostartResult.Failure("the current executable path is unavailable for the autostart task", state);
            }

            var (code, output) = await RunSchTasksAsync(BuildCreateArguments(exePath), ct).ConfigureAwait(false);
            if (code != 0)
            {
                // A nonzero exit code is an operation error; the diagnostic must not be lost.
                _logger.LogError("Failed to create autostart task (exit {Code}): {Output}", code, output);
                return AutostartResult.Failure($"task creation failed (exit {code}): {output}", state);
            }
        }
        else
        {
            if (state == AutostartState.Absent)
                return AutostartResult.Success(AutostartState.Absent);

            var (deleteCode, deleteOutput) = await RunSchTasksAsync(BuildDeleteArguments(), ct).ConfigureAwait(false);
            if (deleteCode != 0)
            {
                _logger.LogError("Failed to delete autostart task (exit {Code}): {Output}", deleteCode, deleteOutput);
                return AutostartResult.Failure($"task deletion failed (exit {deleteCode}): {deleteOutput}", state);
            }
        }

        // Verify the actual registration state rather than trusting the exit code alone: a
        // command that exits 0 without the state taking effect is still a failure.
        var actual = await ReadStateAsync(ct).ConfigureAwait(false);
        var expected = enabled ? AutostartState.Enabled : AutostartState.Absent;
        if (actual == expected)
            return AutostartResult.Success(actual);
        if (actual == AutostartState.Unknown)
            return AutostartResult.Failure(
                "the operation completed but the resulting autostart state could not be verified",
                AutostartState.Unknown);

        _logger.LogError("Autostart state ({Actual}) does not match the requested state ({Requested}) after the operation",
            actual, expected);
        return AutostartResult.Failure(
            enabled
                ? "the autostart task is not correctly registered after the create exited successfully"
                : "the autostart task is still registered after the delete exited successfully",
            actual);
    }

    // ---------------------------------------------------------------------
    // Typed configuration evaluation (machine-readable task XML, not localized text parsing)
    // ---------------------------------------------------------------------

    /// <summary>The parsed, machine-readable shape of the registered task definition.</summary>
    internal readonly record struct TaskDescriptor(
        string? Command,
        string? Arguments,
        bool HasLogonTrigger,
        string? LogonType,
        string? RunLevel,
        bool? Enabled);

    // Parses the /XML export of a single task. Returns null (Unknown) when the XML cannot be
    // interpreted — a task we cannot describe is a task we cannot call "enabled".
    internal static TaskDescriptor? ParseTaskXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var task = doc.Root;
        if (task is null || task.Name.LocalName != "Task")
            return null;

        string? Command = null, Arguments = null, LogonType = null, RunLevel = null;
        bool? Enabled = null;
        var hasLogonTrigger = false;

        XElement? FindChild(XElement parent, string localName) =>
            parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

        foreach (var triggers in task.Elements().Where(e => e.Name.LocalName == "Triggers"))
            hasLogonTrigger = triggers.Elements().Any(e => e.Name.LocalName == "LogonTrigger");

        foreach (var actions in task.Elements().Where(e => e.Name.LocalName == "Actions"))
        {
            var exec = actions.Elements().FirstOrDefault(e => e.Name.LocalName == "Exec");
            if (exec is not null)
            {
                Command = FindChild(exec, "Command")?.Value?.Trim();
                Arguments = FindChild(exec, "Arguments")?.Value?.Trim();
            }
        }

        foreach (var principals in task.Elements().Where(e => e.Name.LocalName == "Principals"))
        {
            var principal = principals.Elements().FirstOrDefault(e => e.Name.LocalName == "Principal");
            if (principal is not null)
            {
                LogonType = FindChild(principal, "LogonType")?.Value?.Trim();
                RunLevel = FindChild(principal, "RunLevel")?.Value?.Trim();
            }
        }

        foreach (var settings in task.Elements().Where(e => e.Name.LocalName == "Settings"))
        {
            var enabledEl = FindChild(settings, "Enabled");
            if (enabledEl is not null)
            {
                if (bool.TryParse(enabledEl.Value, out var en))
                    Enabled = en;
            }
        }

        return new TaskDescriptor(Command, Arguments, hasLogonTrigger, LogonType, RunLevel, Enabled);
    }

    /// <summary>
    /// Classifies a parsed task definition against the expected autostart configuration.
    /// Misconfigured wins over Disabled: any parameter mismatch means the registration cannot
    /// be trusted as the correct autostart entry, regardless of its enabled flag.
    /// </summary>
    internal static AutostartState Evaluate(TaskDescriptor d, string expectedCommand)
    {
        var misconfigured =
            string.IsNullOrWhiteSpace(d.Command)
            || !string.Equals(d.Command, expectedCommand, StringComparison.OrdinalIgnoreCase)
            || !(d.Arguments?.Contains("--background", StringComparison.OrdinalIgnoreCase) ?? false)
            || !d.HasLogonTrigger
            || !string.Equals(d.LogonType, "InteractiveToken", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(d.RunLevel, "HighestAvailable", StringComparison.OrdinalIgnoreCase);

        if (misconfigured)
            return AutostartState.Misconfigured;

        if (d.Enabled is null)
        {
            // The task definition is present but its enabled flag could not be read: we must
            // not claim a verified Enabled state (nor silently treat it as disabled).
            return AutostartState.Unknown;
        }

        return d.Enabled.Value ? AutostartState.Enabled : AutostartState.Disabled;
    }

    /// <summary>
    /// Distinguishes a clean "task not found" query failure from access/permission and other
    /// query failures. schtasks writes the OS ERROR_FILE_NOT_FOUND text for a missing task; any
    /// other nonzero-query failure (access denied, service error) is NOT a missing task and must
    /// surface as Unknown. Both the invariant English marker and the most common localized
    /// variants are matched; the safe default is "not missing".
    /// </summary>
    internal static bool LooksLikeMissingTask(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;
        var t = output;
        return t.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase)
            || t.Contains("can't find the file", StringComparison.OrdinalIgnoreCase)
            || t.Contains("system cannot find the file specified", StringComparison.OrdinalIgnoreCase)
            || t.Contains("не удается найти указанный файл", StringComparison.OrdinalIgnoreCase)
            || t.Contains("не удалось найти указанный файл", StringComparison.OrdinalIgnoreCase)
            || t.Contains("指定的文件找不到", StringComparison.OrdinalIgnoreCase)
            || t.Contains("指定されたファイルが見つかりません", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildCreateArguments(string exePath) =>
        $"/Create /TN \"{AppEdition.ScheduledTaskName}\" /TR \"\\\"{exePath}\\\" --background\" /SC ONLOGON /RL HIGHEST /F";

    internal static string BuildDeleteArguments() =>
        $"/Delete /TN \"{AppEdition.ScheduledTaskName}\" /F";

    internal static string BuildQueryArguments() =>
        $"/Query /TN \"{AppEdition.ScheduledTaskName}\"";

    // Machine-readable task definition export: parsed, never localization-dependent.
    internal static string BuildQueryXmlArguments() =>
        $"/Query /TN \"{AppEdition.ScheduledTaskName}\" /XML";

    // Defeat a bare-name resolution that a hostile PATH entry could hijack: schtasks.exe
    // is always invoked through its absolute %SystemRoot%\System32 path.
    internal static string ResolveSchTasksPath() =>
        Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    // Internal seam so tests can assert the launched FileName is the absolute System32
    // path, not a bare "schtasks.exe" resolved through the process PATH.
    internal static ProcessStartInfo BuildProcessStartInfo(string arguments) =>
        new(ResolveSchTasksPath(), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

    // Runs schtasks.exe through the injected seam when present, otherwise the real process.
    // Both paths share the same exception handling so a seam-thrown error behaves exactly like
    // a real process-start failure (surfaced as a failure result, never lost).
    private async Task<(int ExitCode, string Output)> RunSchTasksAsync(string arguments, CancellationToken ct)
    {
        try
        {
            if (_invoker is not null)
                return await _invoker(arguments, ct).ConfigureAwait(false);

            var psi = BuildProcessStartInfo(arguments);
            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogError("schtasks.exe could not be started");
                return (-1, string.Empty);
            }

            // Read both streams concurrently: a sequential read can deadlock if
            // schtasks fills the stderr pipe buffer while stdout is still open.
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, $"{outputTask.Result} {errorTask.Result}".Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A process-start/invocation failure is surfaced as a failure result, never lost:
            // the caller must be able to distinguish "operation failed" from "operation ok".
            _logger.LogError(ex, "schtasks.exe invocation failed");
            return (-1, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
