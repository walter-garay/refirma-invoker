using Microsoft.Win32;

namespace SignerBridge.Host.SystemIntegration;

/// <summary>
/// Registers the custom URI scheme (signer-bridge://) under HKCU so a web app
/// can wake up the desktop agent on demand without requiring admin privileges.
/// </summary>
public static class UriProtocolRegistrar
{
    public const string Scheme = "signer-bridge";
    private const string ClassesPath = @"Software\Classes\" + Scheme;

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ClassesPath, writable: false);
        return key is not null;
    }

    public static void EnsureRegistered(string? executablePath = null)
    {
        string exe = executablePath ?? Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exe))
            return;

        using var root = Registry.CurrentUser.CreateSubKey(ClassesPath, writable: true);
        root.SetValue(string.Empty, "URL:SignerBridge Protocol");
        root.SetValue("URL Protocol", string.Empty);

        using var iconKey = root.CreateSubKey("DefaultIcon", writable: true);
        iconKey.SetValue(string.Empty, $"\"{exe}\",0");

        using var cmdKey = root.CreateSubKey(@"shell\open\command", writable: true);
        cmdKey.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(ClassesPath, throwOnMissingSubKey: false);
    }
}
