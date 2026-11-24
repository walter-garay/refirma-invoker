using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignerBridge.Core.Abstractions;
using SignerBridge.Core.Configuration;
using SignerBridge.Core.Models;
using SignerBridge.Providers.ReFirmaPdf.Jab;
using SignerBridge.Providers.ReFirmaPdf.Monitoring;

namespace SignerBridge.Providers.ReFirmaPdf;

/// <summary>
/// ReFirma PDF 1.6 automation provider using Java Access Bridge 64-bit on a dedicated STA message pump.
/// </summary>
public sealed class ReFirmaPdfProvider : ISignerProvider, IDisposable
{
    public const string Id = "refirma-pdf";

    private readonly string _appRefPath;
    private readonly string _jreBinPath;
    private readonly string _logFilePath;
    private readonly bool _mockExecution;
    private readonly ILogger<ReFirmaPdfProvider>? _logger;
    private readonly Lazy<JabStaDispatcher> _staDispatcher;

    public ReFirmaPdfProvider(
        IOptions<SignerBridgeOptions> options,
        ILogger<ReFirmaPdfProvider>? logger = null,
        bool? forceMock = null)
    {
        options.Value.Providers.TryGetValue(Id, out var cfg);
        _logger = logger;
        _mockExecution = forceMock ?? (cfg?.MockExecution ?? false);

        _appRefPath = !string.IsNullOrWhiteSpace(cfg?.ExecutablePath)
            ? Environment.ExpandEnvironmentVariables(cfg.ExecutablePath)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\RENIEC\ReFirma Suite\ReFirma PDF 1.6.appref-ms");

        _jreBinPath = !string.IsNullOrWhiteSpace(cfg?.JrePath)
            ? Environment.ExpandEnvironmentVariables(cfg.JrePath)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"AppRENIEC\common\jre\jre-8\bin");

        _logFilePath = !string.IsNullOrWhiteSpace(cfg?.LogFilePath)
            ? Environment.ExpandEnvironmentVariables(cfg.LogFilePath)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"AppRENIEC\refirma-pdf\refirma-pdf.log");

        _staDispatcher = new Lazy<JabStaDispatcher>(() => new JabStaDispatcher(_jreBinPath));
    }

    public string ProviderId => Id;
    public string DisplayName => "RENIEC ReFirma PDF 1.6";
    public string ResolvedExecutablePath => _appRefPath;
    public string ResolvedJreBinPath => _jreBinPath;
    public string ResolvedLogFilePath => _logFilePath;

    public Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        if (_mockExecution)
            return Task.FromResult(new ProviderAvailability(true, "Modo simulación activo."));

        if (!File.Exists(_appRefPath))
            return Task.FromResult(new ProviderAvailability(false, $"No se encontró el acceso directo ClickOnce: {_appRefPath}"));

        string jabDll = Path.Combine(_jreBinPath, "WindowsAccessBridge-64.dll");
        if (!File.Exists(jabDll))
            return Task.FromResult(new ProviderAvailability(false, $"No se encontró WindowsAccessBridge-64.dll en {_jreBinPath}"));

        return Task.FromResult(new ProviderAvailability(true, "ReFirma PDF 1.6 y JAB 64-bit disponibles."));
    }

    public async Task<SignExecutionResult> ExecuteBatchSignAsync(SignJobContext context, CancellationToken ct)
    {
        if (_mockExecution)
            return await ExecuteMockAsync(context, ct).ConfigureAwait(false);

        if (!File.Exists(_appRefPath))
        {
            return new SignExecutionResult(
                false,
                SignErrorCode.APP_NOT_INSTALLED,
                $"No se encontró ReFirma PDF en {_appRefPath}");
        }

        JabAccessibilityHelper.EnsureJabEnabled(_jreBinPath);
        var jab = new JabSession(_staDispatcher.Value);
        var logWatcher = new RefirmaLogWatcher(_logFilePath);

        try
        {
            // Phase 3: Launch / Attach + Batch Configuration + Legal Declaration -> Reach PIN Dialog
            var prePinResult = await ExecutePrePinFlowAsync(jab, context, ct).ConfigureAwait(false);
            if (!prePinResult.Success)
            {
                await ForceCloseReFirmaAsync(jab).ConfigureAwait(false);
                return prePinResult;
            }

            // Phase 4: Wait for manual PIN entry and monitor hybrid UI + refirma-pdf.log
            context.ReportProgress(SignJobStatus.WAITING_USER_PIN, "Esperando que el usuario ingrese el PIN de Firma en ReFirma PDF...");
            var finalResult = await PinAndResultMonitor.WaitForPinAndCompletionAsync(jab, logWatcher, context, ct).ConfigureAwait(false);
            if (!finalResult.Success)
                await ForceCloseReFirmaAsync(jab).ConfigureAwait(false);

            return finalResult;
        }
        catch
        {
            await ForceCloseReFirmaAsync(jab).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SignExecutionResult> ExecutePrePinFlowAsync(JabSession jab, SignJobContext context, CancellationToken ct)
    {
        var timeouts = context.Payload.Timeouts;

        // 1. Launch or attach to clean ReFirma PDF 1.6 window
        context.ReportProgress(SignJobStatus.LAUNCHING_APP, "Iniciando ReFirma PDF 1.6...");
        var mainWin = await EnsureMainWindowReadyAsync(jab, TimeSpan.FromSeconds(timeouts.AppStartupSec), ct).ConfigureAwait(false);
        if (mainWin is null)
            return new SignExecutionResult(false, SignErrorCode.TIMEOUT, "No se pudo abrir o detectar la ventana principal de ReFirma PDF 1.6.", TimedOut: true);

        // 2. Navigate Archivo -> Firmar en lote...
        context.ReportProgress(SignJobStatus.CONFIGURING_BATCH, "Abriendo módulo de firma digital en lote...");
        var batchWin = await OpenBatchDialogAsync(jab, mainWin.Value, ct).ConfigureAwait(false);
        if (batchWin is null)
            return new SignExecutionResult(false, SignErrorCode.UI_AUTOMATION_ERROR, "No se abrió el diálogo 'Parámetros de firma digital en lote'.");

        // 3. Configure Entrada & Salida folders via '...' JFileChooser
        if (!await SetFolderViaChooserAsync(jab, batchWin.Value, "documentos para firmar", context.InputDirectory, ct).ConfigureAwait(false))
            return new SignExecutionResult(false, SignErrorCode.UI_AUTOMATION_ERROR, "No se pudo configurar la carpeta de entrada en ReFirma PDF.");

        batchWin = await jab.FindJavaWindowAsync(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (batchWin is null)
            return new SignExecutionResult(false, SignErrorCode.UI_AUTOMATION_ERROR, "Se perdió el diálogo de parámetros tras elegir entrada.");

        if (!await SetFolderViaChooserAsync(jab, batchWin.Value, "carpeta destino", context.OutputDirectory, ct).ConfigureAwait(false))
            return new SignExecutionResult(false, SignErrorCode.UI_AUTOMATION_ERROR, "No se pudo configurar la carpeta de salida temporal en ReFirma PDF.");

        // 4. Select Certificate (with 1 Actualizar retry)
        batchWin = await jab.FindJavaWindowAsync(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (batchWin is null)
            return new SignExecutionResult(false, SignErrorCode.UI_AUTOMATION_ERROR, "Se perdió el diálogo de parámetros tras elegir salida.");

        if (!await SelectCertificateAsync(jab, batchWin.Value, context.Payload.Certificate, ct).ConfigureAwait(false))
        {
            await TryClickButtonAsync(jab, batchWin.Value, "Cancelar", ct).ConfigureAwait(false);
            return new SignExecutionResult(false, SignErrorCode.CERT_NOT_FOUND,
                $"No se encontró ningún certificado digital que coincida con '{context.Payload.Certificate.Query}' ({context.Payload.Certificate.MatchBy}).");
        }

        // 5. Configure Overwrite, Visible Signature, Stamp Image, Signature Level, Certify, Position, Reason
        batchWin = await jab.FindJavaWindowAsync(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (batchWin is null)
            return new SignExecutionResult(false, SignErrorCode.UI_AUTOMATION_ERROR, "Se perdió el diálogo de parámetros tras seleccionar certificado.");

        await ConfigureBatchOptionsAsync(jab, batchWin.Value, context.Payload, ct).ConfigureAwait(false);

        // 6. Click Aceptar -> Wait Declaración -> Type ACEPTO -> Click Firmar
        context.ReportProgress(SignJobStatus.AWAITING_DECLARATION, "Confirmando declaración legal (ACEPTO)...");
        batchWin = await jab.FindJavaWindowAsync(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (batchWin is null)
            return new SignExecutionResult(false, SignErrorCode.UI_AUTOMATION_ERROR, "Se perdió el diálogo de parámetros antes de aceptar.");

        var aceptarBtn = await jab.FindNodeAsync(batchWin.Value, n =>
            n.Info.role_en_US == "push button" && n.Info.name.Equals("Aceptar", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);

        if (aceptarBtn is null || !aceptarBtn.Value.Info.states_en_US.Contains("enabled", StringComparison.OrdinalIgnoreCase))
            return new SignExecutionResult(false, SignErrorCode.UI_AUTOMATION_ERROR, "El botón 'Aceptar' en Parámetros de firma no está habilitado.");

        await jab.ClickNodeCenterAsync(aceptarBtn.Value, ct).ConfigureAwait(false);

        var declWin = await jab.WaitForJavaWindowAsync(
            t => t.Contains("Declaración", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

        if (declWin is null)
            return new SignExecutionResult(false, SignErrorCode.UI_AUTOMATION_ERROR, "No apareció el modal de 'Declaración'.");

        await Task.Delay(350, ct).ConfigureAwait(false);

        var acepInput = await jab.FindNodeAsync(declWin.Value, n =>
            n.Info.role_en_US == "text" && n.Info.states_en_US.Contains("editable", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (acepInput is null)
            return new SignExecutionResult(false, SignErrorCode.UI_AUTOMATION_ERROR, "No se encontró el campo de texto para escribir ACEPTO.");

        await jab.BringToForegroundAsync(declWin.Value.Hwnd, ct).ConfigureAwait(false);
        await Task.Delay(150, ct).ConfigureAwait(false);
        await jab.ClickNodeCenterAsync(acepInput.Value, ct).ConfigureAwait(false);
        await Task.Delay(120, ct).ConfigureAwait(false);
        await jab.SendKeysToFocusedAsync("^a{BACKSPACE}", ct).ConfigureAwait(false);
        await Task.Delay(80, ct).ConfigureAwait(false);
        await jab.SendUnicodeTextAsync("ACEPTO", 35, ct).ConfigureAwait(false);
        await Task.Delay(350, ct).ConfigureAwait(false);

        string typed = (await jab.GetNodeTextAsync(acepInput.Value, ct).ConfigureAwait(false)).Trim();
        if (!string.Equals(typed, "ACEPTO", StringComparison.Ordinal))
        {
            _logger?.LogWarning("Texto en Declaración fue '{Typed}', reintentando con setTextContents + Unicode...", typed);
            await jab.SetTextContentsAsync(acepInput.Value, string.Empty, ct).ConfigureAwait(false);
            await Task.Delay(80, ct).ConfigureAwait(false);
            await jab.SendUnicodeTextAsync("ACEPTO", 50, ct).ConfigureAwait(false);
            await Task.Delay(350, ct).ConfigureAwait(false);
        }

        var firmarBtn = await jab.FindNodeAsync(declWin.Value, n =>
            n.Info.role_en_US == "push button" && n.Info.name.Equals("Firmar", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (firmarBtn is null || !firmarBtn.Value.Info.states_en_US.Contains("enabled", StringComparison.OrdinalIgnoreCase))
            return new SignExecutionResult(false, SignErrorCode.UI_AUTOMATION_ERROR, "El botón 'Firmar' no se habilitó tras escribir ACEPTO.");

        await jab.ClickNodeCenterAsync(firmarBtn.Value, ct).ConfigureAwait(false);

        // 7. Wait for PIN dialog (TSL validation runs between Firmar and PIN dialog)
        var pinWin = await jab.WaitForJavaWindowAsync(
            t => t.Contains("DNIe", StringComparison.OrdinalIgnoreCase) || t.Contains("PIN", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(35), ct).ConfigureAwait(false);

        if (pinWin is null)
            return new SignExecutionResult(false, SignErrorCode.TSL_VALIDATION_ERROR, "No apareció el diálogo de PIN tras confirmar la declaración (posible error de TSL o lector).");

        await jab.BringToForegroundAsync(pinWin.Value.Hwnd, ct).ConfigureAwait(false);
        return new SignExecutionResult(Success: true);
    }

    private async Task<JavaWindow?> EnsureMainWindowReadyAsync(JabSession jab, TimeSpan timeout, CancellationToken ct)
    {
        // Recover cleanly if a previous run left residual modal dialogs open
        await CleanupResidualDialogsAsync(jab, ct).ConfigureAwait(false);

        var existing = await jab.FindJavaWindowAsync(
            t => t.Contains("ReFirma PDF", StringComparison.OrdinalIgnoreCase) &&
                 !t.Contains("Iniciando", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);

        if (existing is null)
        {
            _logger?.LogInformation("Lanzando ReFirma PDF desde {Path}", _appRefPath);
            Process.Start(new ProcessStartInfo { FileName = _appRefPath, UseShellExecute = true });

            // Wait for splash 'Iniciando la aplicación' if it appears, then wait for it to disappear
            await Task.Delay(1500, ct).ConfigureAwait(false);
            await jab.WaitForWindowToDisappearAsync(
                t => t.Contains("Iniciando", StringComparison.OrdinalIgnoreCase),
                timeout, ct).ConfigureAwait(false);

            existing = await jab.WaitForJavaWindowAsync(
                t => t.Contains("ReFirma PDF", StringComparison.OrdinalIgnoreCase) &&
                     !t.Contains("Iniciando", StringComparison.OrdinalIgnoreCase),
                timeout, ct).ConfigureAwait(false);
        }

        return existing;
    }

    private async Task CleanupResidualDialogsAsync(JabSession jab, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            var residual = await jab.FindJavaWindowAsync(t =>
                t.Contains("Parámetros de firma", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Declaración", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Seleccionar directorio", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("DNIe", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("ReFirma PDF", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);

            if (residual is null)
                return;

            _logger?.LogWarning("Cerrando diálogo residual de ReFirma: {Title}", residual.Value.Title);
            await TryClickButtonAsync(jab, residual.Value, "Cancelar", ct).ConfigureAwait(false);
            await TryClickButtonAsync(jab, residual.Value, "Aceptar", ct).ConfigureAwait(false);
            await Task.Delay(350, ct).ConfigureAwait(false);
        }
    }

    private async Task<JavaWindow?> OpenBatchDialogAsync(JabSession jab, JavaWindow mainWin, CancellationToken ct)
    {
        var alreadyOpen = await jab.FindJavaWindowAsync(
            t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (alreadyOpen is not null)
            return alreadyOpen;

        await jab.BringToForegroundAsync(mainWin.Hwnd, ct).ConfigureAwait(false);
        await Task.Delay(250, ct).ConfigureAwait(false);

        var archivoMenu = await jab.FindNodeAsync(mainWin, n =>
            n.Info.role_en_US == "menu" && n.Info.name.Equals("Archivo", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (archivoMenu is null) return null;

        await jab.DoAccessibleActionAsync(archivoMenu.Value, "hacer clic", ct).ConfigureAwait(false);
        await Task.Delay(350, ct).ConfigureAwait(false);

        var batchItem = await jab.FindNodeAsync(mainWin, n =>
            n.Info.role_en_US == "menu item" && n.Info.name.Contains("Firmar en lote", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);

        if (batchItem is null) return null;
        if (batchItem.Value.Info.width <= 0)
        {
            await jab.ClickNodeCenterAsync(archivoMenu.Value, ct).ConfigureAwait(false);
            await Task.Delay(350, ct).ConfigureAwait(false);
            batchItem = await jab.FindNodeAsync(mainWin, n =>
                n.Info.role_en_US == "menu item" && n.Info.name.Contains("Firmar en lote", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
            if (batchItem is null || batchItem.Value.Info.width <= 0) return null;
        }

        await jab.ClickNodeCenterAsync(batchItem.Value, ct).ConfigureAwait(false);

        return await jab.WaitForJavaWindowAsync(
            t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    private async Task<bool> SetFolderViaChooserAsync(
        JabSession jab,
        JavaWindow batchWin,
        string buttonDescContains,
        string folderPath,
        CancellationToken ct)
    {
        var btn = await jab.FindNodeAsync(batchWin, n =>
            n.Info.role_en_US == "push button" &&
            n.Info.description.Contains(buttonDescContains, StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (btn is null) return false;

        await jab.ClickNodeCenterAsync(btn.Value, ct).ConfigureAwait(false);

        var chooser = await jab.WaitForJavaWindowAsync(
            t => t.Contains("Seleccionar directorio", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (chooser is null) return false;

        await Task.Delay(350, ct).ConfigureAwait(false);

        var pathInput = await jab.FindNodeAsync(chooser.Value, n =>
            n.Info.role_en_US == "text" && n.Info.name.Contains("Nombre de carpeta", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (pathInput is null) return false;

        bool setOk = await jab.SetTextContentsAsync(pathInput.Value, folderPath, ct).ConfigureAwait(false);
        if (!setOk) return false;
        await Task.Delay(200, ct).ConfigureAwait(false);

        var openBtn = await jab.FindNodeAsync(chooser.Value, n =>
            n.Info.role_en_US == "push button" && n.Info.name.Equals("Abrir", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (openBtn is null) return false;

        await jab.ClickNodeCenterAsync(openBtn.Value, ct).ConfigureAwait(false);
        await Task.Delay(450, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> SelectCertificateAsync(
        JabSession jab,
        JavaWindow batchWin,
        CertificateSelector selector,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var listNode = await jab.FindNodeAsync(batchWin, n => n.Info.role_en_US == "list" && n.Info.width > 0, ct).ConfigureAwait(false);
            if (listNode is not null)
            {
                var labels = await jab.FindAllNodesAsync(batchWin, n =>
                    n.Info.role_en_US == "label" && n.Info.width > 0 && n.Info.y >= listNode.Value.Info.y && n.Info.y <= listNode.Value.Info.y + listNode.Value.Info.height, ct).ConfigureAwait(false);

                var match = labels.FirstOrDefault(l => JabSession.MatchesCertificate(l.Info.name, selector));
                if (match.Ac != IntPtr.Zero)
                {
                    await jab.ClickNodeCenterAsync(match, ct).ConfigureAwait(false);
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    return true;
                }
            }

            if (attempt == 0)
            {
                await TryClickButtonAsync(jab, batchWin, "Actualizar", ct).ConfigureAwait(false);
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    private async Task ConfigureBatchOptionsAsync(JabSession jab, JavaWindow batchWin, SignJobPayload payload, CancellationToken ct)
    {
        await EnsureCheckBoxStateAsync(jab, batchWin, "Sobrescribir", payload.Overwrite, ct).ConfigureAwait(false);
        await EnsureCheckBoxStateAsync(jab, batchWin, "Firma visible", payload.Signature.Visible, ct).ConfigureAwait(false);
        await EnsureCheckBoxStateAsync(jab, batchWin, "Certificar los documentos", payload.Signature.CertifyDocuments, ct).ConfigureAwait(false);

        if (payload.Signature.Visible)
        {
            // Stamp image selection (defaults to index 0 if omitted)
            if (payload.Signature.StampIndex is int stampIdx && stampIdx >= 0)
            {
                var stamps = await jab.FindAllNodesAsync(batchWin, n =>
                    n.Info.role_en_US == "toggle button" && n.Info.width > 0 && n.Info.y > 300 && n.Info.y < 380, ct).ConfigureAwait(false);
                if (stampIdx < stamps.Count && !stamps[stampIdx].Info.states_en_US.Contains("checked", StringComparison.OrdinalIgnoreCase))
                {
                    await jab.ClickNodeCenterAsync(stamps[stampIdx], ct).ConfigureAwait(false);
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
            }

            // Position (XY or GRID)
            await ConfigurePositionAsync(jab, batchWin, payload.Signature.Position, ct).ConfigureAwait(false);
        }

        // Signature Level (PAdES_B, PAdES_T, PAdES_LTA)
        if (!string.IsNullOrWhiteSpace(payload.Signature.Level) &&
            !payload.Signature.Level.Equals("PAdES_B", StringComparison.OrdinalIgnoreCase))
        {
            await SelectComboItemAsync(jab, batchWin, comboPredicate: n => n.Info.role_en_US == "combo box" && n.Info.y < 320, payload.Signature.Level, ct).ConfigureAwait(false);
        }

        // Reason (Motivo) - 5 predefined options in ReFirma PDF 1.6
        if (!string.IsNullOrWhiteSpace(payload.Signature.Reason) &&
            !payload.Signature.Reason.Equals("Soy el autor del documento", StringComparison.OrdinalIgnoreCase))
        {
            await SelectComboItemAsync(jab, batchWin, comboPredicate: n => n.Info.role_en_US == "combo box" && n.Info.y > 350, payload.Signature.Reason, ct).ConfigureAwait(false);
        }
    }

    private async Task ConfigurePositionAsync(JabSession jab, JavaWindow batchWin, PositionOptions pos, CancellationToken ct)
    {
        var posBtn = await jab.FindNodeAsync(batchWin, n =>
            n.Info.role_en_US == "push button" && n.Info.description.Contains("posición", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (posBtn is null) return;

        await jab.ClickNodeCenterAsync(posBtn.Value, ct).ConfigureAwait(false);
        await Task.Delay(400, ct).ConfigureAwait(false);

        if (pos.Mode.Equals("XY", StringComparison.OrdinalIgnoreCase) && pos.X is int x && pos.Y is int y)
        {
            var xyItem = await jab.FindNodeAsync(batchWin, n =>
                n.Info.role_en_US == "menu item" && n.Info.name.Contains("X / Y", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
            if (xyItem is null || xyItem.Value.Info.width <= 0) return;

            await jab.ClickNodeCenterAsync(xyItem.Value, ct).ConfigureAwait(false);
            await FillCoordinatePromptAsync(jab, x.ToString(), ct).ConfigureAwait(false);
            await FillCoordinatePromptAsync(jab, y.ToString(), ct).ConfigureAwait(false);
        }
        else if (pos.GridIndex is int gridIdx and >= 0 and <= 14)
        {
            var radios = await jab.FindAllNodesAsync(batchWin, n =>
                n.Info.role_en_US == "radio button" && n.Info.width > 0, ct).ConfigureAwait(false);
            if (gridIdx < radios.Count)
            {
                await jab.ClickNodeCenterAsync(radios[gridIdx], ct).ConfigureAwait(false);
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task FillCoordinatePromptAsync(JabSession jab, string value, CancellationToken ct)
    {
        var promptWin = await jab.WaitForJavaWindowAsync(
            t => t.Equals("ReFirma PDF", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (promptWin is null)
            throw new InvalidOperationException("No apareció el modal de coordenada X/Y.");

        await Task.Delay(250, ct).ConfigureAwait(false);
        var input = await jab.FindNodeAsync(promptWin.Value, n => n.Info.role_en_US == "text", ct).ConfigureAwait(false);
        var okBtn = await jab.FindNodeAsync(promptWin.Value, n =>
            n.Info.role_en_US == "push button" && n.Info.name.Equals("Aceptar", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);

        if (input is null || okBtn is null)
            throw new InvalidOperationException("Controles del modal de coordenada X/Y no encontrados.");

        await jab.SetTextContentsAsync(input.Value, value, ct).ConfigureAwait(false);
        await Task.Delay(150, ct).ConfigureAwait(false);
        await jab.ClickNodeCenterAsync(okBtn.Value, ct).ConfigureAwait(false);
        await Task.Delay(350, ct).ConfigureAwait(false);
    }

    private static async Task EnsureCheckBoxStateAsync(JabSession jab, JavaWindow win, string nameContains, bool desiredChecked, CancellationToken ct)
    {
        var cb = await jab.FindNodeAsync(win, n =>
            n.Info.role_en_US == "check box" && n.Info.name.Contains(nameContains, StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (cb is null) return;

        bool isChecked = cb.Value.Info.states_en_US.Contains("checked", StringComparison.OrdinalIgnoreCase);
        if (isChecked != desiredChecked)
        {
            await jab.ClickNodeCenterAsync(cb.Value, ct).ConfigureAwait(false);
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    private static async Task SelectComboItemAsync(
        JabSession jab,
        JavaWindow win,
        Func<JabNode, bool> comboPredicate,
        string itemText,
        CancellationToken ct)
    {
        var combo = await jab.FindNodeAsync(win, comboPredicate, ct).ConfigureAwait(false);
        if (combo is null || !combo.Value.Info.states_en_US.Contains("enabled", StringComparison.OrdinalIgnoreCase))
            return;

        await jab.DoAccessibleActionAsync(combo.Value, "togglePopup", ct).ConfigureAwait(false);
        await Task.Delay(300, ct).ConfigureAwait(false);

        var option = await jab.FindNodeAsync(win, n =>
            n.Info.role_en_US == "label" &&
            n.Info.width > 0 &&
            n.Info.name.Contains(itemText, StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);

        if (option is not null)
        {
            await jab.ClickNodeCenterAsync(option.Value, ct).ConfigureAwait(false);
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        else
        {
            await jab.DoAccessibleActionAsync(combo.Value, "togglePopup", ct).ConfigureAwait(false);
        }
    }

    private static async Task TryClickButtonAsync(JabSession jab, JavaWindow win, string buttonName, CancellationToken ct)
    {
        var btn = await jab.FindNodeAsync(win, n =>
            n.Info.role_en_US == "push button" && n.Info.name.Equals(buttonName, StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        if (btn is not null)
            await jab.ClickNodeCenterAsync(btn.Value, ct).ConfigureAwait(false);
    }

    private static async Task<SignExecutionResult> ExecuteMockAsync(SignJobContext context, CancellationToken ct)
    {
        context.ReportProgress(SignJobStatus.LAUNCHING_APP, "Iniciando ReFirma PDF (simulación)...");
        await Task.Delay(120, ct).ConfigureAwait(false);

        context.ReportProgress(SignJobStatus.CONFIGURING_BATCH, "Configurando parámetros de firma en lote (simulación)...");
        await Task.Delay(120, ct).ConfigureAwait(false);

        context.ReportProgress(SignJobStatus.AWAITING_DECLARATION, "Aceptando declaración legal (simulación)...");
        await Task.Delay(80, ct).ConfigureAwait(false);

        context.ReportProgress(SignJobStatus.WAITING_USER_PIN, "Esperando ingreso manual de PIN (simulación)...");
        await Task.Delay(120, ct).ConfigureAwait(false);

        context.ReportProgress(SignJobStatus.SIGNING_IN_PROGRESS, "Generando documentos firmados (simulación)...");
        foreach (var inFile in context.InputPdfFiles)
        {
            ct.ThrowIfCancellationRequested();
            string dest = Path.Combine(context.OutputDirectory, Path.GetFileName(inFile));
            File.Copy(inFile, dest, overwrite: true);
        }

        return new SignExecutionResult(Success: true);
    }

    private async Task ForceCloseReFirmaAsync(JabSession jab)
    {
        try
        {
            var pids = await jab.GetReFirmaPidsAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (uint pid in pids)
            {
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    _logger?.LogInformation("Cerrando ReFirma PDF (PID {Pid}) tras fallo/cancelación.", pid);
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "No se pudo cerrar el proceso ReFirma PDF (PID {Pid}).", pid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error enumerando procesos de ReFirma PDF para cierre tras fallo.");
        }
    }

    public void Dispose()
    {
        if (_staDispatcher.IsValueCreated)
            _staDispatcher.Value.Dispose();
    }
}
