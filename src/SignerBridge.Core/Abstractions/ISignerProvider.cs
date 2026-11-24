using SignerBridge.Core.Models;

namespace SignerBridge.Core.Abstractions;

public sealed record ProviderAvailability(bool Available, string? Detail = null);

public sealed record SignExecutionResult(
    bool Success,
    SignErrorCode ErrorCode = SignErrorCode.NONE,
    string? ErrorDetail = null,
    bool Cancelled = false,
    bool TimedOut = false);

public sealed class SignJobContext
{
    public required string JobId { get; init; }
    public required string InputDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public required SignJobPayload Payload { get; init; }
    public required IReadOnlyList<string> InputPdfFiles { get; init; }
    public required Action<SignJobStatus, string> ReportProgress { get; init; }
}

public interface ISignerProvider
{
    string ProviderId { get; }
    string DisplayName { get; }

    Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken ct = default);
    Task<SignExecutionResult> ExecuteBatchSignAsync(SignJobContext context, CancellationToken ct);
}
