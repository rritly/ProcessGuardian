using System.Text;
using FluentAssertions;
using ProcessGuardian.Infrastructure.Autostart;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// R-AS-ENC (v2.6 Amendment D): schtasks.exe writes its redirected output using its own fresh
// windowless console's default code page — the system OEM code page — not the parent console's
// code page. The service must therefore configure both redirected stream encodings explicitly
// with that page; without it a windowless WPF process decodes with code page 0 and the
// localized task-not-found message becomes mojibake, silently downgrading a verified Absent
// state to Unknown. Fixtures below are split into LIVE raw-byte captures (real schtasks.exe
// runs) and SYNTHETIC strings (unit-only, never live evidence).
//
// Environment independence: each LIVE fixture is a byte-for-byte capture from ONE known
// Windows installation whose OEM page was CP866, so it is decoded with that FIXED capture page
// (never the runtime page resolved from GetOEMCP() on the machine executing the tests). A
// GitHub Actions windows-latest runner is en-US (OEMCP 437): decoding a CP866 capture with the
// runtime page yields mojibake and the exact-message/classification assertions below would
// fail on CI while passing on a ru-RU machine. The runtime page selection itself is validated
// separately by CommandOutputEncoding_IsTheSystemOemCodePage.
public class ScheduledTaskAutostartEncodingTests
{
    // ------------------------------------------------------------------
    // Encoding configuration of the launched process
    // ------------------------------------------------------------------

    [Fact]
    public void BuildProcessStartInfo_SetsBothRedirectedStreamEncodings_ToTheSelectedStrategy()
    {
        var psi = ScheduledTaskAutostartService.BuildProcessStartInfo("/Query");

        psi.StandardOutputEncoding.Should().NotBeNull("stdout must be decoded with the verified page");
        psi.StandardErrorEncoding.Should().NotBeNull("stderr must be decoded with the verified page");
        psi.StandardOutputEncoding.Should().BeSameAs(ScheduledTaskAutostartService.CommandOutputEncoding,
            "both streams use the same evidence-backed decoding strategy");
        psi.StandardErrorEncoding.Should().BeSameAs(ScheduledTaskAutostartService.CommandOutputEncoding);
    }

    [Fact]
    public void CommandOutputEncoding_IsTheSystemOemCodePage()
    {
        ScheduledTaskAutostartService.CommandOutputEncoding.CodePage
            .Should().Be(ScheduledTaskAutostartService.OemCodePage,
                "the verified behavior follows the system OEM code page, resolved at runtime, " +
                "never a hard-coded locale-specific value");
    }

    // ------------------------------------------------------------------
    // LIVE raw-byte fixtures (captured from real Windows schtasks.exe runs)
    // ------------------------------------------------------------------

    // LIVE evidence, captured 2026-11-03 on Windows 10.0.26300 (ru-RU, ACP 1251, OEMCP 866,
    // parent console CP 866; a control run with the parent console CP switched to 1251 produced
    // byte-identical output): raw stderr of `schtasks.exe /Query /XML /TN "<absent task>"`,
    // exit code 1, stdout empty (43 bytes). Decoded with CP866 the message is
    // "ОШИБКА: Не удается найти указанный файл." followed by "\r\r\n". (In CP1251 byte 0x98 is
    // undefined, which uniquely rules that page out.)
    private const string LiveWindowsMissingTaskStderrHex =
        "8E9888818A803A208DA520E3A4A0A5E2E1EF20ADA0A9E2A820E3AAA0A7A0ADADEBA920E4A0A9AB2E0D0D0A";

    // The LIVE fixtures were captured on a machine whose OEM page was CP866, so they are decoded
    // with that fixed page, never the runtime GetOEMCP() page of the machine running the tests
    // (machine independence for CI, see the class comment). The runtime selection is asserted by
    // CommandOutputEncoding_IsTheSystemOemCodePage.
    private static readonly Encoding LiveFixtureEncoding = CreateLiveFixtureEncoding();

    private static Encoding CreateLiveFixtureEncoding()
    {
        // Legacy Windows code pages (e.g. CP866) require the code-pages provider on .NET.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(866);
    }

    private static string DecodeLiveFixture(byte[] raw) => LiveFixtureEncoding.GetString(raw);

    private static byte[] LiveMissingTaskBytes() => Convert.FromHexString(LiveWindowsMissingTaskStderrHex);

    [Fact]
    public void LiveWindowsRawBytes_MissingTaskOnStderr_DecodeToTheRealMessage()
    {
        var decoded = DecodeLiveFixture(LiveMissingTaskBytes());

        decoded.Should().Be("ОШИБКА: Не удается найти указанный файл.\r\r\n",
            "the live raw-byte capture must round-trip through its fixed capture page (CP866)");
    }

    [Fact]
    public void LiveWindowsRawBytes_MissingTaskOnStderr_ClassifiesAsMissingTask()
    {
        var decoded = DecodeLiveFixture(LiveMissingTaskBytes());
        var result = new ScheduledTaskAutostartService.SchTasksCommandResult(1, string.Empty, decoded, null);

        ScheduledTaskAutostartService.ClassifyQueryResult(result)
            .Should().Be(ScheduledTaskAutostartService.SchTasksQueryFailure.MissingTask,
                "the real captured bytes must classify as a clean task-not-found after decoding");
    }

    [Fact]
    public void LiveWindowsRawBytes_MissingTaskOnStdout_ClassifiesAsMissingTask()
    {
        // The marker may arrive on either stream; the same live bytes delivered on stdout must
        // still classify as MissingTask.
        var decoded = DecodeLiveFixture(LiveMissingTaskBytes());
        var result = new ScheduledTaskAutostartService.SchTasksCommandResult(1, decoded, string.Empty, null);

        ScheduledTaskAutostartService.ClassifyQueryResult(result)
            .Should().Be(ScheduledTaskAutostartService.SchTasksQueryFailure.MissingTask);
    }

    [Fact]
    public void LiveWindowsRawBytes_UndecodableGarbage_StaysUnknown()
    {
        // Fail-closed guard: bytes that do not decode into a recognized marker (here: the same
        // live bytes XOR-mangled) must remain Unknown, never Absent.
        var raw = LiveMissingTaskBytes();
        for (var i = 0; i < raw.Length; i++)
            raw[i] ^= 0x5A;
        var decoded = DecodeLiveFixture(raw);
        var result = new ScheduledTaskAutostartService.SchTasksCommandResult(1, string.Empty, decoded, null);

        ScheduledTaskAutostartService.ClassifyQueryResult(result)
            .Should().Be(ScheduledTaskAutostartService.SchTasksQueryFailure.Unknown,
                "undecodable/unrecognized output must stay Unknown (fail-closed)");
    }

    // ------------------------------------------------------------------
    // SYNTHETIC fixtures (unit-only; never live Windows evidence)
    // ------------------------------------------------------------------

    [Fact]
    public void SyntheticMojibakeText_IsNeverAClassifiedMarker()
    {
        // The pre-fix defect decoded the OEM message as replacement characters (code page 0).
        // Mojibake variants must never be added to the classifier (v2.6 Amendment D): this
        // synthetic string stays unrecognized.
        var mojibake = "??????: ?? ??????? ????? ??????? ?????.";
        var result = new ScheduledTaskAutostartService.SchTasksCommandResult(1, string.Empty, mojibake, null);

        ScheduledTaskAutostartService.LooksLikeMissingTask(mojibake).Should().BeFalse();
        ScheduledTaskAutostartService.ClassifyQueryResult(result)
            .Should().Be(ScheduledTaskAutostartService.SchTasksQueryFailure.Unknown);
    }
}
