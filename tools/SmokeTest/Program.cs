using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace SmokeTest;

internal static class Program
{
    private static readonly string AppRefPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Windows\Start Menu\Programs\RENIEC\ReFirma Suite\ReFirma PDF 1.6.appref-ms");

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "probe-chooser";
        var dumpDir = args.Length > 1 ? args[1] : @"C:\Users\Usuario\Desktop\MPFN\PROYECTOS\auto-firma-digital\docs\dumps";
        Directory.CreateDirectory(dumpDir);

        var logPath = Path.Combine(dumpDir, $"cs_{mode}.log");
        using var logWriter = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        var tee = new TeeWriter(Console.Out, logWriter);
        Console.SetOut(tee);
        Console.SetError(tee);

        Console.WriteLine($"[Init] Mode={mode}, DumpDir={dumpDir}");

        using var hiddenForm = new Form
        {
            Width = 1,
            Height = 1,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000)
        };

        int exitCode = 0;
        hiddenForm.Shown += async (_, _) =>
        {
            try
            {
                bool jabOk = JabNative.Windows_run();
                Console.WriteLine($"[Init] Windows_run() => {jabOk}");
                if (!jabOk) { exitCode = 1; Application.ExitThread(); return; }

                await WaitPumpAsync(1000);

                if (mode == "probe-chooser")
                    exitCode = await RunProbeChooserAndPosAsync(dumpDir);
                else if (mode == "e2e-pin")
                    exitCode = await RunE2eUntilPinAsync(dumpDir);
                else
                    Console.WriteLine($"Unknown mode: {mode}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EX] {ex}");
                exitCode = 9;
            }
            finally
            {
                Application.ExitThread();
            }
        };

        Application.Run(hiddenForm);
        return exitCode;
    }

    private static async Task<int> RunProbeChooserAndPosAsync(string dumpDir)
    {
        var batchWin = FindJavaWindow(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase));
        if (batchWin == null)
        {
            var mainWin = FindJavaWindow(t => t.Contains("ReFirma PDF", StringComparison.OrdinalIgnoreCase) && !t.Contains("Iniciando", StringComparison.OrdinalIgnoreCase));
            if (mainWin == null)
            {
                Console.WriteLine($"[Launch] Launching: {AppRefPath}");
                Process.Start(new ProcessStartInfo { FileName = AppRefPath, UseShellExecute = true });
                mainWin = await WaitJavaWindowAsync(t => t.Contains("ReFirma PDF", StringComparison.OrdinalIgnoreCase) && !t.Contains("Iniciando", StringComparison.OrdinalIgnoreCase), 45);
            }
            if (mainWin == null)
            {
                Console.Error.WriteLine("[ERROR] Main ReFirma window not found.");
                return 2;
            }
            Win32.ShowWindow(mainWin.Value.Hwnd, Win32.SW_RESTORE);
            Win32.SetForegroundWindow(mainWin.Value.Hwnd);
            await WaitPumpAsync(300);

            var menu = FindNode(mainWin.Value.VmId, mainWin.Value.RootAc, n =>
                n.Info.role_en_US == "menu" && n.Info.name.Equals("Archivo", StringComparison.OrdinalIgnoreCase));
            if (menu == null) return 2;
            DoAccessibleClick(menu.Value);
            await WaitPumpAsync(400);

            var item = FindNode(mainWin.Value.VmId, mainWin.Value.RootAc, n =>
                n.Info.role_en_US == "menu item" && n.Info.name.Contains("Firmar en lote", StringComparison.OrdinalIgnoreCase));
            if (item == null || item.Value.Info.width <= 0) return 2;
            ClickNodeRect(item.Value, "Firmar en lote...");
            batchWin = await WaitJavaWindowAsync(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase), 25);
        }

        if (batchWin == null)
        {
            Console.Error.WriteLine("[ERROR] Batch dialog did not open.");
            return 2;
        }

        Console.WriteLine($"[OK] Batch dialog HWND={batchWin.Value.Hwnd}");
        Win32.SetForegroundWindow(batchWin.Value.Hwnd);
        await WaitPumpAsync(300);

        // 1. Click '...' for Entrada
        var entradaBtn = FindNode(batchWin.Value.VmId, batchWin.Value.RootAc, n =>
            n.Info.role_en_US == "push button" && n.Info.description.Contains("documentos para firmar", StringComparison.OrdinalIgnoreCase));
        if (entradaBtn == null)
        {
            Console.Error.WriteLine("[ERROR] Entrada '...' button not found.");
            return 3;
        }

        ClickNodeRect(entradaBtn.Value, "Entrada '...'");
        await WaitPumpAsync(1000);

        // Check all visible windows (both Java and Win32)
        DumpAllVisibleAndJavaTrees(dumpDir, "03-folder-chooser");

        // Close chooser via Escape so we can also inspect Posición popup
        SendKeys.SendWait("{ESC}");
        await WaitPumpAsync(600);

        // 2. Click Posición button
        batchWin = FindJavaWindow(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase));
        if (batchWin != null)
        {
            var posBtn = FindNode(batchWin.Value.VmId, batchWin.Value.RootAc, n =>
                n.Info.role_en_US == "push button" && n.Info.description.Contains("posición", StringComparison.OrdinalIgnoreCase));
            if (posBtn != null)
            {
                ClickNodeRect(posBtn.Value, "Posición");
                await WaitPumpAsync(700);
                DumpWindowTree(batchWin.Value, Path.Combine(dumpDir, "04-batch-with-pos-popup.txt"));
                DumpAllVisibleAndJavaTrees(dumpDir, "04-pos-windows");
                SendKeys.SendWait("{ESC}");
                await WaitPumpAsync(400);
            }
        }
        return 0;
    }

    private static async Task<int> RunE2eUntilPinAsync(string dumpDir)
    {
        string testIn = @"C:\Users\Usuario\Desktop\MPFN\PROYECTOS\auto-firma-digital\tools\SmokeTest\test-in";
        string testOut = @"C:\Users\Usuario\Desktop\MPFN\PROYECTOS\auto-firma-digital\tools\SmokeTest\test-out";
        Directory.CreateDirectory(testIn);
        Directory.CreateDirectory(testOut);
        EnsureDummyPdf(Path.Combine(testIn, "documento-prueba.pdf"));

        var batchWin = FindJavaWindow(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase));
        if (batchWin == null)
        {
            var mainWin = FindJavaWindow(t => t.Contains("ReFirma PDF", StringComparison.OrdinalIgnoreCase) && !t.Contains("Iniciando", StringComparison.OrdinalIgnoreCase));
            if (mainWin == null)
            {
                Console.WriteLine($"[Launch] Launching: {AppRefPath}");
                Process.Start(new ProcessStartInfo { FileName = AppRefPath, UseShellExecute = true });
                mainWin = await WaitJavaWindowAsync(t => t.Contains("ReFirma PDF", StringComparison.OrdinalIgnoreCase) && !t.Contains("Iniciando", StringComparison.OrdinalIgnoreCase), 45);
            }
            if (mainWin == null)
            {
                Console.Error.WriteLine("[ERROR] Main ReFirma window not found.");
                return 2;
            }
            Win32.ShowWindow(mainWin.Value.Hwnd, Win32.SW_RESTORE);
            Win32.SetForegroundWindow(mainWin.Value.Hwnd);
            await WaitPumpAsync(300);

            var menu = FindNode(mainWin.Value.VmId, mainWin.Value.RootAc, n =>
                n.Info.role_en_US == "menu" && n.Info.name.Equals("Archivo", StringComparison.OrdinalIgnoreCase));
            if (menu == null) return 2;
            DoAccessibleClick(menu.Value);
            await WaitPumpAsync(400);

            var item = FindNode(mainWin.Value.VmId, mainWin.Value.RootAc, n =>
                n.Info.role_en_US == "menu item" && n.Info.name.Contains("Firmar en lote", StringComparison.OrdinalIgnoreCase));
            if (item == null || item.Value.Info.width <= 0) return 2;
            ClickNodeRect(item.Value, "Firmar en lote...");
            batchWin = await WaitJavaWindowAsync(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase), 25);
        }

        if (batchWin == null)
        {
            Console.Error.WriteLine("[ERROR] Batch dialog did not open.");
            return 2;
        }

        Win32.SetForegroundWindow(batchWin.Value.Hwnd);
        await WaitPumpAsync(300);

        // 1. Entrada folder via '...'
        if (!await SetFolderViaChooserAsync(batchWin.Value, "documentos para firmar", "Seleccionar directorio de entrada", testIn))
            return 3;

        // 2. Salida folder via '...'
        batchWin = FindJavaWindow(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase));
        if (batchWin == null) return 3;
        if (!await SetFolderViaChooserAsync(batchWin.Value, "carpeta destino", "Seleccionar directorio de salida", testOut))
            return 4;

        // 3. Select certificate
        batchWin = FindJavaWindow(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase));
        if (batchWin == null) return 4;
        var certItem = FindNode(batchWin.Value.VmId, batchWin.Value.RootAc, n =>
            n.Info.role_en_US == "label" && n.Info.name.Contains("VALDIVIA", StringComparison.OrdinalIgnoreCase));
        if (certItem == null)
        {
            Console.Error.WriteLine("[ERROR] Certificate item matching 'VALDIVIA' not found.");
            return 5;
        }
        ClickNodeRect(certItem.Value, $"Cert: {certItem.Value.Info.name}");
        await WaitPumpAsync(800);

        // Dump post-cert state to inspect enabled state of Motivo, stamps, Aceptar
        batchWin = FindJavaWindow(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase));
        if (batchWin != null)
            DumpWindowTree(batchWin.Value, Path.Combine(dumpDir, "05-batch-after-cert.txt"));

        // 4. Click Posición -> X / Y -> set 120, 450
        if (batchWin != null)
        {
            var posBtn = FindNode(batchWin.Value.VmId, batchWin.Value.RootAc, n =>
                n.Info.role_en_US == "push button" && n.Info.description.Contains("posición", StringComparison.OrdinalIgnoreCase));
            if (posBtn == null) return 6;
            ClickNodeRect(posBtn.Value, "Posición");
            await WaitPumpAsync(500);

            var xyItem = FindNode(batchWin.Value.VmId, batchWin.Value.RootAc, n =>
                n.Info.role_en_US == "menu item" && n.Info.name.Contains("X / Y", StringComparison.OrdinalIgnoreCase));
            if (xyItem == null || xyItem.Value.Info.width <= 0)
            {
                Console.Error.WriteLine("[ERROR] 'X / Y' menu item not found/visible.");
                return 6;
            }
            ClickNodeRect(xyItem.Value, "X / Y");

            if (!await HandleInputPromptAsync(dumpDir, "06-coord-x.txt", "coordenada X", "120")) return 7;
            if (!await HandleInputPromptAsync(dumpDir, "07-coord-y.txt", "coordenada Y", "450")) return 8;
        }

        // 5. Set Motivo via combo box or verify enabled
        batchWin = FindJavaWindow(t => t.Contains("Parámetros de firma digital en lote", StringComparison.OrdinalIgnoreCase));
        if (batchWin == null) return 8;
        var motivoItem = FindNode(batchWin.Value.VmId, batchWin.Value.RootAc, n =>
            n.Info.role_en_US == "label" && n.Info.name.Equals("En señal de conformidad", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"[Motivo] target item found={motivoItem != null}");

        // 6. Click Aceptar
        var aceptarBtn = FindNode(batchWin.Value.VmId, batchWin.Value.RootAc, n =>
            n.Info.role_en_US == "push button" && n.Info.name.Equals("Aceptar", StringComparison.OrdinalIgnoreCase));
        if (aceptarBtn == null)
        {
            Console.Error.WriteLine("[ERROR] Aceptar button not found.");
            return 9;
        }
        Console.WriteLine($"[Aceptar] states='{aceptarBtn.Value.Info.states_en_US}'");
        ClickNodeRect(aceptarBtn.Value, "Aceptar");

        // 7. Wait for Declaración modal
        var declWin = await WaitJavaWindowAsync(t => t.Contains("Declaración", StringComparison.OrdinalIgnoreCase), 15);
        if (declWin == null)
        {
            Console.Error.WriteLine("[ERROR] Declaración modal did not appear.");
            DumpAllVisibleAndJavaTrees(dumpDir, "err-no-decl");
            return 10;
        }
        await WaitPumpAsync(500);
        DumpWindowTree(declWin.Value, Path.Combine(dumpDir, "08-declaracion.txt"));

        var acepInput = FindNode(declWin.Value.VmId, declWin.Value.RootAc, n =>
            n.Info.role_en_US == "text" && n.Info.states_en_US.Contains("editable"));
        if (acepInput == null)
        {
            Console.Error.WriteLine("[ERROR] ACEPTO input not found.");
            return 11;
        }
        ClickNodeRect(acepInput.Value, "ACEPTO input");
        await WaitPumpAsync(150);
        SendKeys.SendWait("ACEPTO");
        await WaitPumpAsync(400);

        var firmarBtn = FindNode(declWin.Value.VmId, declWin.Value.RootAc, n =>
            n.Info.role_en_US == "push button" && n.Info.name.Equals("Firmar", StringComparison.OrdinalIgnoreCase));
        if (firmarBtn == null) return 11;
        Console.WriteLine($"[Firmar] states='{firmarBtn.Value.Info.states_en_US}'");
        if (!firmarBtn.Value.Info.states_en_US.Contains("enabled"))
        {
            Console.Error.WriteLine("[ERROR] Firmar button did not become enabled after typing ACEPTO.");
            return 12;
        }
        ClickNodeRect(firmarBtn.Value, "Firmar");

        // 8. Wait for PIN modal (ReFirma PDF - DNIe) - note TSL validation can take 2-10s
        var pinWin = await WaitJavaWindowAsync(t => t.Contains("DNIe", StringComparison.OrdinalIgnoreCase) || t.Contains("PIN", StringComparison.OrdinalIgnoreCase), 30);
        if (pinWin == null)
        {
            Console.Error.WriteLine("[ERROR] PIN dialog did not appear within 30s.");
            DumpAllVisibleAndJavaTrees(dumpDir, "err-no-pin");
            return 13;
        }

        await WaitPumpAsync(500);
        DumpWindowTree(pinWin.Value, Path.Combine(dumpDir, "09-pin-dialog.txt"));
        Console.WriteLine($"[SUCCESS] PIN modal reached! Title='{pinWin.Value.Title}' HWND={pinWin.Value.Hwnd}");

        // Cancel PIN modal cleanly so DNIe is never locked
        var cancelBtn = FindNode(pinWin.Value.VmId, pinWin.Value.RootAc, n =>
            n.Info.role_en_US == "push button" && n.Info.name.Equals("Cancelar", StringComparison.OrdinalIgnoreCase));
        if (cancelBtn != null)
        {
            ClickNodeRect(cancelBtn.Value, "Cancelar PIN");
            await WaitPumpAsync(800);
        }

        // Dismiss any post-cancel alert modal if shown
        var alertWin = FindJavaWindow(t => t.Equals("ReFirma PDF", StringComparison.OrdinalIgnoreCase));
        if (alertWin != null)
        {
            DumpWindowTree(alertWin.Value, Path.Combine(dumpDir, "10-post-cancel-alert.txt"));
            var okBtn = FindNode(alertWin.Value.VmId, alertWin.Value.RootAc, n => n.Info.role_en_US == "push button");
            if (okBtn != null) ClickNodeRect(okBtn.Value, "Post-cancel OK");
            await WaitPumpAsync(500);
        }

        return 0;
    }

    private static async Task<bool> SetFolderViaChooserAsync(JavaWin batchWin, string btnDescContains, string expectedTitleContains, string folderPath)
    {
        var btn = FindNode(batchWin.VmId, batchWin.RootAc, n =>
            n.Info.role_en_US == "push button" && n.Info.description.Contains(btnDescContains, StringComparison.OrdinalIgnoreCase));
        if (btn == null)
        {
            Console.Error.WriteLine($"[Chooser] Button '{btnDescContains}' not found.");
            return false;
        }

        ClickNodeRect(btn.Value, btnDescContains);
        var chooser = await WaitJavaWindowAsync(t => t.Contains(expectedTitleContains, StringComparison.OrdinalIgnoreCase) || t.Contains("Seleccionar directorio", StringComparison.OrdinalIgnoreCase), 10);
        if (chooser == null)
        {
            Console.Error.WriteLine($"[Chooser] Dialog '{expectedTitleContains}' did not appear.");
            return false;
        }

        await WaitPumpAsync(400);
        var pathInput = FindNode(chooser.Value.VmId, chooser.Value.RootAc, n =>
            n.Info.role_en_US == "text" && n.Info.name.Contains("Nombre de carpeta", StringComparison.OrdinalIgnoreCase));
        if (pathInput == null)
        {
            Console.Error.WriteLine("[Chooser] 'Nombre de carpeta' input not found.");
            return false;
        }

        bool setOk = JabNative.setTextContents(chooser.Value.VmId, pathInput.Value.Ac, folderPath);
        Console.WriteLine($"[Chooser] setTextContents('{folderPath}') => {setOk}");
        await WaitPumpAsync(250);

        var openBtn = FindNode(chooser.Value.VmId, chooser.Value.RootAc, n =>
            n.Info.role_en_US == "push button" && n.Info.name.Equals("Abrir", StringComparison.OrdinalIgnoreCase));
        if (openBtn == null)
        {
            Console.Error.WriteLine("[Chooser] 'Abrir' button not found.");
            return false;
        }

        ClickNodeRect(openBtn.Value, "Abrir");
        await WaitPumpAsync(600);
        return true;
    }

    private static async Task<bool> HandleInputPromptAsync(string dumpDir, string dumpFile, string expectedPromptPart, string value)
    {
        var win = await WaitJavaWindowAsync(t => t.Equals("ReFirma PDF", StringComparison.OrdinalIgnoreCase), 10);
        if (win == null)
        {
            Console.Error.WriteLine($"[Prompt] Modal for '{expectedPromptPart}' did not appear.");
            return false;
        }
        await WaitPumpAsync(350);
        DumpWindowTree(win.Value, Path.Combine(dumpDir, dumpFile));

        var input = FindNode(win.Value.VmId, win.Value.RootAc, n => n.Info.role_en_US == "text");
        var okBtn = FindNode(win.Value.VmId, win.Value.RootAc, n =>
            n.Info.role_en_US == "push button" && n.Info.name.Equals("Aceptar", StringComparison.OrdinalIgnoreCase));
        if (input == null || okBtn == null)
        {
            Console.Error.WriteLine($"[Prompt] Input or Aceptar not found in '{expectedPromptPart}'.");
            return false;
        }

        bool setOk = JabNative.setTextContents(win.Value.VmId, input.Value.Ac, value);
        Console.WriteLine($"[Prompt] setTextContents('{value}') for '{expectedPromptPart}' => {setOk}");
        await WaitPumpAsync(200);
        ClickNodeRect(okBtn.Value, $"Aceptar {expectedPromptPart}");
        await WaitPumpAsync(500);
        return true;
    }

    private static async Task<JavaWin?> WaitJavaWindowAsync(Func<string, bool> titlePredicate, int timeoutSec)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSec)
        {
            await WaitPumpAsync(250);
            var win = FindJavaWindow(titlePredicate);
            if (win != null) return win;
        }
        return null;
    }

    private static void DumpAllVisibleAndJavaTrees(string dumpDir, string prefix)
    {
        var sb = new StringBuilder();
        foreach (var w in Win32.ListVisibleWindows())
        {
            if (w.ClassName.Contains("SunAwt") || w.Title.Contains("ReFirma") || w.Title.Contains("Abrir") || w.Title.Contains("Seleccionar") || w.ClassName == "#32770")
            {
                bool isJ = JabNative.isJavaWindow(w.Hwnd);
                bool got = JabNative.getAccessibleContextFromHWND(w.Hwnd, out int vm, out IntPtr ac);
                sb.AppendLine($"HWND={w.Hwnd} PID={w.Pid} Class='{w.ClassName}' Title='{w.Title}' isJava={isJ} gotAC={got} vm={vm}");
                if (got && ac != IntPtr.Zero)
                {
                    DumpWindowTree(new JavaWin(w.Hwnd, w.Title, w.Pid, vm, ac), Path.Combine(dumpDir, $"{prefix}_{w.Hwnd}.txt"));
                }
            }
        }
        File.WriteAllText(Path.Combine(dumpDir, $"{prefix}_windows.txt"), sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"[Dump] Wrote {prefix}_windows.txt");
    }

    private static async Task WaitPumpAsync(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            await Task.Delay(25);
        }
    }

    private static void ClickNodeRect(JabNode node, string label)
    {
        int cx = node.Info.x + node.Info.width / 2;
        int cy = node.Info.y + node.Info.height / 2;
        Console.WriteLine($"[Click] '{label}' at ({cx},{cy}) rect=({node.Info.x},{node.Info.y},{node.Info.width},{node.Info.height})");
        Win32.ClickAt(cx, cy);
    }

    private record struct JavaWin(IntPtr Hwnd, string Title, uint Pid, int VmId, IntPtr RootAc);
    private record struct JabNode(int VmId, IntPtr Ac, JabNative.AccessibleContextInfo Info, int Depth);

    private static JavaWin? FindJavaWindow(Func<string, bool> titlePredicate)
    {
        foreach (var w in Win32.ListVisibleWindows())
        {
            if (!titlePredicate(w.Title)) continue;
            if (!w.ClassName.Contains("SunAwt")) continue;
            if (JabNative.getAccessibleContextFromHWND(w.Hwnd, out int vmId, out IntPtr ac) && ac != IntPtr.Zero)
                return new JavaWin(w.Hwnd, w.Title, w.Pid, vmId, ac);
        }
        return null;
    }

    private static void DumpWindowTree(JavaWin win, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== WINDOW HWND={win.Hwnd} PID={win.Pid} vmID={win.VmId} Title='{win.Title}' ===");
        TraverseTree(win.VmId, win.RootAc, 0, n =>
        {
            AppendNodeLine(sb, n);
            return true;
        });
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"[Dump] Wrote {outputPath}");
    }

    private static void AppendNodeLine(StringBuilder sb, JabNode n)
    {
        var indent = new string(' ', n.Depth * 2);
        string actions = "";
        if (n.Info.accessibleAction && JabNative.getAccessibleActions(n.VmId, n.Ac, out var act) && act.actionsCount > 0)
            actions = string.Join(",", act.actionInfo.Take(act.actionsCount).Select(a => a.name));

        string text = n.Info.accessibleText ? GetNodeText(n) : "";
        sb.AppendLine($"{indent}[{n.Info.role_en_US}] name=\"{n.Info.name}\" desc=\"{n.Info.description}\" states=\"{n.Info.states_en_US}\" rect=({n.Info.x},{n.Info.y},{n.Info.width},{n.Info.height}) idx={n.Info.indexInParent} children={n.Info.childrenCount} actions=[{actions}] text=\"{text}\"");
    }

    private static string GetNodeText(JabNode n)
    {
        if (!n.Info.accessibleText) return "";
        if (!JabNative.getAccessibleTextInfo(n.VmId, n.Ac, out var ti, 0, 0) || ti.charCount <= 0)
            return "";
        int end = Math.Min(ti.charCount - 1, 200);
        var buf = new StringBuilder(end + 4);
        if (JabNative.getAccessibleTextRange(n.VmId, n.Ac, 0, end, buf, (short)(end + 2)))
            return buf.ToString().Replace("\r", "\\r").Replace("\n", "\\n");
        return "";
    }

    private static void TraverseTree(int vmId, IntPtr ac, int depth, Func<JabNode, bool> visitor, int maxDepth = 20)
    {
        if (ac == IntPtr.Zero || depth > maxDepth) return;
        if (!JabNative.getAccessibleContextInfo(vmId, ac, out var info)) return;

        var node = new JabNode(vmId, ac, info, depth);
        if (!visitor(node)) return;

        for (int i = 0; i < info.childrenCount; i++)
        {
            IntPtr child = JabNative.getAccessibleChildFromContext(vmId, ac, i);
            if (child != IntPtr.Zero)
            {
                TraverseTree(vmId, child, depth + 1, visitor, maxDepth);
                JabNative.releaseJavaObject(vmId, child);
            }
        }
    }

    private static JabNode? FindNode(int vmId, IntPtr rootAc, Func<JabNode, bool> predicate)
    {
        JabNode? found = null;
        void Walk(IntPtr ac, int depth)
        {
            if (found != null || ac == IntPtr.Zero || depth > 20) return;
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
                    if (found == null || found.Value.Ac != child)
                        JabNative.releaseJavaObject(vmId, child);
                }
            }
        }
        Walk(rootAc, 0);
        return found;
    }

    private static bool DoAccessibleClick(JabNode node)
    {
        if (node.Info.accessibleAction && JabNative.getAccessibleActions(node.VmId, node.Ac, out var acts) && acts.actionsCount > 0)
        {
            var todo = new JabNative.AccessibleActionsToDo
            {
                actionsCount = 1,
                actions = new JabNative.AccessibleActionInfo[JabNative.MAX_ACTIONS_TO_DO]
            };
            todo.actions[0] = new JabNative.AccessibleActionInfo { name = acts.actionInfo[0].name };
            return JabNative.doAccessibleActions(node.VmId, node.Ac, ref todo, out _);
        }
        return false;
    }

    private static void EnsureDummyPdf(string path)
    {
        if (File.Exists(path)) return;
        const string minimalPdf =
            "%PDF-1.4\n" +
            "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
            "2 0 obj<</Type/Pages/Count 1/Kids[3 0 R]>>endobj\n" +
            "3 0 obj<</Type/Page/MediaBox[0 0 595 842]/Parent 2 0 R/Resources<<>>>>endobj\n" +
            "xref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000054 00000 n \n0000000105 00000 n \n" +
            "trailer<</Size 4/Root 1 0 R>>\nstartxref\n182\n%%EOF\n";
        File.WriteAllText(path, minimalPdf, Encoding.ASCII);
    }

    private sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
        public override void Flush() { a.Flush(); b.Flush(); }
    }
}
