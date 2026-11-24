using System.Drawing;
using System.Windows.Forms;

namespace SignerBridge.Providers.ReFirmaPdf.Jab;

/// <summary>
/// Maintains a dedicated STA thread with an active WinForms message loop (Application.Run)
/// so WindowsAccessBridge-64.dll IPC messages are pumped continuously and reliably.
/// </summary>
internal sealed class JabStaDispatcher : IDisposable
{
    private readonly Thread _staThread;
    private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Form? _hiddenForm;
    private bool _disposed;

    public JabStaDispatcher(string jreBinPath)
    {
        JabNative.ConfigureJreBinDirectory(jreBinPath);

        _staThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "SignerBridge.JabStaPump"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    public Task<bool> EnsureInitializedAsync() => _readyTcs.Task;

    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken ct = default)
    {
        bool ok = await _readyTcs.Task.ConfigureAwait(false);
        if (!ok)
            throw new InvalidOperationException("Java Access Bridge (Windows_run) no pudo inicializarse.");

        if (_hiddenForm is null || _hiddenForm.IsDisposed)
            throw new ObjectDisposedException(nameof(JabStaDispatcher));

        ct.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hiddenForm.BeginInvoke(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            try
            {
                tcs.TrySetResult(action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    public Task InvokeAsync(Action action, CancellationToken ct = default)
        => InvokeAsync(() => { action(); return true; }, ct);

    private void RunMessageLoop()
    {
        try
        {
            _hiddenForm = new Form
            {
                Width = 1,
                Height = 1,
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000)
            };

            _hiddenForm.Shown += (_, _) =>
            {
                bool ok = JabNative.Windows_run();
                _readyTcs.TrySetResult(ok);
            };

            Application.Run(_hiddenForm);
        }
        catch (Exception ex)
        {
            _readyTcs.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hiddenForm is { IsDisposed: false })
        {
            try
            {
                _hiddenForm.BeginInvoke(() => _hiddenForm.Close());
            }
            catch
            {
                // Ignore during shutdown
            }
        }
    }
}
