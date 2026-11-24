using System.Diagnostics;

namespace SignerBridge.Providers.ReFirmaPdf.Jab;

internal static class JabAccessibilityHelper
{
    private const string AccessBridgeDirective = "assistive_technologies=com.sun.java.accessibility.AccessBridge";

    /// <summary>
    /// Ensures Java Access Bridge is enabled for the current user's JRE 8 environment
    /// before launching or attaching to ReFirma PDF.
    /// </summary>
    public static void EnsureJabEnabled(string jreBinPath)
    {
        string userAccessProps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".accessibility.properties");

        if (File.Exists(userAccessProps))
        {
            string content = File.ReadAllText(userAccessProps);
            if (content.Contains(AccessBridgeDirective, StringComparison.OrdinalIgnoreCase) &&
                !content.Contains("#" + AccessBridgeDirective, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        string jabSwitch = Path.Combine(jreBinPath, "jabswitch.exe");
        if (File.Exists(jabSwitch))
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = jabSwitch,
                Arguments = "-enable",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(5000);
        }
        else
        {
            File.WriteAllText(userAccessProps, $"{AccessBridgeDirective}\r\nscreen_magnifier_present=true\r\n");
        }
    }
}
