using System.Diagnostics;
using System.Text;
using SignerBridge.Core.Models;

namespace SignerBridge.Providers.ReFirmaPdf.Jab;

internal readonly record struct JavaWindow(IntPtr Hwnd, string Title, uint Pid, int VmId, IntPtr RootAc);

internal readonly record struct JabNode(int VmId, IntPtr Ac, JabNative.AccessibleContextInfo Info, int Depth);

internal sealed class JabSession
{
    private readonly JabStaDispatcher _sta;

    public JabSession(JabStaDispatcher sta)
    {
        _sta = sta;
    }

    public Task<JavaWindow?> FindJavaWindowAsync(Func<string, bool> titlePredicate, CancellationToken ct)
    {
        return _sta.InvokeAsync(() => FindJavaWindowCore(titlePredicate), ct);
    }

    public async Task<JavaWindow?> WaitForJavaWindowAsync(
        Func<string, bool> titlePredicate,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var win = await FindJavaWindowAsync(titlePredicate, ct).ConfigureAwait(false);
            if (win is not null)
                return win;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<bool> WaitForWindowToDisappearAsync(
        Func<string, bool> titlePredicate,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var win = await _sta.InvokeAsync(() =>
                Win32.ListVisibleWindows().Any(w => w.ClassName.Contains("SunAwt") && titlePredicate(w.Title)), ct)
                .ConfigureAwait(false);
            if (!win)
                return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    public Task<JabNode?> FindNodeAsync(JavaWindow win, Func<JabNode, bool> predicate, CancellationToken ct)
    {
        return _sta.InvokeAsync(() => FindNodeCore(win.VmId, win.RootAc, predicate), ct);
    }

    public Task<List<JabNode>> FindAllNodesAsync(JavaWindow win, Func<JabNode, bool> predicate, CancellationToken ct)
    {
        return _sta.InvokeAsync(() => FindAllNodesCore(win.VmId, win.RootAc, predicate), ct);
    }

    public Task BringToForegroundAsync(IntPtr hwnd, CancellationToken ct)
    {
        return _sta.InvokeAsync(() =>
        {
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            Win32.SetForegroundWindow(hwnd);
        }, ct);
    }

    public Task<bool> DoAccessibleActionAsync(JabNode node, string? preferredAction = null, CancellationToken ct = default)
    {
        return _sta.InvokeAsync(() =>
        {
            if (!node.Info.accessibleAction ||
                !JabNative.getAccessibleActions(node.VmId, node.Ac, out var acts) ||
                acts.actionsCount <= 0)
            {
                return false;
            }

            string actionName = acts.actionInfo[0].name;
            if (!string.IsNullOrEmpty(preferredAction))
            {
                for (int i = 0; i < acts.actionsCount; i++)
                {
                    if (acts.actionInfo[i].name.Equals(preferredAction, StringComparison.OrdinalIgnoreCase))
                    {
                        actionName = acts.actionInfo[i].name;
                        break;
                    }
                }
            }

            var todo = new JabNative.AccessibleActionsToDo
            {
                actionsCount = 1,
                actions = new JabNative.AccessibleActionInfo[JabNative.MAX_ACTIONS_TO_DO]
            };
            todo.actions[0] = new JabNative.AccessibleActionInfo { name = actionName };
            return JabNative.doAccessibleActions(node.VmId, node.Ac, ref todo, out _);
        }, ct);
    }

    public Task ClickNodeCenterAsync(JabNode node, CancellationToken ct = default)
    {
        return _sta.InvokeAsync(() =>
        {
            int cx = node.Info.x + node.Info.width / 2;
            int cy = node.Info.y + node.Info.height / 2;
            Win32.ClickAt(cx, cy);
        }, ct);
    }

    public Task<bool> SetTextContentsAsync(JabNode node, string text, CancellationToken ct = default)
    {
        return _sta.InvokeAsync(() => JabNative.setTextContents(node.VmId, node.Ac, text), ct);
    }

    public Task SendKeysToFocusedAsync(string keys, CancellationToken ct = default)
    {
        return _sta.InvokeAsync(() => System.Windows.Forms.SendKeys.SendWait(keys), ct);
    }

    public Task SendUnicodeTextAsync(string text, int delayMs = 35, CancellationToken ct = default)
    {
        return _sta.InvokeAsync(() => Win32.SendUnicodeString(text, delayMs), ct);
    }

    public Task<List<uint>> GetReFirmaPidsAsync(CancellationToken ct = default)
    {
        return _sta.InvokeAsync(() =>
            Win32.ListVisibleWindows()
                .Where(w => w.ClassName.Contains("SunAwt", StringComparison.Ordinal) &&
                            w.Title.Contains("ReFirma PDF", StringComparison.OrdinalIgnoreCase))
                .Select(w => w.Pid)
                .Distinct()
                .ToList(), ct);
    }

    public Task<string> GetNodeTextAsync(JabNode node, CancellationToken ct = default)
    {
        return _sta.InvokeAsync(() =>
        {
            if (!node.Info.accessibleText) return string.Empty;
            if (!JabNative.getAccessibleTextInfo(node.VmId, node.Ac, out var ti, 0, 0) || ti.charCount <= 0)
                return string.Empty;
            int end = Math.Min(ti.charCount - 1, 512);
            var buf = new StringBuilder(end + 4);
            return JabNative.getAccessibleTextRange(node.VmId, node.Ac, 0, end, buf, (short)(end + 2))
                ? buf.ToString()
                : string.Empty;
        }, ct);
    }

    public Task ReleaseWindowAsync(JavaWindow win, CancellationToken ct = default)
    {
        return _sta.InvokeAsync(() => JabNative.releaseJavaObject(win.VmId, win.RootAc), ct);
    }

    public static bool MatchesCertificate(string certDisplayText, CertificateSelector selector)
    {
        if (string.IsNullOrWhiteSpace(certDisplayText))
            return false;

        if (string.IsNullOrWhiteSpace(selector.Query))
            return true; // Default first certificate if no query specified

        string query = selector.Query.Trim();
        string mode = selector.MatchBy.Trim().ToUpperInvariant();

        return mode switch
        {
            "FIR" => certDisplayText.Contains(
                query.StartsWith("FIR", StringComparison.OrdinalIgnoreCase) ? query : $"FIR {query}",
                StringComparison.OrdinalIgnoreCase),

            "SN" => NormalizeSerial(certDisplayText).Contains(NormalizeSerial(query), StringComparison.OrdinalIgnoreCase),

            "FULL_NAME" => certDisplayText.Contains(query, StringComparison.OrdinalIgnoreCase),

            _ => certDisplayText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 NormalizeSerial(certDisplayText).Contains(NormalizeSerial(query), StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string NormalizeSerial(string s)
        => s.Replace(":", string.Empty).Replace(" ", string.Empty);

    private static JavaWindow? FindJavaWindowCore(Func<string, bool> titlePredicate)
    {
        foreach (var w in Win32.ListVisibleWindows())
        {
            if (!w.ClassName.Contains("SunAwt", StringComparison.Ordinal))
                continue;
            if (!titlePredicate(w.Title))
                continue;
            if (JabNative.getAccessibleContextFromHWND(w.Hwnd, out int vmId, out IntPtr ac) && ac != IntPtr.Zero)
                return new JavaWindow(w.Hwnd, w.Title, w.Pid, vmId, ac);
        }
        return null;
    }

    private static JabNode? FindNodeCore(int vmId, IntPtr rootAc, Func<JabNode, bool> predicate)
    {
        JabNode? found = null;
        void Walk(IntPtr ac, int depth)
        {
            if (found is not null || ac == IntPtr.Zero || depth > 20) return;
            if (!JabNative.getAccessibleContextInfo(vmId, ac, out var info)) return;

            var node = new JabNode(vmId, ac, info, depth);
            if (predicate(node))
            {
                found = node;
                return;
            }

            for (int i = 0; i < info.childrenCount; i++)
            {
                IntPtr child = JabNative.getAccessibleChildFromContext(vmId, ac, i);
                if (child != IntPtr.Zero)
                {
                    Walk(child, depth + 1);
                    if (found is null || found.Value.Ac != child)
                        JabNative.releaseJavaObject(vmId, child);
                }
            }
        }
        Walk(rootAc, 0);
        return found;
    }

    private static List<JabNode> FindAllNodesCore(int vmId, IntPtr rootAc, Func<JabNode, bool> predicate)
    {
        var list = new List<JabNode>();
        void Walk(IntPtr ac, int depth)
        {
            if (ac == IntPtr.Zero || depth > 20) return;
            if (!JabNative.getAccessibleContextInfo(vmId, ac, out var info)) return;

            var node = new JabNode(vmId, ac, info, depth);
            if (predicate(node))
                list.Add(node);

            for (int i = 0; i < info.childrenCount; i++)
            {
                IntPtr child = JabNative.getAccessibleChildFromContext(vmId, ac, i);
                if (child != IntPtr.Zero)
                {
                    Walk(child, depth + 1);
                    if (list.Count == 0 || list[^1].Ac != child)
                        JabNative.releaseJavaObject(vmId, child);
                }
            }
        }
        Walk(rootAc, 0);
        return list;
    }
}
