import ctypes
from ctypes import wintypes
import os
import sys
import time
import subprocess

MAX_STRING_SIZE = 1024
SHORT_STRING_SIZE = 256
MAX_ACTION_INFO = 256
MAX_ACTIONS_TO_DO = 32

JAB_DLL_PATH = os.path.join(
    os.environ["USERPROFILE"],
    r"AppRENIEC\common\jre\jre-8\bin\WindowsAccessBridge-64.dll",
)
APPREF_PATH = os.path.join(
    os.environ["APPDATA"],
    r"Microsoft\Windows\Start Menu\Programs\RENIEC\ReFirma Suite\ReFirma PDF 1.6.appref-ms",
)

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

# Preload JAB DLL (using CDLL because WindowsAccessBridge-64.dll uses cdecl)
jab = ctypes.CDLL(JAB_DLL_PATH)

JOBJECT64 = ctypes.c_int64
VMID = ctypes.c_int32


class AccessibleContextInfo(ctypes.Structure):
    _fields_ = [
        ("name", ctypes.c_wchar * MAX_STRING_SIZE),
        ("description", ctypes.c_wchar * MAX_STRING_SIZE),
        ("role", ctypes.c_wchar * SHORT_STRING_SIZE),
        ("role_en_US", ctypes.c_wchar * SHORT_STRING_SIZE),
        ("states", ctypes.c_wchar * SHORT_STRING_SIZE),
        ("states_en_US", ctypes.c_wchar * SHORT_STRING_SIZE),
        ("indexInParent", ctypes.c_int32),
        ("childrenCount", ctypes.c_int32),
        ("x", ctypes.c_int32),
        ("y", ctypes.c_int32),
        ("width", ctypes.c_int32),
        ("height", ctypes.c_int32),
        ("accessibleComponent", ctypes.c_int32),
        ("accessibleAction", ctypes.c_int32),
        ("accessibleSelection", ctypes.c_int32),
        ("accessibleText", ctypes.c_int32),
        ("accessibleInterfaces", ctypes.c_int32),
    ]


class AccessibleActionInfo(ctypes.Structure):
    _fields_ = [("name", ctypes.c_wchar * SHORT_STRING_SIZE)]


class AccessibleActions(ctypes.Structure):
    _fields_ = [
        ("actionsCount", ctypes.c_int32),
        ("actionInfo", AccessibleActionInfo * MAX_ACTION_INFO),
    ]


class AccessibleActionsToDo(ctypes.Structure):
    _fields_ = [
        ("actionsCount", ctypes.c_int32),
        ("actions", AccessibleActionInfo * MAX_ACTIONS_TO_DO),
    ]


class AccessibleTextInfo(ctypes.Structure):
    _fields_ = [
        ("charCount", ctypes.c_int32),
        ("caretIndex", ctypes.c_int32),
        ("indexAtPoint", ctypes.c_int32),
    ]


# Configure JAB prototypes
jab.Windows_run.restype = wintypes.BOOL
jab.isJavaWindow.argtypes = [wintypes.HWND]
jab.isJavaWindow.restype = wintypes.BOOL
jab.releaseJavaObject.argtypes = [VMID, JOBJECT64]
jab.getAccessibleContextFromHWND.argtypes = [wintypes.HWND, ctypes.POINTER(VMID), ctypes.POINTER(JOBJECT64)]
jab.getAccessibleContextFromHWND.restype = wintypes.BOOL
jab.getAccessibleContextInfo.argtypes = [VMID, JOBJECT64, ctypes.POINTER(AccessibleContextInfo)]
jab.getAccessibleContextInfo.restype = wintypes.BOOL
jab.getAccessibleChildFromContext.argtypes = [VMID, JOBJECT64, ctypes.c_int32]
jab.getAccessibleChildFromContext.restype = JOBJECT64
jab.getAccessibleActions.argtypes = [VMID, JOBJECT64, ctypes.POINTER(AccessibleActions)]
jab.getAccessibleActions.restype = wintypes.BOOL
jab.doAccessibleActions.argtypes = [VMID, JOBJECT64, ctypes.POINTER(AccessibleActionsToDo), ctypes.POINTER(ctypes.c_int32)]
jab.doAccessibleActions.restype = wintypes.BOOL
jab.setTextContents.argtypes = [VMID, JOBJECT64, ctypes.c_wchar_p]
jab.setTextContents.restype = wintypes.BOOL
jab.getAccessibleTextInfo.argtypes = [VMID, JOBJECT64, ctypes.POINTER(AccessibleTextInfo), ctypes.c_int32, ctypes.c_int32]
jab.getAccessibleTextInfo.restype = wintypes.BOOL
jab.getAccessibleTextRange.argtypes = [VMID, JOBJECT64, ctypes.c_int32, ctypes.c_int32, ctypes.c_wchar_p, ctypes.c_int16]
jab.getAccessibleTextRange.restype = wintypes.BOOL
jab.requestFocus.argtypes = [VMID, JOBJECT64]
jab.requestFocus.restype = wintypes.BOOL
jab.addAccessibleSelectionFromContext.argtypes = [VMID, JOBJECT64, ctypes.c_int32]
jab.clearAccessibleSelectionFromContext.argtypes = [VMID, JOBJECT64]


def pump_messages(duration_ms=200):
    msg = wintypes.MSG()
    end = time.time() + (duration_ms / 1000.0)
    while time.time() < end:
        while user32.PeekMessageW(ctypes.byref(msg), 0, 0, 0, 1):
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
        time.sleep(0.02)


def list_visible_windows():
    wins = []
    EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(hwnd, lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(length + 1)
        if length > 0:
            user32.GetWindowTextW(hwnd, buf, length + 1)
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, 256)
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        wins.append((hwnd, buf.value, cls.value, pid.value))
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return wins


def find_java_window(predicate):
    for hwnd, title, cls, pid in list_visible_windows():
        if not predicate(title):
            continue
        if jab.isJavaWindow(hwnd):
            vmid = VMID()
            ac = JOBJECT64()
            if jab.getAccessibleContextFromHWND(hwnd, ctypes.byref(vmid), ctypes.byref(ac)) and ac.value:
                return hwnd, title, pid, vmid.value, ac.value
    return None


def wait_java_window(predicate, timeout_sec=45):
    start = time.time()
    while time.time() - start < timeout_sec:
        pump_messages(250)
        win = find_java_window(predicate)
        if win:
            return win
    return None


def get_text(vmid, ac, info):
    if not info.accessibleText:
        return ""
    ti = AccessibleTextInfo()
    if not jab.getAccessibleTextInfo(vmid, ac, ctypes.byref(ti), 0, 0) or ti.charCount <= 0:
        return ""
    end = min(ti.charCount - 1, 200)
    buf = ctypes.create_unicode_buffer(end + 4)
    if jab.getAccessibleTextRange(vmid, ac, 0, end, buf, end + 2):
        return buf.value.replace("\r", "\\r").replace("\n", "\\n")
    return ""


def get_actions(vmid, ac, info):
    if not info.accessibleAction:
        return []
    acts = AccessibleActions()
    if jab.getAccessibleActions(vmid, ac, ctypes.byref(acts)) and acts.actionsCount > 0:
        return [acts.actionInfo[i].name for i in range(min(acts.actionsCount, MAX_ACTION_INFO))]
    return []


def dump_tree(vmid, root_ac, out_file, title=""):
    lines = [f"=== WINDOW vmID={vmid} title='{title}' ==="]

    def walk(ac, depth):
        if not ac or depth > 20:
            return
        info = AccessibleContextInfo()
        if not jab.getAccessibleContextInfo(vmid, ac, ctypes.byref(info)):
            return
        acts = ",".join(get_actions(vmid, ac, info))
        txt = get_text(vmid, ac, info)
        indent = "  " * depth
        lines.append(
            f'{indent}[{info.role_en_US}] name="{info.name}" desc="{info.description}" '
            f'states="{info.states_en_US}" rect=({info.x},{info.y},{info.width},{info.height}) '
            f'idx={info.indexInParent} children={info.childrenCount} actions=[{acts}] text="{txt}" '
            f'ifaces=(comp={info.accessibleComponent},act={info.accessibleAction},sel={info.accessibleSelection},txt={info.accessibleText},int={info.accessibleInterfaces})'
        )
        for i in range(info.childrenCount):
            child = jab.getAccessibleChildFromContext(vmid, ac, i)
            if child:
                walk(child, depth + 1)
                jab.releaseJavaObject(vmid, child)

    walk(root_ac, 0)
    with open(out_file, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    print(f"[Dump] {out_file} ({len(lines)} nodes)")


def find_node(vmid, root_ac, predicate):
    found = None

    def walk(ac, depth):
        nonlocal found
        if found is not None or not ac or depth > 20:
            return
        info = AccessibleContextInfo()
        if not jab.getAccessibleContextInfo(vmid, ac, ctypes.byref(info)):
            return
        if predicate(info, ac):
            found = (ac, info)
            return
        for i in range(info.childrenCount):
            child = jab.getAccessibleChildFromContext(vmid, ac, i)
            if child:
                walk(child, depth + 1)
                if found is None or found[0] != child:
                    jab.releaseJavaObject(vmid, child)

    walk(root_ac, 0)
    return found


def do_action(vmid, ac, info, action_name=None):
    acts = get_actions(vmid, ac, info)
    if acts:
        chosen = action_name if action_name in acts else acts[0]
        todo = AccessibleActionsToDo()
        todo.actionsCount = 1
        todo.actions[0].name = chosen
        failure = ctypes.c_int32(-1)
        ok = jab.doAccessibleActions(vmid, ac, ctypes.byref(todo), ctypes.byref(failure))
        print(f"  doAccessibleActions('{chosen}') on '{info.name}' [{info.role_en_US}] => ok={ok}, failure={failure.value}")
        return bool(ok)
    return False


def main():
    dump_dir = r"C:\Users\Usuario\Desktop\MPFN\PROYECTOS\auto-firma-digital\docs\dumps"
    os.makedirs(dump_dir, exist_ok=True)
    log_f = open(os.path.join(dump_dir, "smoke_stdout.log"), "w", encoding="utf-8")

    class Tee:
        def write(self, s):
            sys.__stdout__.write(s)
            log_f.write(s)
            log_f.flush()
        def flush(self):
            sys.__stdout__.flush()
            log_f.flush()

    sys.stdout = Tee()
    sys.stderr = Tee()

    ok = jab.Windows_run()
    print(f"[Init] Windows_run() => {ok}")
    pump_messages(500)
    if not ok:
        sys.exit(1)

    existing = find_java_window(lambda t: "ReFirma PDF" in t)
    if not existing:
        print(f"[Launch] Starting {APPREF_PATH}")
        os.startfile(APPREF_PATH)
        pump_messages(2000)

    main_win = wait_java_window(lambda t: "ReFirma PDF" in t and "Iniciando" not in t, timeout_sec=60)
    if not main_win:
        print("[ERROR] Main ReFirma PDF window not found.")
        for h, t, c, p in list_visible_windows():
            if t or jab.isJavaWindow(h):
                print(f"  hwnd={h} java={jab.isJavaWindow(h)} cls='{c}' title='{t}'")
        sys.exit(2)

    hwnd, title, pid, vmid, root_ac = main_win
    print(f"[OK] Main window: '{title}' hwnd={hwnd} pid={pid} vmid={vmid}")
    user32.SetForegroundWindow(hwnd)
    pump_messages(500)
    dump_tree(vmid, root_ac, os.path.join(dump_dir, "01-main-window.txt"), title)

    # Check if batch dialog is already open
    batch_win = find_java_window(lambda t: "Parámetros de firma digital en lote" in t)
    if not batch_win:
        menu = find_node(vmid, root_ac, lambda i, _: i.role_en_US == "menu" and i.name == "Archivo")
        if not menu:
            print("[ERROR] Menu 'Archivo' not found")
            sys.exit(3)
        do_action(vmid, menu[0], menu[1])
        pump_messages(400)

        item = find_node(vmid, root_ac, lambda i, _: i.role_en_US == "menu item" and "Firmar en lote" in i.name)
        if not item:
            print("[ERROR] Menu item 'Firmar en lote' not found")
            sys.exit(4)
        do_action(vmid, item[0], item[1])
        pump_messages(600)
        batch_win = wait_java_window(lambda t: "Parámetros de firma digital en lote" in t, timeout_sec=15)

    if not batch_win:
        print("[ERROR] Batch dialog did not appear.")
        sys.exit(5)

    bhwnd, btitle, bpid, bvmid, broot = batch_win
    print(f"[OK] Batch dialog: '{btitle}' hwnd={bhwnd} vmid={bvmid}")
    pump_messages(800)
    dump_tree(bvmid, broot, os.path.join(dump_dir, "02-batch-dialog.txt"), btitle)
    print("[SUCCESS] Phase 0 Probe completed!")


if __name__ == "__main__":
    main()
