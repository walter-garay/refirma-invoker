using System.Text.Json.Serialization;

namespace SignerBridge.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignJobStatus
{
    QUEUED,
    LAUNCHING_APP,
    CONFIGURING_BATCH,
    AWAITING_DECLARATION,
    WAITING_USER_PIN,
    SIGNING_IN_PROGRESS,
    SUCCEEDED,
    FAILED,
    CANCELLED,
    TIMED_OUT
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignErrorCode
{
    NONE,
    APP_NOT_INSTALLED,
    CERT_NOT_FOUND,
    NO_PDF_FILES,
    USER_CANCELLED_PIN,
    INVALID_PIN_OR_LOCKED,
    TSL_VALIDATION_ERROR,
    UI_AUTOMATION_ERROR,
    TIMEOUT,
    INVALID_REQUEST,
    INTERNAL_ERROR
}

public sealed record SignJobPayload
{
    public string Provider { get; init; } = "refirma-pdf";
    public CertificateSelector Certificate { get; init; } = new();
    public SignatureOptions Signature { get; init; } = new();
    public bool Overwrite { get; init; } = false;
    public TimeoutOptions Timeouts { get; init; } = new();
}

public sealed record CertificateSelector
{
    public string MatchBy { get; init; } = "AUTO"; // AUTO | FULL_NAME | FIR | SN
    public string Query { get; init; } = string.Empty;
}

public sealed record SignatureOptions
{
    public bool Visible { get; init; } = true;
    public int? StampIndex { get; init; }
    public string Level { get; init; } = "PAdES_B";
    public bool CertifyDocuments { get; init; } = false;
    public string Reason { get; init; } = "Soy el autor del documento";
    public PositionOptions Position { get; init; } = new();
}

public sealed record PositionOptions
{
    public string Mode { get; init; } = "GRID"; // XY | GRID
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? GridIndex { get; init; }
}

public sealed record TimeoutOptions
{
    public int AppStartupSec { get; init; } = 45;
    public int UserPinSec { get; init; } = 180;
    public int SigningSec { get; init; } = 120;
}

public sealed record SignJobEvent(
    string JobId,
    SignJobStatus Status,
    string Message,
    DateTimeOffset Timestamp,
    SignErrorCode ErrorCode = SignErrorCode.NONE,
    string? ErrorDetail = null,
    int? FilesCount = null);

public sealed record SignJobResponse(
    string JobId,
    string Provider,
    SignJobStatus Status,
    string Message,
    int InputFilesCount,
    int? OutputFilesCount,
    SignErrorCode ErrorCode,
    string? ErrorDetail,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string EventsWsPath,
    string ResultPath);

public sealed record ProviderInfo(string ProviderId, string DisplayName, bool Available, string? Detail = null);

public sealed record HealthResponse(
    string Service,
    string Version,
    bool Busy,
    string? ActiveJobId,
    IReadOnlyList<ProviderInfo> Providers);

public sealed record SignedFileEntry(string FileName, long SizeBytes, string Sha256);

public sealed record SignResultManifest(
    string JobId,
    string Provider,
    DateTimeOffset CompletedAt,
    int FilesCount,
    IReadOnlyList<SignedFileEntry> Files);
