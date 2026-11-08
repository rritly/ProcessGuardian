using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Configuration;
using ProcessGuardian.Infrastructure.Security;

namespace ProcessGuardian.Infrastructure.Autostart;

// HKCU Run keys cannot auto-elevate (UAC would prompt on every logon). The admin
// edition therefore registers a scheduled task (LogonTrigger, RunLevel HIGHEST), which
// starts elevated silently. Registered via "schtasks.exe /Create /XML" import (no COM/WMI
// dependency): the /Create command line has no switches for the Task Settings power
// restrictions, so the imported definition explicitly sets both battery flags to false.
public sealed class ScheduledTaskAutostartService : IAutostartService
{
    // Task Scheduler 2.0 export XML namespace.
    internal static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private readonly ILogger<ScheduledTaskAutostartService> _logger;
    private readonly SchTasksInvoker? _invoker;
    private readonly IProtectedTaskXmlStagingStore _staging;

    public ScheduledTaskAutostartService(ILogger<ScheduledTaskAutostartService> logger)
        : this(logger, null)
    {
    }

    // Internal seam so CI can script schtasks.exe results (exit code, stdout, stderr, invocation
    // exception) without touching the real scheduled-task store, and so the secure staging
    // boundary (R-AS-XML-IMPORT-SECURITY) can be tested without a live elevated ACL. Production
    // leaves both null and runs the real process against real protected storage.
    internal ScheduledTaskAutostartService(
        ILogger<ScheduledTaskAutostartService> logger,
        SchTasksInvoker? invoker,
        IProtectedTaskXmlStagingStore? stagingStore = null)
    {
        _logger = logger;
        _invoker = invoker;
        _staging = stagingStore ?? new Win32TaskXmlStagingStore();
    }

    internal delegate Task<SchTasksCommandResult> SchTasksInvoker(string arguments, CancellationToken ct);

    /// <summary>
    /// A single schtasks.exe invocation outcome that keeps exit code, stdout and stderr separate
    /// so they are never collapsed before diagnostic recording/classification (v2.5 R-AS-DIAG).
    /// A non-null <see cref="InvocationException"/> means the executable could not be started at
    /// all; it is a failure result, never a silent throw.
    /// </summary>
    internal readonly record struct SchTasksCommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        Exception? InvocationException,
        string? StagingFailure = null)
    {
        // Per-stream cap for diagnostic captures so a hostile or huge output can never flood the
        // log. A full exported task XML is never logged, even when bounded.
        internal const int MaxStreamChars = 4096;

        public bool InvocationFailed => InvocationException is not null;

        // Secure-staging refusal (R-AS-XML-IMPORT-SECURITY): the definition was never staged, so
        // schtasks.exe was never invoked and nothing was registered. Carries the refusal reason
        // verbatim for the failure report; distinct from InvocationFailed, which stays exclusively
        // for a real schtasks.exe start/invocation error.
        public bool StagingFailed => StagingFailure is not null;

        // Factory for a secure-staging refusal outcome: schtasks.exe was never invoked, both
        // streams are empty, and the reason travels with the result.
        internal static SchTasksCommandResult SecureStagingRefused(string reason) =>
            new(0, string.Empty, string.Empty, null, reason);

        // Combined text used ONLY for classification (the "not found"/"access denied"/"service
        // unavailable" markers can be written to either stdout or stderr). The raw streams are
        // kept separate for diagnostic capture and logging; this does not merge the stored values.
        public string Text => $"{StandardOutput}{Environment.NewLine}{StandardError}".Trim();

        // Bounded diagnostic text (each stream capped independently) for failure messages.
        public string BoundedText
        {
            get
            {
                var stdout = Bound(StandardOutput);
                var stderr = Bound(StandardError);
                if (string.IsNullOrEmpty(stdout))
                    return stderr;
                if (string.IsNullOrEmpty(stderr))
                    return stdout;
                return $"{stdout} {stderr}";
            }
        }

        // Display-only normalization (R-AS-TASK-SCHEDULER-DIAGNOSTICS): surrounding
        // whitespace/CR/LF is trimmed BEFORE the cap so log lines never end in raw CR/LF;
        // internal text is preserved. The raw StandardOutput/StandardError used for
        // classification stay untouched.
        internal static string Bound(string? value, int maxChars = MaxStreamChars)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            var trimmed = value.Trim();
            return trimmed.Length <= maxChars
                ? trimmed
                : trimmed[..maxChars] + $"...[truncated at {maxChars}]";
        }
    }

    /// <summary>Distinct, fail-closed classifications of a nonzero command result.</summary>
    internal enum SchTasksQueryFailure
    {
        None,
        InvocationFailure,
        MissingTask,
        AccessDenied,
        ServiceUnavailable,
        Unknown,
    }

    internal static SchTasksQueryFailure ClassifyQueryResult(SchTasksCommandResult result)
    {
        if (result.InvocationFailed)
            return SchTasksQueryFailure.InvocationFailure;
        if (result.ExitCode == 0)
            return SchTasksQueryFailure.None;
        if (LooksLikeMissingTask(result.Text))
            return SchTasksQueryFailure.MissingTask;
        if (LooksLikeAccessDenied(result.Text))
            return SchTasksQueryFailure.AccessDenied;
        if (LooksLikeServiceUnavailable(result.Text))
            return SchTasksQueryFailure.ServiceUnavailable;
        return SchTasksQueryFailure.Unknown;
    }

    // The expected <Command> of the autostart task: the current executable. A task that points
    // at a different (e.g. moved, old) install path is Misconfigured, not Enabled.
    private static string ExpectedCommand =>
        Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    public async Task<AutostartState> ReadStateAsync(CancellationToken ct = default)
    {
        var (state, _) = await ReadStateDetailedAsync(ct).ConfigureAwait(false);
        return state;
    }

    /// <summary>
    /// Reads the typed autostart state together with the parsed task descriptor. The descriptor
    /// lets callers surface a per-check diagnostic (which field failed, what was expected, what
    /// was actually found) instead of only the aggregate Misconfigured result.
    /// </summary>
    internal async Task<(AutostartState State, TaskDescriptor? Descriptor)> ReadStateDetailedAsync(CancellationToken ct = default)
    {
        var query = await RunSchTasksAsync(BuildQueryXmlArguments(), ct).ConfigureAwait(false);
        if (query.InvocationFailed)
        {
            LogCommandFailure("query-xml", query);
            return (AutostartState.Unknown, null);
        }

        if (query.ExitCode == 0)
        {
            var config = ParseTaskXml(query.StandardOutput, _logger);
            if (config is null)
            {
                // Exit 0 but the XML is unparsable/unverifiable: a task we cannot describe is a
                // task we cannot call enabled — fail-closed Unknown, never Absent.
                _logger.LogWarning(
                    "Autostart task query succeeded but the task XML could not be parsed; stderr: {Stderr:l}",
                    SchTasksCommandResult.Bound(query.StandardError));
                return (AutostartState.Unknown, null);
            }

            var state = Evaluate(config.Value, ExpectedCommand);
            if (state == AutostartState.Misconfigured)
                LogMismatches(GetMismatches(config.Value, ExpectedCommand), "read-state");
            return (state, config);
        }

        // A nonzero /Query exit code is NOT proof the task is absent: schtasks returns nonzero
        // for a missing task, for access-denied, and for other query failures alike. Only a clean
        // "task not found" result is Absent; access denied, an unavailable Task Scheduler
        // service, an invocation failure, malformed XML and any unknown cause all classify as
        // Unknown so the caller can never mistake a read error for a verified "off" registration
        // (v2.5 R-AS-DIAG). The precise cause is recorded to the log first, never lost.
        switch (ClassifyQueryResult(query))
        {
            case SchTasksQueryFailure.MissingTask:
                // A positively classified missing task is the ordinary Absent state, especially
                // on first install: routine Debug diagnostics, no raw localized stdout/stderr and
                // no Error event (R-AS-TASK-SCHEDULER-DIAGNOSTICS). The fail-closed rule is
                // unchanged — only the supported task-not-found response reaches this case, and
                // the actual raw streams stay in the SchTasksCommandResult for any genuine
                // failure diagnostic that later needs them.
                _logger.LogDebug("Autostart task is absent (exit {Code})", query.ExitCode);
                return (AutostartState.Absent, null);
            case SchTasksQueryFailure.AccessDenied:
                LogCommandFailure("query-xml", query);
                _logger.LogWarning("Autostart task query indicated access was denied; state cannot be verified");
                return (AutostartState.Unknown, null);
            case SchTasksQueryFailure.ServiceUnavailable:
                LogCommandFailure("query-xml", query);
                _logger.LogWarning("Autostart task query indicated the Task Scheduler service is unavailable; state cannot be verified");
                return (AutostartState.Unknown, null);
            default:
                LogCommandFailure("query-xml", query);
                _logger.LogWarning(
                    "Autostart task query failed (exit {Code}) and the cause is not a clean missing task; state cannot be verified",
                    query.ExitCode);
                return (AutostartState.Unknown, null);
        }
    }

    // Per-check diagnostic for a Misconfigured task: which field, what was expected, what was
    // found. The full exported XML is never logged; the command value is the executable path the
    // task points at, which the application already logs elsewhere for the same diagnostics.
    private void LogMismatches(IReadOnlyList<TaskMismatch> mismatches, string context)
    {
        foreach (var m in mismatches)
            _logger.LogError(
                "Autostart task mismatch ({Context:l}): check {Check:l}; expected {Expected:l}; actual {Actual:l}",
                context, m.Check, m.Expected, m.Actual);
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

        // Carried out of the branch so the disable path's provisional delete result can reach the
        // shared verification block below (R-AS-TASK-SCHEDULER-DIAGNOSTICS).
        SchTasksCommandResult? delete = null;
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

            var create = await CreateTaskAsync(exePath, ct).ConfigureAwait(false);
            if (create.StagingFailed)
            {
                // Secure staging refused (R-AS-XML-IMPORT-SECURITY): schtasks.exe was never
                // invoked and nothing was registered, so the post-create verification is skipped.
                // The refusal reason travels with the result for the report.
                _logger.LogError(
                    "Autostart enable failed: the task definition could not be staged securely ({Reason:l})",
                    create.StagingFailure);
                return AutostartResult.Failure(
                    $"task creation failed: the task definition could not be staged securely: {create.StagingFailure}",
                    state);
            }
            if (create.InvocationFailed)
            {
                LogCommandFailure("create", create);
                // The user-facing message stays concise English (R-AS-TASK-SCHEDULER-DIAGNOSTICS):
                // raw localized exception text stays in the bounded structured log above, never in
                // the ErrorMessage that MainViewModel displays as AutostartError.
                return AutostartResult.Failure("task creation failed: schtasks.exe could not be started", state);
            }
            if (create.ExitCode != 0)
            {
                LogCommandFailure("create", create);
                return AutostartResult.Failure($"task creation failed (exit {create.ExitCode})", state);
            }
        }
        else
        {
            if (state == AutostartState.Absent)
                return AutostartResult.Success(AutostartState.Absent);

            var result = await RunSchTasksAsync(BuildDeleteArguments(), ct).ConfigureAwait(false);
            delete = result;
            if (result.InvocationFailed)
            {
                LogCommandFailure("delete", result);
                return AutostartResult.Failure("task deletion failed: schtasks.exe could not be started", state);
            }
            if (result.ExitCode != 0)
            {
                // Provisional (R-AS-TASK-SCHEDULER-DIAGNOSTICS): a concurrent removal between the
                // pre-query and this delete is a benign race — the task may already be absent, so
                // the post-operation verification below decides, and a verified absence is an
                // idempotent success. Logged at Debug for now; a genuine failure re-logs the
                // bounded diagnostics at Error. A failure to invoke schtasks.exe remains an
                // invocation failure above.
                _logger.LogDebug(
                    "Autostart task delete reported nonzero (exit {Code}); verifying actual absence", result.ExitCode);
            }
        }

        // Verify the actual registration state rather than trusting the exit code alone: a
        // command that exits 0 without the state taking effect is still a failure, and a nonzero
        // delete that already achieved the desired absence is still an idempotent success.
        var (actual, descriptor) = await ReadStateDetailedAsync(ct).ConfigureAwait(false);
        var expected = enabled ? AutostartState.Enabled : AutostartState.Absent;
        if (actual == expected)
            return AutostartResult.Success(actual);
        if (actual == AutostartState.Unknown)
        {
            if (delete is { ExitCode: not 0 })
                LogCommandFailure("delete", delete.Value);
            return AutostartResult.Failure(
                "the operation completed but the resulting autostart state could not be verified",
                AutostartState.Unknown);
        }

        // For a Misconfigured task whose descriptor we could parse, log the exact failing check
        // with the expected and actual values — never the whole exported XML — so the cause of a
        // "create exited 0 but the task checked as Misconfigured" report can be pinned down.
        if (actual == AutostartState.Misconfigured && descriptor is { } d)
            LogMismatches(GetMismatches(d, ExpectedCommand), enabled ? "set-enabled=true" : "set-enabled=false");

        // A genuine failure re-logs the bounded delete diagnostics at Error (R-AS-TASK-SCHEDULER-
        // DIAGNOSTICS): the provisional Debug above is upgraded exactly once, only when the
        // desired postcondition was NOT achieved.
        if (!enabled && delete is { ExitCode: not 0 })
            LogCommandFailure("delete", delete.Value);
        _logger.LogError("Autostart state ({Actual}) does not match the requested state ({Requested}) after the operation",
            actual, expected);
        return AutostartResult.Failure(
            enabled
                ? "the autostart task is not correctly registered after the create exited successfully"
                : "the autostart task is still registered after the delete attempt",
            actual);
    }

    // Recovery primitive (v2.5 R-AS-AUTO): unconditional, verified removal of the app-owned
    // task. Unlike SetEnabledAsync(false), it does NOT gate the delete behind a pre-query: the
    // recovery policy is always "task absent", so the exact app-owned task name is deleted
    // directly, and only then is the actual state positively verified as Absent. A nonzero
    // delete is never proof of either outcome (the task may already be gone, access may be
    // denied): only a follow-up read that confirms Absent reports success. Unknown / still
    // present / Misconfigured all fail closed so the recovery stays pending. This method never
    // enables a registration and never touches anything but the exact app-owned task name.
    public async Task<AutostartResult> RemoveRegistrationAsync(CancellationToken ct = default)
    {
        var delete = await RunSchTasksAsync(BuildDeleteArguments(), ct).ConfigureAwait(false);
        if (delete.InvocationFailed)
        {
            LogCommandFailure("delete", delete);
            // English-only user-facing message (R-AS-TASK-SCHEDULER-DIAGNOSTICS): raw localized
            // exception text stays in the bounded structured log above.
            return AutostartResult.Failure("task deletion failed: schtasks.exe could not be started", AutostartState.Unknown);
        }

        // A nonzero /Delete exit code alone proves nothing and is NOT yet an error (not-found is
        // expected when the task was already removed by a previous, interrupted recovery or a
        // concurrent actor). It is provisional until the positive verification below decides
        // (R-AS-TASK-SCHEDULER-DIAGNOSTICS); a genuine failure re-logs the bounded diagnostics
        // at Error exactly once.
        if (delete.ExitCode != 0)
            _logger.LogDebug("Autostart task delete reported nonzero (exit {Code}); verifying actual absence", delete.ExitCode);
        else
            _logger.LogInformation("Autostart task delete command reported success (exit 0); verifying actual absence");

        // Positive verification: /Delete's exit code does not prove the task is gone. Query the
        // machine-readable XML state and require Absent; Unknown/access/service/other failures and
        // a still-present or Misconfigured task all keep the recovery pending (fail-closed).
        var (actual, _) = await ReadStateDetailedAsync(ct).ConfigureAwait(false);
        if (actual == AutostartState.Absent)
        {
            _logger.LogInformation("Autostart task removal verified; the task is absent");
            return AutostartResult.Success(AutostartState.Absent);
        }

        // A genuine failure re-logs the bounded delete diagnostics at Error (the provisional
        // Debug above is upgraded only when the required postcondition was not achieved).
        if (delete.ExitCode != 0)
            LogCommandFailure("delete", delete);
        _logger.LogWarning("Autostart task removal could not be verified (actual state: {Actual}); recovery stays pending", actual);
        return AutostartResult.Failure(
            actual == AutostartState.Unknown
                ? "the autostart task removal could not be verified; the state is unknown"
                : $"the autostart task is still present after the delete (state: {actual})",
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
        bool? Enabled,
        bool? DisallowStartIfOnBatteries,
        bool? StopIfGoingOnBatteries);

    /// <summary>A single failed verification check: which field, what was expected, what was found.</summary>
    internal readonly record struct TaskMismatch(string Check, string Expected, string Actual);

    // Parses the /XML export of a single task. Returns null (Unknown) when the XML cannot be
    // interpreted — a task we cannot describe is a task we cannot call "enabled". The optional
    // logger receives a bounded diagnostic when an <Enabled> element holds an unparsable value.
    internal static TaskDescriptor? ParseTaskXml(string? xml, ILogger? logger = null)
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
        bool? Enabled = null, DisallowStartIfOnBatteries = null, StopIfGoingOnBatteries = null;
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

        // [R-AS-XML-VERIFY] Per the Windows Task Scheduler schema, <Enabled> under <Settings> is
        // OPTIONAL and defaults to true when the <Settings> element itself is present (confirmed
        // against a real /Query /XML export of a successfully created autostart task, which has
        // no <Enabled> element at all). A missing <Enabled> inside an existing <Settings> block
        // is therefore Enabled, not Unknown — otherwise a verified, successfully created task is
        // reported as unverifiable. Fail-closed behavior is preserved: an <Enabled> element with
        // an unparsable boolean keeps the state Unknown, and the schema-default rule is NOT
        // extended to a task with no <Settings> element at all (that case stays Unknown).
        foreach (var settings in task.Elements().Where(e => e.Name.LocalName == "Settings"))
        {
            var enabledEl = FindChild(settings, "Enabled");
            if (enabledEl is null)
            {
                // Schema default: Enabled = true when <Settings> exists without an <Enabled> child.
                Enabled = true;
            }
            else if (bool.TryParse(enabledEl.Value, out var en))
            {
                Enabled = en;
            }
            else
            {
                // Fail-closed: an unreadable Enabled value must never be guessed. Only the raw
                // element value is logged — never the whole exported task XML.
                logger?.LogWarning(
                    "Autostart task XML has an unparsable <Enabled> value ({Value}); the enabled state stays unknown",
                    enabledEl.Value);
            }

            // [R-AS-POWER-POLICY] The battery power restrictions must be explicitly false for the
            // task to be power-source independent. Unlike <Enabled>, NO schema default is applied
            // here: Windows defaults both flags to true, so a missing element is never proof that
            // the task is unrestricted. A missing or unparsable value stays null (unverified),
            // which GetMismatches classifies as a mismatch (fail-closed). Only the raw element
            // value is logged — never the whole exported task XML.
            var disallowEl = FindChild(settings, "DisallowStartIfOnBatteries");
            if (disallowEl is not null)
            {
                if (bool.TryParse(disallowEl.Value, out var disallow))
                    DisallowStartIfOnBatteries = disallow;
                else
                    logger?.LogWarning(
                        "Autostart task XML has an unparsable <DisallowStartIfOnBatteries> value ({Value}); the flag stays unverified",
                        disallowEl.Value);
            }

            var stopEl = FindChild(settings, "StopIfGoingOnBatteries");
            if (stopEl is not null)
            {
                if (bool.TryParse(stopEl.Value, out var stop))
                    StopIfGoingOnBatteries = stop;
                else
                    logger?.LogWarning(
                        "Autostart task XML has an unparsable <StopIfGoingOnBatteries> value ({Value}); the flag stays unverified",
                        stopEl.Value);
            }
        }

        return new TaskDescriptor(
            Command, Arguments, hasLogonTrigger, LogonType, RunLevel, Enabled,
            DisallowStartIfOnBatteries, StopIfGoingOnBatteries);
    }

    /// <summary>
    /// Windows Task Scheduler round-trips a <c>/TR</c> executable that was quoted on the command
    /// line into a quoted <c>&lt;Command&gt;</c> element (e.g. <c>"C:\...\app.exe"</c>). The compare
    /// value is the quoted path's content; the quoting itself is not part of the task semantics.
    /// A single paired outer quote pair is stripped; nothing else is altered.
    /// </summary>
    internal static string? NormalizeCommand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;
        var t = raw.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            t = t.Substring(1, t.Length - 2);
        return t;
    }

    /// <summary>
    /// The set of verification checks that fail for the parsed task definition. An empty result
    /// means the task matches every autostart invariant (it may still be Disabled, or its Enabled
    /// flag may be unreadable, which Evaluate reports separately as Unknown). The security
    /// invariants — LogonTrigger, InteractiveToken, HighestAvailable, the --background flag, the
    /// exact-command requirement and the battery power policy — are never relaxed by this
    /// classification.
    /// </summary>
    internal static IReadOnlyList<TaskMismatch> GetMismatches(TaskDescriptor d, string expectedCommand)
    {
        var mismatches = new List<TaskMismatch>();

        var command = NormalizeCommand(d.Command);
        if (string.IsNullOrWhiteSpace(command))
            mismatches.Add(new TaskMismatch("Command", expectedCommand, d.Command ?? "<missing>"));
        else if (!string.Equals(command, expectedCommand, StringComparison.OrdinalIgnoreCase))
            mismatches.Add(new TaskMismatch("Command", expectedCommand, d.Command ?? "<missing>"));

        if (!(d.Arguments?.Contains("--background", StringComparison.OrdinalIgnoreCase) ?? false))
            mismatches.Add(new TaskMismatch("Arguments", "--background", d.Arguments ?? "<missing>"));

        if (!d.HasLogonTrigger)
            mismatches.Add(new TaskMismatch("LogonTrigger", "LogonTrigger", "<none>"));

        if (!string.Equals(d.LogonType, "InteractiveToken", StringComparison.OrdinalIgnoreCase))
            mismatches.Add(new TaskMismatch("LogonType", "InteractiveToken", d.LogonType ?? "<missing>"));

        if (!string.Equals(d.RunLevel, "HighestAvailable", StringComparison.OrdinalIgnoreCase))
            mismatches.Add(new TaskMismatch("RunLevel", "HighestAvailable", d.RunLevel ?? "<missing>"));

        // [R-AS-POWER-POLICY] Battery independence: both Task Scheduler power restrictions must
        // be explicitly false. A missing element or an unparsable value fails the check as well —
        // absence is never proof that the task is unrestricted (Windows defaults both flags to
        // true), so the task cannot be trusted as power-source independent unless the exported
        // XML proves both flags false.
        if (d.DisallowStartIfOnBatteries is not false)
            mismatches.Add(new TaskMismatch(
                "DisallowStartIfOnBatteries",
                "false",
                d.DisallowStartIfOnBatteries?.ToString() ?? "<missing>"));
        if (d.StopIfGoingOnBatteries is not false)
            mismatches.Add(new TaskMismatch(
                "StopIfGoingOnBatteries",
                "false",
                d.StopIfGoingOnBatteries?.ToString() ?? "<missing>"));

        return mismatches;
    }

    /// <summary>
    /// Classifies a parsed task definition against the expected autostart configuration.
    /// Misconfigured wins over Disabled: any parameter mismatch means the registration cannot
    /// be trusted as the correct autostart entry, regardless of its enabled flag.
    /// </summary>
    internal static AutostartState Evaluate(TaskDescriptor d, string expectedCommand)
    {
        if (GetMismatches(d, expectedCommand).Count > 0)
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

    /// <summary>
    /// Matches an access-denied query/delete failure (a permission problem on the Task Scheduler
    /// store, not a missing task). Only concrete, tested markers are matched; anything else is
    /// left unclassified and therefore Unknown.
    /// </summary>
    internal static bool LooksLikeAccessDenied(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("отказано в доступе", StringComparison.OrdinalIgnoreCase)
            || text.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase)
            || text.Contains("アクセスが拒否され", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Matches an unavailable/not-started Task Scheduler service. Only concrete, tested markers
    /// are matched; anything else stays unclassified (Unknown).
    /// </summary>
    internal static bool LooksLikeServiceUnavailable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.Contains("service is not available", StringComparison.OrdinalIgnoreCase)
            || text.Contains("the service has not been started", StringComparison.OrdinalIgnoreCase)
            || text.Contains("служба недоступна", StringComparison.OrdinalIgnoreCase)
            || text.Contains("the rpc server is unavailable", StringComparison.OrdinalIgnoreCase);
    }

    // Logs a failed schtasks invocation/operation with the operation name, exit code and each
    // stream bounded, plus the invocation exception when present. Never logs a successful full
    // task XML. The streams are logged separately, never collapsed before logging.
    private void LogCommandFailure(string operation, SchTasksCommandResult result)
    {
        if (result.InvocationFailed)
        {
            _logger.LogError(result.InvocationException,
                "Autostart {Operation:l} failed: schtasks.exe could not be invoked", operation);
            return;
        }
        _logger.LogError(
            "Autostart {Operation:l} failed (exit {Code}); stdout: {StdOut:l}; stderr: {StdErr:l}",
            operation,
            result.ExitCode,
            SchTasksCommandResult.Bound(result.StandardOutput),
            SchTasksCommandResult.Bound(result.StandardError));
    }

    // ---------------------------------------------------------------------
    // Command output decoding (v2.6 Amendment D / R-AS-ENC)
    // ---------------------------------------------------------------------

    // P/Invoke used to resolve the system OEM code page at runtime. The system locale's OEM
    // page governs the child's own console default; a hard-coded locale-specific page is
    // intentionally never used.
    [DllImport("kernel32.dll", EntryPoint = "GetOEMCP", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetOemCodePage();

    /// <summary>The raw system OEM code page number reported by <c>GetOEMCP</c>.</summary>
    internal static int OemCodePage => (int)GetOemCodePage();

    /// <summary>
    /// The encoding both redirected schtasks.exe streams are decoded with. Live Windows evidence
    /// (raw-byte capture on Windows 10.0.26300, ru-RU, ACP 1251 / OEMCP 866, 2026-11-03) showed
    /// that schtasks.exe writes its redirected output using its own fresh (windowless) console's
    /// default code page — the system OEM code page — and NOT the parent console's code page
    /// (a parent console switched to 1251 still produced CP866 bytes). The page is therefore
    /// resolved via <c>GetOEMCP</c> at runtime instead of hard-coding a locale-specific value.
    /// Without an explicit encoding, the app's windowless process has <c>Console.OutputEncoding</c>
    /// with code page 0, and the localized task-not-found message decodes as mojibake that the
    /// classifier cannot recognize — which silently downgraded verified Absent states to Unknown.
    /// </summary>
    internal static readonly Encoding CommandOutputEncoding = CreateCommandOutputEncoding();

    private static Encoding CreateCommandOutputEncoding()
    {
        // Legacy Windows code pages (e.g. the OEM page) require the code-pages provider on .NET.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(OemCodePage);
        }
        catch (ArgumentException ex)
        {
            // The OEM page could not be resolved in this environment. Decoding must stay
            // functional, so fall back to UTF-8 explicitly and record why; classification remains
            // fail-closed (unrecognized output is Unknown), so a wrong page can never produce a
            // false "task absent".
            Debug.Fail($"System OEM code page {OemCodePage} could not be resolved: {ex.Message}");
            return Encoding.UTF8;
        }
    }

    // [R-AS-POWER-POLICY] Registers the autostart task by importing a full task-definition XML.
    // "schtasks.exe /Create" has no command-line switches for the Task Settings power
    // restrictions, and Windows defaults both <DisallowStartIfOnBatteries> and
    // <StopIfGoingOnBatteries> to true — live evidence: the task created by the previous
    // /TR command line exports both flags as true, which blocks the logon autostart on battery
    // power and stops the running app when the charger is unplugged. A /Create /XML import with
    // both flags explicitly false is the only schtasks.exe facility that can set them; no COM/WMI
    // dependency is introduced.
    // [R-AS-XML-IMPORT-SECURITY] The definition is staged inside the protected elevated data
    // root, never in the interactive user's %TEMP%: a medium-integrity process of the same user
    // could otherwise tamper with the XML consumed by the elevated schtasks.exe. The root must
    // exist, be a real directory (no reparse indirection) and carry the protected owner/DACL
    // before anything is staged; the GUID-named file is created exclusively with its
    // born-protected descriptor supplied at creation (CREATE_NEW, protected DACL, Administrators
    // owner, Administrators + SYSTEM FullControl only), and its live classification is verified
    // after the handle is closed and before the elevated consumer opens it. Every gate is fail
    // closed: any refusal returns a StagingFailed result and never invokes schtasks.exe. The file
    // is always deleted in a finally block — on success, on failure and on cancellation alike —
    // but only when this invocation owns it: a CREATE_NEW collision never deletes, overwrites, or
    // modifies the pre-existing object.
    private async Task<SchTasksCommandResult> CreateTaskAsync(string exePath, CancellationToken ct)
    {
        // Fail-closed root gate: elevated configuration initialization guarantees a protected root
        // at startup, before autostart synchronization; any missing/reparse/untrusted root refuses
        // staging here instead of writing security-sensitive XML to an unverified location.
        var stagingRoot = ElevatedDataPaths.ElevatedRoot;
        if (!_staging.ExistsAndIsNotReparse(stagingRoot))
        {
            _logger.LogError(
                "Autostart staging refused: the protected data root {Root:l} is missing or is a reparse point",
                stagingRoot);
            return SchTasksCommandResult.SecureStagingRefused(
                "the protected data root is missing or is a reparse point");
        }
        if (_staging.Classify(stagingRoot, isDirectory: true) != ProtectionClassification.Trusted)
        {
            _logger.LogError(
                "Autostart staging refused: the protected data root {Root:l} is not classified as trusted",
                stagingRoot);
            return SchTasksCommandResult.SecureStagingRefused(
                "the protected data root is not classified as trusted");
        }

        var definitionPath = Path.Combine(stagingRoot, $"ProcessGuardian-task-{Guid.NewGuid():N}.xml");

        // Encoding.Unicode via WriteAllText also writes the UTF-16 LE BOM; the exclusive-create
        // stream has no encoder, so the preamble is written explicitly to keep the bytes identical.
        var content = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(BuildTaskDefinitionXml(exePath)))
            .ToArray();

        bool owned = false;
        try
        {
            try
            {
                _staging.CreateExclusiveProtected(
                    definitionPath, ElevatedSecurity.CreateProtectedFileSecurity(), content, out owned);
            }
            catch (Win32Exception ex)
            {
                // Exclusive-create refusal (a CREATE_NEW collision among others): the pre-existing
                // object is never overwritten, deleted, or modified (owned stays false); staging
                // fails closed without invoking schtasks.exe.
                _logger.LogError(ex,
                    "Autostart staging refused: the exclusive definition file could not be created");
                return SchTasksCommandResult.SecureStagingRefused(
                    "the exclusive definition file could not be created");
            }

            // Mandatory post-create verification of the actual staged file (R-AS-XML-IMPORT-SECURITY):
            // the handle is already closed here, and a file whose live owner/DACL does not classify
            // as trusted is never handed to the elevated consumer. A zero /Create exit code is not a
            // substitute either — the /Query /XML verification below it stays mandatory.
            if (_staging.Classify(definitionPath, isDirectory: false) != ProtectionClassification.Trusted)
            {
                _logger.LogError(
                    "Autostart staging refused: the staged definition file is not classified as trusted");
                return SchTasksCommandResult.SecureStagingRefused(
                    "the staged definition file is not classified as trusted");
            }

            return await RunSchTasksAsync(BuildCreateArguments(definitionPath), ct).ConfigureAwait(false);
        }
        finally
        {
            if (owned)
                TryDeleteDefinitionFile(definitionPath);
        }
    }

    // Best-effort cleanup of the staged task-definition file. Called only for a file created by
    // the current invocation (R-AS-XML-IMPORT-SECURITY ownership tracking) — a CREATE_NEW
    // collision never reaches this method, so a pre-existing object is never touched. The file
    // sits in the protected elevated root, where a medium-integrity caller can still hit
    // UnauthorizedAccessException/SecurityException on a misconfigured root; a failed delete is
    // logged and never thrown, so the create/verify outcome above is never masked by a cleanup
    // error.
    private void TryDeleteDefinitionFile(string path)
    {
        try
        {
            _staging.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "The staged task-definition file could not be deleted: {Path:l}", path);
        }
    }

    // [R-AS-POWER-POLICY] Builds the full Task Scheduler 2.0 definition XML imported by
    // CreateTaskAsync. Every value — including the executable path — is escaped by the XElement
    // construction itself; nothing machine-specific is hard-coded (the principal UserId is the
    // identity the process actually runs under, resolved at runtime). All the autostart
    // invariants are preserved: LogonTrigger, InteractiveToken, HighestAvailable, the quoted
    // executable with the --background argument, the interactive user session. The power
    // restrictions are explicitly false — Windows defaults are never relied upon.
    internal static string BuildTaskDefinitionXml(string exePath)
    {
        var ns = TaskNs;
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo"),
                new XElement(ns + "Triggers",
                    new XElement(ns + "LogonTrigger")),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "UserId",
                            $"{Environment.UserDomainName}\\{Environment.UserName}"),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "HighestAvailable"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "IdleSettings",
                        new XElement(ns + "Duration", "PT10M"),
                        new XElement(ns + "WaitTimeout", "PT1H"),
                        new XElement(ns + "StopOnIdleEnd", "true"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "Enabled", "true")),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", $"\"{exePath}\""),
                        new XElement(ns + "Arguments", "--background")))));

        // XDocument.ToString() omits the XML declaration; it is prepended explicitly so the
        // declared utf-16 encoding matches the Encoding.Unicode bytes the file is written with.
        return $"{doc.Declaration}{Environment.NewLine}{doc}";
    }

    // The task definition is imported from the temporary XML file built by
    // BuildTaskDefinitionXml ("schtasks.exe /Create" has no command-line switches for the Task
    // Settings power restrictions). /F force-overwrites an existing registration so the repair
    // path can replace a task with incorrect settings in place.
    internal static string BuildCreateArguments(string definitionXmlPath) =>
        $"/Create /TN \"{AppEdition.ScheduledTaskName}\" /XML \"{definitionXmlPath}\" /F";

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

            // Both streams are decoded with the verified OEM code page before classification
            // (v2.6 Amendment D / R-AS-ENC): without this, a windowless process decodes with
            // code page 0 and localized messages become mojibake.
            StandardOutputEncoding = CommandOutputEncoding,
            StandardErrorEncoding = CommandOutputEncoding,
        };

    // Runs schtasks.exe through the injected seam when present, otherwise the real process.
    // Both paths share the same exception handling so a seam-thrown error behaves exactly like
    // a real process-start failure (surfaced as a failure result with the exception captured,
    // never lost and never silently thrown). stdout and stderr stay separate in the result.
    private async Task<SchTasksCommandResult> RunSchTasksAsync(string arguments, CancellationToken ct)
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
                return new SchTasksCommandResult(-1, string.Empty, string.Empty,
                    new InvalidOperationException("schtasks.exe could not be started"));
            }

            // Read both streams concurrently: a sequential read can deadlock if
            // schtasks fills the stderr pipe buffer while stdout is still open.
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new SchTasksCommandResult(process.ExitCode, outputTask.Result, errorTask.Result, null);
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
            return new SchTasksCommandResult(-1, string.Empty, string.Empty, ex);
        }
    }
}
