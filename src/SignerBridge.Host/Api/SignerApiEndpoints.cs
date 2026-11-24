using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SignerBridge.Core.Configuration;
using SignerBridge.Core.Jobs;
using SignerBridge.Core.Models;
using SignerBridge.Core.Storage;
using SignerBridge.Host.SystemIntegration;

namespace SignerBridge.Host.Api;

public static class SignerApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void MapSignerApi(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapGet("/health", async (JobManager jobs, CancellationToken ct) =>
        {
            var providerInfos = new List<ProviderInfo>();
            foreach (var p in jobs.GetProviders())
            {
                var avail = await p.CheckAvailabilityAsync(ct);
                providerInfos.Add(new ProviderInfo(p.ProviderId, p.DisplayName, avail.Available, avail.Detail));
            }

            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            return Results.Ok(new HealthResponse("SignerBridge", version, jobs.IsBusy, jobs.ActiveJobId, providerInfos));
        });

        v1.MapPost("/sign/jobs", async (HttpRequest req, JobManager jobs, IArchiveService archiveService, DesktopSessionMonitor sessionMonitor, CancellationToken ct) =>
        {
            if (sessionMonitor.IsSessionLocked)
                return Results.StatusCode(StatusCodes.Status423Locked);

            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "INVALID_REQUEST", detail = "Se requiere multipart/form-data." });

            var form = await req.ReadFormAsync(ct);
            var payloadJson = form["payload"].FirstOrDefault();
            SignJobPayload? payload;
            try
            {
                payload = string.IsNullOrWhiteSpace(payloadJson)
                    ? new SignJobPayload()
                    : JsonSerializer.Deserialize<SignJobPayload>(payloadJson, JsonOpts);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = "INVALID_PAYLOAD_JSON", detail = ex.Message });
            }

            if (payload is null)
                return Results.BadRequest(new { error = "INVALID_REQUEST", detail = "Payload nulo." });

            if (!jobs.TryGetProvider(payload.Provider, out _))
                return Results.BadRequest(new { error = "UNKNOWN_PROVIDER", detail = $"Proveedor desconocido: {payload.Provider}" });

            string jobId = jobs.CreateJobWorkspace(out string inputDir, out string outputDir);
            try
            {
                var inputPdfs = new List<string>();
                var archiveFile = form.Files.GetFile("archive");
                if (archiveFile is not null)
                {
                    await using var zipStream = archiveFile.OpenReadStream();
                    inputPdfs.AddRange(archiveService.ExtractPdfZipSafely(zipStream, inputDir));
                }
                else
                {
                    foreach (var file in form.Files)
                    {
                        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                            continue;

                        await using var pdfStream = file.OpenReadStream();
                        inputPdfs.Add(await archiveService.SavePdfStreamAsync(pdfStream, file.FileName, inputDir, ct));
                    }
                }

                if (inputPdfs.Count == 0)
                {
                    jobs.CleanupJobDirectory(jobId);
                    return Results.BadRequest(new { error = "NO_PDF_FILES", detail = "No se enviaron archivos PDF válidos." });
                }

                var created = jobs.EnqueueJob(jobId, inputDir, outputDir, inputPdfs, payload);
                return Results.Accepted($"/v1/sign/jobs/{jobId}", created);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                jobs.CleanupJobDirectory(jobId);
                return Results.BadRequest(new { error = "INVALID_FILES", detail = ex.Message });
            }
            catch
            {
                jobs.CleanupJobDirectory(jobId);
                throw;
            }
        }).DisableAntiforgery();

        v1.MapGet("/sign/jobs/{jobId}", (string jobId, JobManager jobs) =>
        {
            var job = jobs.GetJob(jobId);
            return job is null ? Results.NotFound(new { error = "JOB_NOT_FOUND" }) : Results.Ok(job);
        });

        v1.MapGet("/sign/jobs/{jobId}/result", async (
            string jobId,
            JobManager jobs,
            IOptions<SignerBridgeOptions> options,
            HttpResponse response,
            CancellationToken ct) =>
        {
            var job = jobs.GetJob(jobId);
            if (job is null)
                return Results.NotFound(new { error = "JOB_NOT_FOUND" });

            if (job.Status != SignJobStatus.SUCCEEDED)
                return Results.Conflict(new { error = "JOB_NOT_COMPLETED", status = job.Status, detail = job.ErrorDetail ?? job.Message });

            var zipPath = jobs.GetResultZipPath(jobId);
            if (zipPath is null)
                return Results.NotFound(new { error = "RESULT_NOT_AVAILABLE" });

            byte[] zipBytes = await File.ReadAllBytesAsync(zipPath, ct);
            if (options.Value.CleanupAfterResultDownload)
                jobs.DeleteJob(jobId);

            return Results.File(zipBytes, "application/zip", $"signed-{jobId}.zip");
        });

        v1.MapDelete("/sign/jobs/{jobId}", (string jobId, JobManager jobs) =>
        {
            return jobs.DeleteJob(jobId) ? Results.NoContent() : Results.NotFound(new { error = "JOB_NOT_FOUND" });
        });

        v1.Map("/sign/jobs/{jobId}/events", async (HttpContext ctx, string jobId, JobManager jobs) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (jobs.GetJob(jobId) is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await foreach (var ev in jobs.SubscribeEventsAsync(jobId, ctx.RequestAborted))
            {
                var json = JsonSerializer.Serialize(ev);
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ctx.RequestAborted);
            }

            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        });
    }
}
