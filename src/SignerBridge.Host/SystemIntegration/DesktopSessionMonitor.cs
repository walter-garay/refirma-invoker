using Microsoft.Win32;

namespace SignerBridge.Host.SystemIntegration;

/// <summary>
/// Tracks whether the interactive Windows user session is currently locked.
/// </summary>
public sealed class DesktopSessionMonitor : IDisposable
{
    private volatile bool _isLocked;

    public DesktopSessionMonitor()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public bool IsSessionLocked => _isLocked;

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            _isLocked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
            _isLocked = false;
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
