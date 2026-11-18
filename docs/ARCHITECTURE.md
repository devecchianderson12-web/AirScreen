# Arquitectura

```
 Windows                                                                  Android
┌──────────────────────────────────────────────────────────┐          ┌──────────────────────────────┐
│ DualScreen.exe (usuario normal, portable)                │          │ DualScreen.apk               │
│                                                          │          │                              │
│  Bandeja + UI WPF ──► AppController                       │          │ MainActivity  ScanActivity   │
│                         │                                │          │      │                       │
│        ┌────────────────┼──────────────┐                 │          │ StreamHub ◄─ StreamService   │
│        ▼                ▼              ▼                 │          │   │  (primer plano, WifiLock)│
│  DisplayManager   StreamPipeline   LanServer/RemoteHost  │  TLS/TCP │   ▼                          │
│   │  IOCTL          WGC ► VideoProcessor ► MF encoder ───┼──────────┼─► VideoDecoder (MediaCodec)  │
│   ▼                 (GPU, cero copias)  NVENC/QSV/AMF    │  WebRTC  │      └► SurfaceView          │
│ DualScreenIdd (UMDF/IddCx) ◄── Windows lo ve como        │          │         DisplayActivity      │
│   monitor físico (WDDM)                                  │          └──────────────────────────────┘
└──────────────────────────────────────────────────────────┘
                     ▲  señalización (WSS) + STUN/TURN (solo remoto)
                     └──────────── signaling-server (Go) + coturn
```

## Decisiones técnicas

### Monitor real: Indirect Display Driver (IddCx)
La única forma soportada por Microsoft de que Windows trate un dispositivo no físico como monitor (con
modo extendido, disposición, rotación, frecuencias y DPI propios) es un **Indirect Display Driver** sobre
WDDM. Es un controlador UMDF (modo usuario, no puede colgar el kernel). Alternativas descartadas:

- *Ventana flotante* o *captura del escritorio*: no crea un monitor; descartado por requisito.
- *Controlador de pantalla WDDM completo (KMD)*: enorme y de riesgo; IddCx es el camino oficial.
- *Dongle HDMI falso*: requiere hardware.

El controlador solo crea/elimina monitores bajo demanda (IOCTL) y vacía su *swap chain*; el contenido se
captura desde la app con **Windows.Graphics.Capture**, que funciona igual en monitores IddCx, incluye el
cursor y solo genera fotogramas cuando cambia la pantalla (cero coste en reposo).

### Codificación
`Windows.Graphics.Capture` → copia GPU → `ID3D11VideoProcessor` (BGRA→NV12 BT.709, escalado/letterbox en el
motor de vídeo) → **MFT de hardware asíncrono** de Media Foundation, que es la interfaz común a **NVENC**,
**Intel Quick Sync** y **AMD AMF**. Todo en el mismo dispositivo D3D11: sin copias a CPU.
Configuración: CBR, `LowLatencyMode`, sin B‑frames, VBV ≈ 1 fotograma, IDR bajo demanda.
Respaldo: MFT H.264 de software de Microsoft. H.264 por defecto (menor latencia); HEVC cuando la tasa de
píxeles supera 1440p60 y ambos lo soportan; AV1 si el usuario lo elige y hay hardware en ambos lados.

### Por qué no WebRTC para el vídeo en LAN
WebRTC añade un *jitter buffer* y control de congestión pensados para videollamadas (decenas de ms extra).
En LAN se usa TLS/TCP directo con control propio basado en retardo (`BitrateController`), que nunca encola
fotogramas: si la red va atrasada, simplemente no se captura el siguiente. WebRTC sí se usa en remoto porque
resuelve NAT (ICE/STUN/TURN); allí el vídeo viaja por un canal de datos fiable, reutilizando el mismo protocolo.

### Latencia (presupuesto típico en Wi‑Fi 5/6, 1080p120)
| Etapa | Tiempo aproximado |
|---|---|
| Composición DWM → WGC | 1 fotograma (8 ms a 120 Hz) |
| Conversión NV12 + codificación HW | 2–5 ms |
| Red (Wi‑Fi local) | 2–10 ms |
| Decodificación HW en baja latencia | 3–8 ms |
| Presentación en el panel | ≤ 1 fotograma |

Son valores de referencia, no mediciones de este proyecto: la latencia real se muestra en vivo en ambas apps
(RTT/2 + codificación + cola + decodificación).

### Actualizaciones
`UpdateService` consulta `releases/latest` de GitHub cada N horas. La descarga se verifica con
su SHA‑256 **y** una firma ECDSA P‑256 (ambos en un comentario HTML invisible de las notas de la release) hecha en CI con una clave privada que solo existe como *secret*;
la clave pública va embebida en el exe. Luego se lanza una **copia temporal del propio exe** en modo
`--apply-update` (actualizador independiente), que espera a que el proceso viejo termine, renombra
`DualScreen.exe → DualScreen.exe.old` (Windows permite renombrar un ejecutable en uso), coloca el nuevo,
lo arranca con `--post-update TOKEN` y espera a que señale un evento con nombre. Si no lo hace en 45 s,
mata el nuevo, restaura el `.old` y arranca la versión anterior. Si la carpeta no es escribible, el
actualizador se eleva con UAC.

### Privilegios
| Acción | Privilegios |
|---|---|
| Ejecutar DualScreen.exe, modo espejo, red, actualizaciones | Usuario normal |
| Instalar/desinstalar el controlador | Administrador (un aviso UAC; proceso auxiliar `--driver install`) |
| Controlador en Windows sin modo de prueba | Firma de Microsoft (ver DRIVER.md) |
| Actualizar si el exe está en `Program Files` | Administrador (UAC automático) |
| Firewall de Windows | Windows pedirá permitir el puerto la primera vez (red privada) |

### Consumo en reposo
Sin teléfono conectado no existe dispositivo D3D ni codificador; la ventana se destruye al cerrarla (no se
oculta). Quedan solo dos sockets (TCP/UDP), un temporizador de actualizaciones y el icono de bandeja.

### Datos locales
`%LOCALAPPDATA%\DualScreen` (o `DualScreenData\` junto al exe si existe `portable.txt`):
`settings.json`, `identity.bin` (PFX cifrado con DPAPI), `devices.json` (hashes de tokens, cifrado DPAPI),
`logs\` (7 días).
