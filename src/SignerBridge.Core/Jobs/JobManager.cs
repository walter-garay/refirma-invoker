using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignerBridge.Core.Abstractions;
using SignerBridge.Core.Configuration;
using SignerBridge.Core.Models;
using SignerBridge.Core.Storage;

namespace SignerBridge.Core.Jobs;

public sealed class JobManager : IDisposable
{
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new();
    private readonly SemaphoreSlim _signingLock = new(1, 1);
    private readonly ISignerProviderFactory _providerFactory;
    private readonly IArchiveService _archiveService;
    private readonly SignerBridgeOptions _options;
    private readonly ILogger<JobManager> _logger;
    private readonly Timer _cleanupTimer;
    private string? _activeJobId;

    public JobManager(
        ISignerProviderFactory providerFactory,
        IArchiveService archiveService,
        IOptions<SignerBridgeOptions> options,
        ILogger<JobManager> logger)
    {
        _providerFactory = providerFactory;
        _archiveService = archiveService;
        _options = options.Value;
        _logger = logger;

        Directory.CreateDirectory(GetJobsRoot());
        _cleanupTimer = new Timer(_ => CleanupExpiredJobs(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public bool IsBusy => _signingLock.CurrentCount == 0;
    public string? ActiveJobId => Volatile.Read(ref _activeJobId);

    public IReadOnlyCollection<ISignerProvider> GetProviders() => _providerFactory.GetAll();

    public bool TryGetProvider(string providerId, out ISignerProvider provider)
        => _providerFactory.TryGetProvider(providerId, out provider);

    public string CreateJobWorkspace(out string inputDir, out string outputDir)
    {
        string jobId = Guid.NewGuid().ToString("N");
        string jobRoot = Path.Combine(GetJobsRoot(), jobId);
        inputDir = Path.Combine(jobRoot, "input");
        outputDir = Path.Combine(jobRoot, "output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        return jobId;
    }

    public SignJobResponse EnqueueJob(
        string jobId,
        string inputDir,
        string outputDir,
        IReadOnlyList<string> inputPdfFiles,
        SignJobPayload payload)
    {
        if (!_providerFactory.TryGetProvider(payload.Provider, out var provider))
            throw new ArgumentException($"Proveedor de firma no soportado: '{payload.Provider}'.");

        var now = DateTimeOffset.UtcNow;
        var record = new JobRecord
        {
            JobId = jobId,
            ProviderId = provider.ProviderId,
            InputDir = inputDir,
            OutputDir = outputDir,
            ResultZipPath = Path.Combine(GetJobsRoot(), jobId, "result.zip"),
            Payload = payload,
            InputPdfFiles = inputPdfFiles,
            Status = SignJobStatus.QUEUED,
            Message = "Trabajo encolado esperando turno de firma.",
            CreatedAt = now,
            UpdatedAt = now
        };

        _jobs[jobId] = record;
        record.PublishEvent(new SignJobEvent(jobId, record.Status, record.Message, now, FilesCount: inputPdfFiles.Count));

        _ = Task.Run(() => ProcessJobAsync(record, provider));
        return record.ToResponse();
    }

    public SignJobResponse? GetJob(string jobId)
        => _jobs.TryGetValue(jobId, out var rec) ? rec.ToResponse() : null;

    public string? GetResultZipPath(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var rec) && rec.Status == SignJobStatus.SUCCEEDED && File.Exists(rec.ResultZipPath))
            return rec.ResultZipPath;
        return null;
    }

    public async IAsyncEnumerable<SignJobEvent> SubscribeEventsAsync(
        string jobId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_jobs.TryGetValue(jobId, out var rec))
            yield break;

        var (reader, subId, history) = rec.Subscribe();
        try
        {
            foreach (var ev in history)
                yield return ev;

            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var ev))
                {
                    yield return ev;
                    if (IsTerminal(ev.Status))
                        yield break;
                }
            }
        }
        finally
        {
            rec.Unsubscribe(subId);
        }
    }

    public bool DeleteJob(string jobId)
    {
        if (!_jobs.TryRemove(jobId, out var rec))
            return false;

        rec.Cts.Cancel();
        CleanupJobDirectory(rec.JobId);
        return true;
    }

    private async Task ProcessJobAsync(JobRecord rec, ISignerProvider provider)
    {
        await _signingLock.WaitAsync(rec.Cts.Token).ConfigureAwait(false);
        Volatile.Write(ref _activeJobId, rec.JobId);
        try
        {
            _logger.LogInformation("Iniciando job {JobId} con proveedor {ProviderId} ({Count} PDFs).",
                rec.JobId, provider.ProviderId, rec.InputPdfFiles.Count);

            var context = new SignJobContext
            {
                JobId = rec.JobId,
                InputDirectory = rec.InputDir,
                OutputDirectory = rec.OutputDir,
                Payload = rec.Payload,
                InputPdfFiles = rec.InputPdfFiles,
                ReportProgress = (status, msg) =>
                {
                    rec.UpdateState(status, msg);
                    rec.PublishEvent(new SignJobEvent(rec.JobId, status, msg, DateTimeOffset.UtcNow));
                }
            };

            var result = await provider.ExecuteBatchSignAsync(context, rec.Cts.Token).ConfigureAwait(false);

            if (result.Success)
            {
                await _archiveService.BuildResultZipAsync(
                    rec.JobId, provider.ProviderId, rec.OutputDir, rec.ResultZipPath, rec.Cts.Token).ConfigureAwait(false);

                int outCount = Directory.Exists(rec.OutputDir)
                    ? Directory.GetFiles(rec.OutputDir, "*.pdf", SearchOption.TopDirectoryOnly).Length
                    : 0;

                rec.OutputFilesCount = outCount;
                rec.UpdateState(SignJobStatus.SUCCEEDED, $"Firma completada exitosamente ({outCount} documento(s)).");
                rec.PublishEvent(new SignJobEvent(
                    rec.JobId, SignJobStatus.SUCCEEDED, rec.Message, DateTimeOffset.UtcNow, FilesCount: outCount));

                _logger.LogInformation("Job {JobId} completado con éxito ({Count} PDFs).", rec.JobId, outCount);
            }
            else if (result.Cancelled)
            {
                rec.UpdateState(SignJobStatus.CANCELLED, result.ErrorDetail ?? "Operación cancelada por el usuario.", result.ErrorCode, result.ErrorDetail);
                rec.PublishEvent(new SignJobEvent(
                    rec.JobId, SignJobStatus.CANCELLED, rec.Message, DateTimeOffset.UtcNow, result.ErrorCode, result.ErrorDetail));
            }
            else if (result.TimedOut)
            {
                rec.UpdateState(SignJobStatus.TIMED_OUT, result.ErrorDetail ?? "Tiempo de espera agotado.", result.ErrorCode, result.ErrorDetail);
                rec.PublishEvent(new SignJobEvent(
                    rec.JobId, SignJobStatus.TIMED_OUT, rec.Message, DateTimeOffset.UtcNow, result.ErrorCode, result.ErrorDetail));
            }
            else
            {
                rec.UpdateState(SignJobStatus.FAILED, result.ErrorDetail ?? "Error durante la firma.", result.ErrorCode, result.ErrorDetail);
                rec.PublishEvent(new SignJobEvent(
                    rec.JobId, SignJobStatus.FAILED, rec.Message, DateTimeOffset.UtcNow, result.ErrorCode, result.ErrorDetail));
            }
        }
        catch (OperationCanceledException)
        {
            rec.UpdateState(SignJobStatus.CANCELLED, "Trabajo cancelado.", SignErrorCode.USER_CANCELLED_PIN);
            rec.PublishEvent(new SignJobEvent(
                rec.JobId, SignJobStatus.CANCELLED, rec.Message, DateTimeOffset.UtcNow, SignErrorCode.USER_CANCELLED_PIN));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado procesando job {JobId}", rec.JobId);
            rec.UpdateState(SignJobStatus.FAILED, "Error interno ejecutando la firma.", SignErrorCode.INTERNAL_ERROR, ex.Message);
            rec.PublishEvent(new SignJobEvent(
                rec.JobId, SignJobStatus.FAILED, rec.Message, DateTimeOffset.UtcNow, SignErrorCode.INTERNAL_ERROR, ex.Message));
        }
        finally
        {
            Volatile.Write(ref _activeJobId, null);
            _signingLock.Release();
        }
    }

    private void CleanupExpiredJobs()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-_options.JobTtlMinutes);
        foreach (var kvp in _jobs)
        {
            if (IsTerminal(kvp.Value.Status) && kvp.Value.UpdatedAt < cutoff)
            {
                if (_jobs.TryRemove(kvp.Key, out _))
                    CleanupJobDirectory(kvp.Key);
            }
        }
    }

    public void CleanupJobDirectory(string jobId)
    {
        try
        {
            string jobRoot = Path.Combine(GetJobsRoot(), jobId);
            if (Directory.Exists(jobRoot))
                Directory.Delete(jobRoot, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo limpiar la carpeta temporal del job {JobId}", jobId);
        }
    }

    private string GetJobsRoot() => Path.Combine(_options.DataDirectory, "jobs");

    private static bool IsTerminal(SignJobStatus s) =>
        s is SignJobStatus.SUCCEEDED or SignJobStatus.FAILED or SignJobStatus.CANCELLED or SignJobStatus.TIMED_OUT;

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _signingLock.Dispose();
    }

    private sealed class JobRecord
    {
        private readonly object _sync = new();
        private readonly List<SignJobEvent> _history = [];
        private readonly Dictionary<Guid, Channel<SignJobEvent>> _subscribers = [];

        public required string JobId { get; init; }
        public required string ProviderId { get; init; }
        public required string InputDir { get; init; }
        public required string OutputDir { get; init; }
        public required string ResultZipPath { get; init; }
        public required SignJobPayload Payload { get; init; }
        public required IReadOnlyList<string> InputPdfFiles { get; init; }
        public SignJobStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public int? OutputFilesCount { get; set; }
        public SignErrorCode ErrorCode { get; set; } = SignErrorCode.NONE;
        public string? ErrorDetail { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
        public CancellationTokenSource Cts { get; } = new();

        public void UpdateState(SignJobStatus status, string message, SignErrorCode errorCode = SignErrorCode.NONE, string? errorDetail = null)
        {
            lock (_sync)
            {
                Status = status;
                Message = message;
                ErrorCode = errorCode;
                ErrorDetail = errorDetail;
                UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        public void PublishEvent(SignJobEvent ev)
        {
            lock (_sync)
            {
                _history.Add(ev);
                foreach (var ch in _subscribers.Values)
                {
                    ch.Writer.TryWrite(ev);
                    if (IsTerminal(ev.Status))
                        ch.Writer.TryComplete();
                }
            }
        }

        public (ChannelReader<SignJobEvent> Reader, Guid SubId, List<SignJobEvent> History) Subscribe()
        {
            lock (_sync)
            {
                var id = Guid.NewGuid();
                var ch = Channel.CreateUnbounded<SignJobEvent>();
                if (!IsTerminal(Status))
                    _subscribers[id] = ch;
                else
                    ch.Writer.TryComplete();

                return (ch.Reader, id, [.. _history]);
            }
        }

        public void Unsubscribe(Guid subId)
        {
            lock (_sync)
            {
                if (_subscribers.Remove(subId, out var ch))
                    ch.Writer.TryComplete();
            }
        }

        public SignJobResponse ToResponse()
        {
            lock (_sync)
            {
                return new SignJobResponse(
                    JobId,
                    ProviderId,
                    Status,
                    Message,
                    InputPdfFiles.Count,
                    OutputFilesCount,
                    ErrorCode,
                    ErrorDetail,
                    CreatedAt,
                    UpdatedAt,
                    $"/v1/sign/jobs/{JobId}/events",
                    $"/v1/sign/jobs/{JobId}/result");
            }
        }
    }
}
