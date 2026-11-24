# Guía de Integración Web — SignerBridge API v1

**URL base por defecto:** `http://127.0.0.1:48915`  
**Protocolo de despertar bajo demanda (opcional):** `signer-bridge://wake`  
**Autenticación obligatoria:** Header `X-Signer-Token: <token>` (en WebSockets: query `?token=<token>`).

---

## 1. Requisitos previos de Seguridad y CORS / PNA

1. **Lista blanca de orígenes (`AllowedOrigins`):**
   Edita `appsettings.json` en la instalación de SignerBridge y agrega el origen exacto de tu aplicación web (sin barra final):
   ```json
   "AllowedOrigins": [
     "http://localhost",
     "https://tu-sistema.mpfn.gob.pe"
   ]
   ```
   *(Chrome y Edge aplican **Private Network Access (PNA)** cuando una web HTTPS llama a `127.0.0.1`; SignerBridge responde automáticamente `Access-Control-Allow-Private-Network: true` en el preflight `OPTIONS` para los orígenes autorizados).*
2. **Token de Pairing (`X-Signer-Token`):**
   El usuario obtiene el token haciendo clic derecho en el ícono de **SignerBridge** en la bandeja del sistema -> **Copiar Token de Pairing** (se guarda cifrado con DPAPI en `%LOCALAPPDATA%\SignerBridge\pairing.dat`).

---

## 2. Endpoints de la API

### `GET /v1/health`
Verifica que el agente está activo, si está ocupado firmando otro lote y qué firmadores están disponibles.
- **Headers:** `X-Signer-Token: <token>`
- **Respuesta `200 OK`:**
  ```json
  {
    "service": "SignerBridge",
    "version": "1.0.0",
    "busy": false,
    "activeJobId": null,
    "providers": [
      {
        "providerId": "refirma-pdf",
        "displayName": "RENIEC ReFirma PDF 1.6",
        "available": true,
        "detail": "ReFirma PDF 1.6 y JAB 64-bit disponibles."
      }
    ]
  }
  ```

### `POST /v1/sign/jobs`
Envía un lote de PDFs (recomendado **1 archivo `.zip`** para 50–150 PDFs, o múltiples campos `files`) junto con el JSON de configuración en `multipart/form-data`.
- **Headers:** `X-Signer-Token: <token>`
- **Form fields (`multipart/form-data`):**
  - `archive`: Archivo `.zip` que contiene los `.pdf` a firmar *(recomendado para lotes grandes)*.
  - `files`: *(alternativo a `archive`)* Uno o varios archivos `.pdf`.
  - `payload`: String JSON con la configuración de firma (ver Sección 3).
- **Respuestas:**
  - `202 Accepted`: Devuelve el objeto `SignJobResponse` con `jobId`, `eventsWsPath` y `resultPath`.
  - `400 Bad Request`: ZIP corrupto, archivo sin cabecera `%PDF-`, proveedor desconocido o sin PDFs.
  - `423 Locked`: La sesión de Windows está bloqueada.

### `GET /v1/sign/jobs/{jobId}`
Consulta el estado actual del job (útil para *polling* como respaldo de WebSocket).

### `WS /v1/sign/jobs/{jobId}/events?token=<token>`
Canal WebSocket que emite eventos `SignJobEvent` en tiempo real conforme avanza la automatización:
```json
{
  "JobId": "a1b2c3...",
  "Status": "WAITING_USER_PIN",
  "Message": "Esperando que el usuario ingrese el PIN de Firma en ReFirma PDF...",
  "Timestamp": "2026-11-24T17:05:00Z",
  "ErrorCode": "NONE",
  "ErrorDetail": null,
  "FilesCount": null
}
```

### `GET /v1/sign/jobs/{jobId}/result`
Descarga el `.zip` (`application/zip`) con todos los PDFs firmados y un `manifest.json` interno con tamaño y SHA-256 por archivo.
- Solo responde `200 OK` cuando `Status == "SUCCEEDED"` (si no, responde `409 Conflict` con el estado/error).
- Si `CleanupAfterResultDownload` es `true`, borra automáticamente los temporales tras entregar el ZIP.

### `DELETE /v1/sign/jobs/{jobId}`
Cancela un trabajo y elimina su carpeta temporal.

---

## 3. Campos Permitidos en `payload` (JSON)

```json
{
  "provider": "refirma-pdf",
  "certificate": {
    "matchBy": "FIR",
    "query": "09428893"
  },
  "signature": {
    "visible": true,
    "stampIndex": 0,
    "level": "PAdES_B",
    "certifyDocuments": false,
    "reason": "Soy el autor del documento",
    "position": {
      "mode": "XY",
      "x": 120,
      "y": 450
    }
  },
  "overwrite": false,
  "timeouts": {
    "appStartupSec": 45,
    "userPinSec": 180,
    "signingSec": 120
  }
}
```

| Campo | Tipo | Obligatorio | Valores / Comportamiento |
| :--- | :--- | :--- | :--- |
| `provider` | `string` | No (def: `"refirma-pdf"`) | Identificador del firmador (`"refirma-pdf"`). |
| `certificate.matchBy` | `string` | No (def: `"AUTO"`) | Cómo buscar el certificado en la lista de ReFirma:<br>• `"FULL_NAME"`: por `APELLIDOS Nombres`<br>• `"FIR"`: por código FIR/DNI (ej. `"09428893"` o `"FIR 09428893"`)<br>• `"SN"`: por número de serie (con o sin `:`)<br>• `"AUTO"`: coincidencia flexible |
| `certificate.query` | `string` | No (def: `""`) | Texto a buscar. Si se omite o es vacío, selecciona el primer certificado disponible. |
| `signature.visible` | `boolean` | No (def: `true`) | Activa/desactiva la casilla `"Firma visible (1ra página)"`. |
| `signature.stampIndex` | `int?` | No (def: `null`) | Índice base-0 de la imagen de sello (`0`, `1`, ...). Si se omite, usa la seleccionada por defecto (la primera). |
| `signature.level` | `string` | No (def: `"PAdES_B"`) | Nivel de firma: `"PAdES_B"`, `"PAdES_T"` o `"PAdES_LTA"`. |
| `signature.certifyDocuments` | `boolean` | No (def: `false`) | Activa/desactiva `"Certificar los documentos"`. |
| `signature.reason` | `string` | No (def: `"Soy el autor del documento"`) | Motivo de firma. En ReFirma PDF 1.6 las 5 opciones válidas son:<br>• `"Soy el autor del documento"`<br>• `"En señal de conformidad"`<br>• `"Doy V° B°"`<br>• `"Por encargo"`<br>• `"Doy fe"` |
| `signature.position.mode` | `string` | No (def: `"GRID"`) | `"XY"` (abre botón celeste `X / Y` e ingresa `x` e `y`) o `"GRID"` (grilla 5x3). |
| `signature.position.x` | `int?` | Si `mode == "XY"` | Coordenada horizontal `X`. |
| `signature.position.y` | `int?` | Si `mode == "XY"` | Coordenada vertical `Y`. |
| `signature.position.gridIndex` | `int?` | Si `mode == "GRID"` | Índice `0..14` de los 15 radio buttons de posición. |
| `overwrite` | `boolean` | No (def: `false`) | Casilla `"Sobrescribir"` en carpeta destino. |
| `timeouts.appStartupSec` | `int` | No (def: `45`) | Segundos máximos para abrir ReFirma y pasar `"Iniciando la aplicación"`. |
| `timeouts.userPinSec` | `int` | No (def: `180`) | Segundos máximos esperando que el usuario ingrese su PIN. |
| `timeouts.signingSec` | `int` | No (def: `120`) | Segundos máximos esperando la escritura de los PDFs firmados tras ingresar el PIN. |

---

## 4. Estados y Códigos de Error

- **Estados (`SignJobStatus`):** `QUEUED` -> `LAUNCHING_APP` -> `CONFIGURING_BATCH` -> `AWAITING_DECLARATION` -> `WAITING_USER_PIN` -> `SIGNING_IN_PROGRESS` -> `SUCCEEDED` | `FAILED` | `CANCELLED` | `TIMED_OUT`.
- **Códigos de Error (`SignErrorCode`):** `NONE`, `APP_NOT_INSTALLED`, `CERT_NOT_FOUND`, `NO_PDF_FILES`, `USER_CANCELLED_PIN`, `INVALID_PIN_OR_LOCKED`, `TSL_VALIDATION_ERROR`, `UI_AUTOMATION_ERROR`, `TIMEOUT`, `INVALID_REQUEST`, `INTERNAL_ERROR`.

---

## 5. Ejemplo de Integración en JavaScript (Frontend Web)

```javascript
async function firmarLoteConSignerBridge({ baseUrl = "http://127.0.0.1:48915", token, zipBlob, payload, onStatus }) {
  const form = new FormData();
  form.append("archive", zipBlob, "lote.zip");
  form.append("payload", JSON.stringify(payload));

  const res = await fetch(`${baseUrl}/v1/sign/jobs`, {
    method: "POST",
    headers: { "X-Signer-Token": token },
    body: form
  });
  if (!res.ok) throw new Error(await res.text());
  const job = await res.json();

  await new Promise((resolve, reject) => {
    const wsUrl = baseUrl.replace(/^http/, "ws") + `${job.eventsWsPath}?token=${encodeURIComponent(token)}`;
    const ws = new WebSocket(wsUrl);
    ws.onmessage = (ev) => {
      const event = JSON.parse(ev.data);
      onStatus?.(event); // Muestra alerta al usuario cuando event.Status === "WAITING_USER_PIN"
      if (event.Status === "SUCCEEDED") { ws.close(); resolve(); }
      if (["FAILED", "CANCELLED", "TIMED_OUT"].includes(event.Status)) {
        ws.close();
        reject(new Error(`${event.ErrorCode}: ${event.ErrorDetail || event.Message}`));
      }
    };
    ws.onerror = () => reject(new Error("Error de conexión WebSocket con SignerBridge"));
  });

  const zipRes = await fetch(`${baseUrl}${job.resultPath}`, {
    headers: { "X-Signer-Token": token }
  });
  if (!zipRes.ok) throw new Error("No se pudo descargar el ZIP firmado");
  return await zipRes.blob(); // ZIP con los PDFs firmados + manifest.json
}
```
