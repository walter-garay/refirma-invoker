using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using SignerBridge.Core.Security;

namespace SignerBridge.Host.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRunValueName = "SignerBridge";

    private readonly NotifyIcon _notifyIcon;
    private readonly DpapiTokenStore _tokenStore;
    private readonly string _logsDirectory;
    private readonly Func<Task> _shutdownHostAsync;
    private readonly ToolStripMenuItem _startupMenuItem;

    public TrayApplicationContext(
        string endpointUrl,
        DpapiTokenStore tokenStore,
        string logsDirectory,
        Func<Task> shutdownHostAsync)
    {
        _tokenStore = tokenStore;
        _logsDirectory = logsDirectory;
        _shutdownHostAsync = shutdownHostAsync;

        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem($"Activo en {endpointUrl}") { Enabled = false };
        var copyTokenItem = new ToolStripMenuItem("Copiar Token de Pairing", null, OnCopyToken);
        var openLogsItem = new ToolStripMenuItem("Abrir carpeta de logs", null, OnOpenLogs);

        _startupMenuItem = new ToolStripMenuItem("Iniciar con Windows", null, OnToggleStartup)
        {
            Checked = IsAutoStartEnabled()
        };

        var exitItem = new ToolStripMenuItem("Salir", null, OnExit);

        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(copyTokenItem);
        menu.Items.Add(openLogsItem);
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = $"SignerBridge ({endpointUrl})",
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    private void OnCopyToken(object? sender, EventArgs e)
    {
        string token = _tokenStore.GetOrCreateToken();
        Clipboard.SetText(token);
        _notifyIcon.ShowBalloonTip(2500, "SignerBridge", "Token de pairing copiado al portapapeles.", ToolTipIcon.Info);
    }

    private void OnOpenLogs(object? sender, EventArgs e)
    {
        Directory.CreateDirectory(_logsDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = _logsDirectory,
            UseShellExecute = true
        });
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        bool enable = !_startupMenuItem.Checked;
        SetAutoStart(enable);
        _startupMenuItem.Checked = IsAutoStartEnabled();
    }

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: false);
        return key?.GetValue(AppRunValueName) is string val && !string.IsNullOrWhiteSpace(val);
    }

    private static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true)
                     ?? Registry.CurrentUser.CreateSubKey(RunRegistryKey, writable: true);
        if (enable)
        {
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(AppRunValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppRunValueName, throwOnMissingValue: false);
        }
    }

    private async void OnExit(object? sender, EventArgs e)
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        try
        {
            await _shutdownHostAsync();
        }
        finally
        {
            ExitThread();
        }
    }
}
