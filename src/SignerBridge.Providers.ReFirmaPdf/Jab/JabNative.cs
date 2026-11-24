using System.Runtime.InteropServices;
using System.Text;

namespace SignerBridge.Providers.ReFirmaPdf.Jab;

internal static class JabNative
{
    public const string DllName = "WindowsAccessBridge-64.dll";
    public const int MAX_STRING_SIZE = 1024;
    public const int SHORT_STRING_SIZE = 256;
    public const int MAX_ACTION_INFO = 256;
    public const int MAX_ACTIONS_TO_DO = 32;

    private static string? _preferredJreBinDir;

    public static void ConfigureJreBinDirectory(string jreBinDir)
    {
        _preferredJreBinDir = jreBinDir;
    }

    static JabNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(JabNative).Assembly, (libraryName, _, _) =>
        {
            if (libraryName != DllName)
                return IntPtr.Zero;

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_preferredJreBinDir))
                candidates.Add(Path.Combine(_preferredJreBinDir, DllName));

            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"AppRENIEC\common\jre\jre-8\bin",
                DllName));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), DllName));
            candidates.Add(DllName);

            foreach (var path in candidates)
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                    return handle;
            }

            return IntPtr.Zero;
        });
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AccessibleContextInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_STRING_SIZE)]
        public string name;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_STRING_SIZE)]
        public string description;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SHORT_STRING_SIZE)]
        public string role;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SHORT_STRING_SIZE)]
        public string role_en_US;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SHORT_STRING_SIZE)]
        public string states;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SHORT_STRING_SIZE)]
        public string states_en_US;

        public int indexInParent;
        public int childrenCount;
        public int x;
        public int y;
        public int width;
        public int height;

        [MarshalAs(UnmanagedType.Bool)]
        public bool accessibleComponent;

        [MarshalAs(UnmanagedType.Bool)]
        public bool accessibleAction;

        [MarshalAs(UnmanagedType.Bool)]
        public bool accessibleSelection;

        [MarshalAs(UnmanagedType.Bool)]
        public bool accessibleText;

        [MarshalAs(UnmanagedType.Bool)]
        public bool accessibleInterfaces;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AccessibleActionInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SHORT_STRING_SIZE)]
        public string name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AccessibleActions
    {
        public int actionsCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_ACTION_INFO)]
        public AccessibleActionInfo[] actionInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AccessibleActionsToDo
    {
        public int actionsCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_ACTIONS_TO_DO)]
        public AccessibleActionInfo[] actions;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AccessibleTextInfo
    {
        public int charCount;
        public int caretIndex;
        public int indexAtPoint;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Windows_run();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void releaseJavaObject(int vmID, IntPtr javaObject);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool isJavaWindow(IntPtr window);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool getAccessibleContextFromHWND(IntPtr target, out int vmID, out IntPtr ac);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool getAccessibleContextInfo(int vmID, IntPtr ac, out AccessibleContextInfo info);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr getAccessibleChildFromContext(int vmID, IntPtr ac, int index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool getAccessibleActions(int vmID, IntPtr ac, out AccessibleActions actions);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool doAccessibleActions(int vmID, IntPtr ac, ref AccessibleActionsToDo actionsToDo, out int failure);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool setTextContents(int vmID, IntPtr ac, string text);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool getAccessibleTextInfo(int vmID, IntPtr ac, out AccessibleTextInfo textInfo, int x, int y);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool getAccessibleTextRange(int vmID, IntPtr ac, int startIndex, int endIndex, StringBuilder text, short len);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool requestFocus(int vmID, IntPtr ac);
}

internal static class Win32
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const int SW_RESTORE = 9;

    public static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(45);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    public static List<(IntPtr Hwnd, string Title, string ClassName, uint Pid)> ListVisibleWindows()
    {
        var result = new List<(IntPtr, string, string, uint)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            int len = GetWindowTextLength(hwnd);
            var sb = new StringBuilder(len + 1);
            if (len > 0) GetWindowText(hwnd, sb, sb.Capacity);
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            GetWindowThreadProcessId(hwnd, out uint pid);
            result.Add((hwnd, sb.ToString(), cls.ToString(), pid));
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
