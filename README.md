# ReFirma Invoker — Integración Web API para Firma Digital

Agente de escritorio liviano para **Windows 10 / 11 (x64)** (**SignerBridge**) que expone una **API local segura (`127.0.0.1:48915`)** para automatizar el flujo de firma digital en lote de **RENIEC ReFirma PDF 1.6** desde aplicaciones web, pausando en el modal criptográfico para que el usuario ingrese manualmente su **PIN de Firma** y devolviendo los PDFs firmados en `.zip`.

Diseñado con arquitectura modular extensible (`ISignerProvider`) para incorporar otros firmadores como **Firma Perú** o **Firma ONPE** sin cambiar el contrato de la API.

---

## Características principales

- **API REST + WebSocket local segura (`127.0.0.1:48915`):**
  - Autenticación por token (`X-Signer-Token`) persistido cifrado con **Windows DPAPI**.
  - Soporte **CORS + Private Network Access (PNA)** (`Access-Control-Allow-Private-Network: true`) para consumo desde aplicaciones web HTTPS.
  - Cola FIFO con exclusión mutua (`1` sesión de firma UI a la vez) y limpieza automática de archivos temporales.
- **Automatización robusta de ReFirma PDF 1.6:**
  - Control directo del árbol Java Swing mediante **Java Access Bridge 64-bit (`WindowsAccessBridge-64.dll`)** en hilo STA dedicado (`PerMonitorV2` DPI aware).
  - Selección de certificado por **APELLIDOS Nombres**, **FIR** o **SN (Serial)**, motivo, sello visible, nivel PAdES y coordenadas **X / Y** o grilla.
  - Monitoreo híbrido **UI + `refirma-pdf.log`** para detectar éxito, cancelación de usuario, PIN incorrecto/bloqueado o errores TSL.
- **System Tray & Resiliencia Windows:**
  - Corre en la bandeja del sistema en sesión interactiva (instancia única), con copiado rápido del token, visor de logs, auto-arranque opcional y protocolo `signer-bridge://wake`.

---

## Estructura del Proyecto

```text
├── src/
│   ├── SignerBridge.Core/                 # Contratos, DPAPI, ArchiveService, JobManager
│   ├── SignerBridge.Providers.ReFirmaPdf/ # Motor JAB 64-bit, monitor híbrido y proveedor ReFirma
│   └── SignerBridge.Host/                 # System Tray + API Kestrel + seguridad CORS/PNA
├── tests/
│   ├── SignerBridge.Core.Tests/           # Pruebas unitarias y de integración (xUnit)
│   └── test-web-client.html               # Cliente web de prueba E2E
├── docs/
│   ├── web-integration-guide.md           # Documentación completa de API y campos
│   └── smoke-test-report.md               # Reporte de Fase 0 (selectores Swing JAB)
├── scripts/publish-win-x64.ps1            # Script de publicación autocontenida
└── installer/SignerBridge.iss             # Script de instalador Inno Setup
```

---

## Uso Rápido (Ejecutable Portable / Instalador)

1. Descarga el ejecutable `ReFirmaInvoker-win-x64.exe` desde la sección **Releases** de este repositorio.
2. Asegúrate de tener **ReFirma PDF 1.6** instalado en el equipo Windows.
3. Ejecuta `ReFirmaInvoker-win-x64.exe`: aparecerá el ícono en la bandeja del sistema.
4. Haz clic derecho sobre el ícono -> **Copiar Token de Pairing**.
5. Configura los orígenes permitidos en `appsettings.json` (`AllowedOrigins`) si tu sistema web corre en un dominio distinto a `localhost`.

Para integrar desde tu aplicación web, consulta la guía completa en [docs/web-integration-guide.md](docs/web-integration-guide.md).

---

## Compilación y Pruebas desde Código Fuente

Requiere **.NET 8 SDK** (`win-x64`):

```powershell
# Ejecutar pruebas unitarias y de integración
dotnet test SignerBridge.sln -c Release

# Publicar ejecutable único autocontenido (win-x64)
.\scripts\publish-win-x64.ps1 -SingleFile
```
