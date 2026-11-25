namespace SignerBridge.Core.Configuration;

public sealed class SignerBridgeOptions
{
    public const string SectionName = "SignerBridge";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 48915;
    public string[] AllowedOrigins { get; set; } =
    [
        "http://localhost",
        "https://localhost",
        "http://127.0.0.1",
        "https://sigvin.sistemasudh.com"
    ];
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SignerBridge");
    public int JobTtlMinutes { get; set; } = 15;
    public long MaxUploadBytes { get; set; } = 512L * 1024 * 1024;
    public bool CleanupAfterResultDownload { get; set; } = true;

    public Dictionary<string, ProviderConfig> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProviderConfig
{
    public bool Enabled { get; set; } = true;
    public string? ExecutablePath { get; set; }
    public string? JrePath { get; set; }
    public string? LogFilePath { get; set; }
    public bool MockExecution { get; set; } = false;
}
