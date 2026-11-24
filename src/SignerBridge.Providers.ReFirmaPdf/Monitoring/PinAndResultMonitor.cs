using System.Diagnostics;
using SignerBridge.Core.Abstractions;
using SignerBridge.Core.Models;
using SignerBridge.Providers.ReFirmaPdf.Jab;

namespace SignerBridge.Providers.ReFirmaPdf.Monitoring;

/// <summary>
/// Hybrid UI + Log monitor that waits for manual PIN entry and detects completion,
/// cancellation, cryptographic errors, or timeouts.
/// </summary>
internal static class PinAndResultMonitor
{
    public static async Task<SignExecutionResult> WaitForPinAndCompletionAsync(
        JabSession jab,
        RefirmaLogWatcher logWatcher,
        SignJobContext context,
        CancellationToken ct)
    {
        int expectedCount = context.InputPdfFiles.Count;
        var pinTimeout = TimeSpan.FromSeconds(context.Payload.Timeouts.UserPinSec);
        var signingTimeout = TimeSpan.FromSeconds(context.Payload.Timeouts.SigningSec);

        bool pinDialogClosed = false;
        var phaseWatch = Stopwatch.StartNew();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // 1. Check log lines appended since job start
            var newLines = logWatcher.ReadNewLines();
            var logFinding = RefirmaLogWatcher.InspectLines(newLines);
            if (logFinding.HasTerminalFinding)
            {
                await DismissAnyAlertModalAsync(jab, ct).ConfigureAwait(false);
                return new SignExecutionResult(
                    Success: false,
                    ErrorCode: logFinding.ErrorCode,
                    ErrorDetail: logFinding.Detail,
                    Cancelled: logFinding.Cancelled);
            }

            // 2. Ensure ReFirma PDF is still alive
            var anyReFirma = await jab.FindJavaWindowAsync(
                t => t.Contains("ReFirma PDF", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
            if (anyReFirma is null)
            {
                return new SignExecutionResult(
                    Success: false,
                    ErrorCode: SignErrorCode.USER_CANCELLED_PIN,
                    ErrorDetail: "ReFirma PDF fue cerrado antes de completar la firma.",
                    Cancelled: true);
            }

            // 3. Track PIN dialog state
            var pinWin = await jab.FindJavaWindowAsync(
                t => t.Contains("DNIe", StringComparison.OrdinalIgnoreCase) || t.Contains("PIN", StringComparison.OrdinalIgnoreCase), ct)
                .ConfigureAwait(false);

            if (!pinDialogClosed)
            {
                if (pinWin is null)
                {
                    pinDialogClosed = true;
                    phaseWatch.Restart();
                    context.ReportProgress(SignJobStatus.SIGNING_IN_PROGRESS, "PIN procesado; firmando documentos en lote...");
                }
                else if (phaseWatch.Elapsed > pinTimeout)
                {
                    await TryCancelPinDialogAsync(jab, pinWin.Value, ct).ConfigureAwait(false);
                    return new SignExecutionResult(
                        Success: false,
                        ErrorCode: SignErrorCode.TIMEOUT,
                        ErrorDetail: "Tiempo de espera agotado esperando el ingreso manual del PIN.",
                        TimedOut: true);
                }
            }
            else
            {
                // 4. Post-PIN: check for alert modal (success or error) and physical output files
                var alertWin = await jab.FindJavaWindowAsync(
                    t => t.Equals("ReFirma PDF", StringComparison.OrdinalIgnoreCase) ||
                         t.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                         t.Contains("Firma digital en lote", StringComparison.OrdinalIgnoreCase), ct)
                    .ConfigureAwait(false);

                if (alertWin is not null)
                {
                    string dialogText = await ReadDialogLabelsAsync(jab, alertWin.Value, ct).ConfigureAwait(false);
                    await DismissDialogAsync(jab, alertWin.Value, ct).ConfigureAwait(false);

                    if (LooksLikeErrorDialog(dialogText))
                    {
                        return new SignExecutionResult(
                            Success: false,
                            ErrorCode: ClassifyUiError(dialogText),
                            ErrorDetail: string.IsNullOrWhiteSpace(dialogText) ? "ReFirma PDF reportó un error al firmar." : dialogText);
                    }
                }

                if (AreAllOutputPdfsReady(context.OutputDirectory, expectedCount))
                {
                    await DismissAnyAlertModalAsync(jab, ct).ConfigureAwait(false);
                    return new SignExecutionResult(Success: true);
                }

                if (phaseWatch.Elapsed > signingTimeout)
                {
                    return new SignExecutionResult(
                        Success: false,
                        ErrorCode: SignErrorCode.TIMEOUT,
                        ErrorDetail: "Tiempo de espera agotado esperando la generación de los PDFs firmados.",
                        TimedOut: true);
                }
            }

            await Task.Delay(400, ct).ConfigureAwait(false);
        }
    }

    public static bool AreAllOutputPdfsReady(string outputDirectory, int expectedCount)
    {
        if (!Directory.Exists(outputDirectory))
            return false;

        var files = Directory.GetFiles(outputDirectory, "*.pdf", SearchOption.TopDirectoryOnly);
        if (files.Length < expectedCount)
            return false;

        foreach (var file in files)
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length == 0) return false;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException)
            {
                return false; // Still being written by ReFirma
            }
        }

        return true;
    }

    private static async Task<string> ReadDialogLabelsAsync(JabSession jab, JavaWindow win, CancellationToken ct)
    {
        var nodes = await jab.FindAllNodesAsync(win, n =>
            (n.Info.role_en_US is "label" or "text") && !string.IsNullOrWhiteSpace(n.Info.name), ct).ConfigureAwait(false);
        return string.Join(" ", nodes.Select(n => n.Info.name.Trim()).Distinct());
    }

    private static async Task DismissAnyAlertModalAsync(JabSession jab, CancellationToken ct)
    {
        var alertWin = await jab.FindJavaWindowAsync(
            t => t.Equals("ReFirma PDF", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (alertWin is not null)
            await DismissDialogAsync(jab, alertWin.Value, ct).ConfigureAwait(false);
    }

    private static async Task DismissDialogAsync(JabSession jab, JavaWindow win, CancellationToken ct)
    {
        var btn = await jab.FindNodeAsync(win, n =>
            n.Info.role_en_US == "push button" &&
            (n.Info.name.Equals("Aceptar", StringComparison.OrdinalIgnoreCase) ||
             n.Info.name.Equals("Cerrar", StringComparison.OrdinalIgnoreCase) ||
             n.Info.name.Equals("OK", StringComparison.OrdinalIgnoreCase)), ct).ConfigureAwait(false);

        if (btn is not null)
        {
            await jab.ClickNodeCenterAsync(btn.Value, ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
    }

    private static async Task TryCancelPinDialogAsync(JabSession jab, JavaWindow pinWin, CancellationToken ct)
    {
        var cancelBtn = await jab.FindNodeAsync(pinWin, n =>
            n.Info.role_en_US == "push button" && n.Info.name.Equals("Cancelar", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (cancelBtn is not null)
        {
            await jab.ClickNodeCenterAsync(cancelBtn.Value, ct).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
            await DismissAnyAlertModalAsync(jab, ct).ConfigureAwait(false);
        }
    }

    private static bool LooksLikeErrorDialog(string text)
        => text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("incorrecto", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("bloquead", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("no se pudo", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("fall", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("cancel", StringComparison.OrdinalIgnoreCase);

    private static SignErrorCode ClassifyUiError(string text)
    {
        if (text.Contains("PIN", StringComparison.OrdinalIgnoreCase) || text.Contains("bloquead", StringComparison.OrdinalIgnoreCase))
            return SignErrorCode.INVALID_PIN_OR_LOCKED;
        if (text.Contains("TSL", StringComparison.OrdinalIgnoreCase))
            return SignErrorCode.TSL_VALIDATION_ERROR;
        if (text.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            return SignErrorCode.USER_CANCELLED_PIN;
        return SignErrorCode.UI_AUTOMATION_ERROR;
    }
}
