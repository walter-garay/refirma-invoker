using System.Text;
using SignerBridge.Core.Models;

namespace SignerBridge.Providers.ReFirmaPdf.Monitoring;

public readonly record struct LogInspectionResult(
    bool HasTerminalFinding,
    SignErrorCode ErrorCode = SignErrorCode.NONE,
    string? Detail = null,
    bool Cancelled = false);

/// <summary>
/// Reads newly appended lines from ReFirma's refirma-pdf.log starting from a byte offset
/// captured when the signing job began.
/// </summary>
public sealed class RefirmaLogWatcher
{
    private readonly string _logFilePath;
    private long _offset;

    public RefirmaLogWatcher(string logFilePath)
    {
        _logFilePath = logFilePath;
        _offset = File.Exists(_logFilePath) ? new FileInfo(_logFilePath).Length : 0;
    }

    public IReadOnlyList<string> ReadNewLines()
    {
        if (!File.Exists(_logFilePath))
            return [];

        var fi = new FileInfo(_logFilePath);
        if (fi.Length < _offset)
            _offset = 0; // Log rotated/truncated

        if (fi.Length == _offset)
            return [];

        using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(_offset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                lines.Add(line);
        }

        _offset = fs.Position;
        return lines;
    }

    public static LogInspectionResult InspectLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Contains("DniePeruSignPinDialog cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return new LogInspectionResult(
                    HasTerminalFinding: true,
                    ErrorCode: SignErrorCode.USER_CANCELLED_PIN,
                    Detail: "El usuario canceló el ingreso del PIN en ReFirma PDF.",
                    Cancelled: true);
            }

            if (line.Contains("PIN incorrecto", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("CKR_PIN_INCORRECT", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("CKR_PIN_LOCKED", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("tarjeta bloqueada", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Quedan 0 intentos", StringComparison.OrdinalIgnoreCase))
            {
                return new LogInspectionResult(
                    HasTerminalFinding: true,
                    ErrorCode: SignErrorCode.INVALID_PIN_OR_LOCKED,
                    Detail: ExtractLogMessage(line));
            }

            if (line.Contains("TSL", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("Exception", StringComparison.OrdinalIgnoreCase)))
            {
                return new LogInspectionResult(
                    HasTerminalFinding: true,
                    ErrorCode: SignErrorCode.TSL_VALIDATION_ERROR,
                    Detail: ExtractLogMessage(line));
            }

            if ((line.Contains(" ERROR ", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("RuntimeException:", StringComparison.OrdinalIgnoreCase)) &&
                !line.Contains("DniePeruSignPinDialog cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return new LogInspectionResult(
                    HasTerminalFinding: true,
                    ErrorCode: SignErrorCode.UI_AUTOMATION_ERROR,
                    Detail: ExtractLogMessage(line));
            }
        }

        return new LogInspectionResult(HasTerminalFinding: false);
    }

    private static string ExtractLogMessage(string line)
    {
        int dashIdx = line.IndexOf(" - ", StringComparison.Ordinal);
        return dashIdx >= 0 && dashIdx + 3 < line.Length
            ? line[(dashIdx + 3)..].Trim()
            : line.Trim();
    }
}
