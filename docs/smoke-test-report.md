# Reporte de Prueba de Humo (Fase 0) — ReFirma PDF 1.6

**Fecha:** 2026-11-24  
**Veredicto:** **GO (100% Viable)**  
**Proyecto:** `C:\Users\Usuario\Desktop\MPFN\PROYECTOS\auto-firma-digital`  
**Herramienta validada:** C# / .NET 8 (`net8.0-windows`, `x64`, `PerMonitorV2`) + P/Invoke `WindowsAccessBridge-64.dll`

---

## 1. Resumen Ejecutivo

Se verificó empíricamente que **ReFirma PDF 1.6** puede ser controlado de extremo a extremo de forma determinista hasta la aparición del modal de ingreso manual de PIN (**`ReFirma PDF - DNIe`**), y que la cancelación o eventos criptográficos quedan reflejados tanto en la UI como en el log en tiempo real `C:\Users\Usuario\AppRENIEC\refirma-pdf\refirma-pdf.log`.

---

## 2. Hallazgos Críticos de Arquitectura (Validados en Vivo)

1. **Windows UIA estándar NO ve el interior de ReFirma PDF:**
   - Inspeccionado vía Microsoft UI Automation (`detectedFramework: Win32`): la ventana principal expone solo 7 nodos (barra de título) y el modal de lote expone solo 3 nodos.
   - **Conclusión:** El uso de **Java Access Bridge (JAB 64-bit)** sobre el JRE privado de ReFirma (`C:\Users\Usuario\AppRENIEC\common\jre\jre-8\bin\WindowsAccessBridge-64.dll`) es **obligatorio**.

2. **Activación de JAB:**
   - El JRE embebido de ReFirma (`1.8.0_151-b12` 64-bit) trae `jabswitch.exe`, pero `accessibility.properties` viene comentado por defecto.
   - Al ejecutar `jabswitch.exe -enable`, se habilitó `%USERPROFILE%\.accessibility.properties` (`assistive_technologies=com.sun.java.accessibility.AccessBridge`) y ReFirma expuso inmediatamente **114 nodos Swing** en la ventana principal y **79+ nodos** en los modales.

3. **Reglas de oro para estabilidad con JAB (descubiertas en Fase 0):**
   - **Loop de mensajes Windows real (`Application.Run`):** `JabNative.Windows_run()` debe correr en un hilo STA con un pump continuo de mensajes WinForms (`Application.Run` + `Timer`/`async` + `DoEvents`). Sin pump continuo, `isJavaWindow` / `getAccessibleContextFromHWND` falla en diálogos `SunAwtDialog`.
   - **High-DPI `PerMonitorV2` obligatorio:** `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` debe invocarse al inicio de `Main()`. Sin él, Windows escala las coordenadas físicas devueltas por JAB y los clics físicos caen desplazados (por ejemplo, cayendo sobre `Salir` en vez de `Firmar en lote...`).
   - **No usar `doAccessibleActions` para disparar diálogos modales:** En `JMenuItem` (`Firmar en lote...`), `doAccessibleActions('hacer clic')` retorna `ok=False`. Se debe abrir el menú `Archivo` con `doAccessibleActions('hacer clic')` para desplegar el popup con coordenadas físicas válidas, y luego hacer clic Win32 en el rectángulo de `Firmar en lote...`. Lo mismo aplica a botones que abren modales (`...`, `X / Y`, `Aceptar`, `Firmar`).
   - **Sesión interactiva requerida:** Procesos en desktops aislados/Session 0 no ven las ventanas `SunAwt*` del usuario. El agente final debe ejecutarse en la sesión interactiva del usuario (System Tray).

---

## 3. Mapa de Controles Verificados (Selectores JAB)

| Pantalla / Modal | Título Ventana (`SunAwt*`) | Control | Selector JAB Verificado | Método de Interacción Validado | Dump |
| :--- | :--- | :--- | :--- | :--- | :--- |
| Ventana Principal | `ReFirma PDF 1.6` | Menú `Archivo` | `role="menu"`, `name="Archivo"` | `doAccessibleActions("hacer clic")` | [01-main-window.txt](dumps/01-main-window.txt) |
| Ventana Principal | `ReFirma PDF 1.6` | Opción `Firmar en lote...` | `role="menu item"`, `name` contiene `"Firmar en lote"` | Win32 Click en centro de `rect` tras abrir `Archivo` | [01-main-window.txt](dumps/01-main-window.txt) |
| Parámetros Lote | `ReFirma PDF - Parámetros de firma digital en lote` | Botón `...` Entrada | `role="push button"`, `desc` contiene `"documentos para firmar"` | Win32 Click en centro de `rect` | [02-batch-dialog.txt](dumps/02-batch-dialog.txt) |
| Selector Carpeta | `Seleccionar directorio de entrada` / `salida` | Input ruta | `role="text"`, `name` contiene `"Nombre de carpeta"` | `JabNative.setTextContents(...)` (`True`) | [03-folder-chooser_3607058.txt](dumps/03-folder-chooser_3607058.txt) |
| Selector Carpeta | `Seleccionar directorio de entrada` / `salida` | Botón `Abrir` | `role="push button"`, `name="Abrir"` | Win32 Click en centro de `rect` | [03-folder-chooser_3607058.txt](dumps/03-folder-chooser_3607058.txt) |
| Parámetros Lote | `...Parámetros de firma digital en lote` | Botón `...` Salida | `role="push button"`, `desc` contiene `"carpeta destino"` | Win32 Click en centro de `rect` | [02-batch-dialog.txt](dumps/02-batch-dialog.txt) |
| Parámetros Lote | `...Parámetros de firma digital en lote` | Lista Certificados | `role="label"` dentro de `role="list"` | Match por Nombre / `FIR` / `SN` + Win32 Click | [05-batch-after-cert.txt](dumps/05-batch-after-cert.txt) |
| Parámetros Lote | `...Parámetros de firma digital en lote` | Checkbox `Firma visible` | `role="check box"`, `name` contiene `"Firma visible"` | Verificar estado `checked` | [05-batch-after-cert.txt](dumps/05-batch-after-cert.txt) |
| Parámetros Lote | `...Parámetros de firma digital en lote` | Sellos (`0.png`, `1.png`) | `role="toggle button"` (`idx=0` checked por defecto, `idx=1`) | Click opcional si `stampIndex > 0` | [05-batch-after-cert.txt](dumps/05-batch-after-cert.txt) |
| Parámetros Lote | `...Parámetros de firma digital en lote` | Botón `Posición` | `role="push button"`, `desc` contiene `"posición"` | Win32 Click en centro de `rect` | [04-batch-with-pos-popup.txt](dumps/04-batch-with-pos-popup.txt) |
| Popup Posición | *(dentro del layered pane del modal)* | Grilla 5x3 / Opción `X / Y` | `15` `radio button` (`idx=0..14`) + `menu item` `name="X / Y"` | Win32 Click en `X / Y` | [04-batch-with-pos-popup.txt](dumps/04-batch-with-pos-popup.txt) |
| Modal Coord X / Y | `ReFirma PDF` | Prompt X e Y | `label` `"Introduce la coordenada X:"` / `"Y:"`, `role="text"`, `push button` `"Aceptar"` | `setTextContents` + Win32 Click en `Aceptar` | [06-coord-x.txt](dumps/06-coord-x.txt), [07-coord-y.txt](dumps/07-coord-y.txt) |
| Parámetros Lote | `...Parámetros de firma digital en lote` | `Motivo:` | `role="combo box"` (se habilita tras elegir certificado; 5 ítems: `Soy el autor del documento`, `En señal de conformidad`, `Doy V° B°`, `Por encargo`, `Doy fe`) | Seleccionar ítem de lista *(nota: no es combo editable libre)* | [05-batch-after-cert.txt](dumps/05-batch-after-cert.txt) |
| Parámetros Lote | `...Parámetros de firma digital en lote` | Botón `Aceptar` | `role="push button"`, `name="Aceptar"` (`enabled` tras cert+carpetas) | Win32 Click en centro de `rect` | [05-batch-after-cert.txt](dumps/05-batch-after-cert.txt) |
| Declaración | `ReFirma PDF - Firma digital en lote - Declaración` | Campo `ACEPTO` y `Firmar` | `role="text"` editable + `push button` `name="Firmar"` | Click + `SendKeys("ACEPTO")` -> verifica `Firmar` `enabled` -> Click `Firmar` | [08-declaracion.txt](dumps/08-declaracion.txt) |
| PIN DNIe | `ReFirma PDF - DNIe` | Modal PIN | `label` `"PIN de Firma"`, `role="password text"`, `label` intentos, `Aceptar`/`Cancelar` | Esperar ingreso manual del usuario + vigilar cierre + `refirma-pdf.log` | [09-pin-dialog.txt](dumps/09-pin-dialog.txt) |

---

## 4. Evidencia de Ejecución E2E (Hasta Modal PIN + Cancelación Limpia)

En la ejecución `SmokeTest.exe e2e-pin`:
- Entorno de prueba: `tools/SmokeTest/test-in/documento-prueba.pdf` -> `tools/SmokeTest/test-out`.
- Certificado seleccionado: `VALDIVIA ALVARADO Beatriz Rocio FIR 09428893 hard (SN:362E9833BE2C815573F13729B7FEE49346DA3662)`.
- Coordenadas aplicadas: `X=120`, `Y=450`.
- Declaración: escribió `ACEPTO`, el botón `Firmar` pasó a `enabled` y abrió el modal `ReFirma PDF - DNIe` (`HWND=1903236`).
- `refirma-pdf.log` registró:
  - `Validación en la TSL OK!`
  - `DniePeruSignPinDialog init ... begin` (`username: BEATRIZ VALDIVIA (09428893)`, `Quedan 5 intentos de validación de PIN`)
  - Al pulsar `Cancelar` desde el test: `DniePeruSignPinDialog cancelled`.

**Conclusión:** Compuerta **Fase 0 aprobada (GO)**. Listo para iniciar la **Fase 1**.
