# Protocolo DualScreen v1

Implementaciones: `windows/src/DualScreen/Net/Protocol.cs` y `android/app/src/main/java/com/dualscreen/app/net/Protocol.kt`.
Cualquier cambio incompatible incrementa `proto`.

## Transportes

| Modo | Transporte | Cifrado y autenticación del PC |
|---|---|---|
| LAN | TCP `47800` (configurable), Nagle desactivado | TLS 1.2/1.3 con certificado ECDSA P‑256 autofirmado; el teléfono fija su huella SHA‑256 |
| Remoto | Canal de datos WebRTC `ds` ordenado y fiable (SCTP/DTLS) | DTLS; la oferta SDP (que contiene la huella DTLS) va firmada con la clave de identidad del PC |

Ambos se exponen como un flujo de bytes con el mismo framing; el canal WebRTC se trocea en mensajes de 16 KiB.

## Descubrimiento (LAN)

El teléfono envía `DSDISCOVER1` por difusión UDP al puerto `47801`. Cada PC responde por unicast:

```json
{"magic":"DSHOST1","hostId":"…32 hex…","name":"PC","port":47800,"fp":"…64 hex…","version":"0.1.0","proto":1}
```

`hostId` = primeros 16 bytes (hex) de SHA‑256(SubjectPublicKeyInfo del certificado). La huella `fp`
obtenida así **no** se considera de confianza por sí sola.

## Código QR

```
dualscreen://pair?v=1&h=<hostId>&n=<nombre>&a=<ip1,ip2>&p=<puerto>&f=<huella>&c=<código>[&s=<wss://señalización>]
```

## Framing

```
[tipo: 1 byte][longitud: uint32 big-endian][carga útil]
```

Longitud máxima: 16 MiB.

| Tipo | Nombre | Dirección | Carga |
|---|---|---|---|
| 0x01 | HELLO | teléfono → PC | JSON `HelloMsg` |
| 0x02 | WELCOME | PC → teléfono | JSON `WelcomeMsg` |
| 0x03 | STREAM_CONFIG | PC → teléfono | JSON `{mime,width,height,fps,bitrateKbps,mode,rotation}` |
| 0x04 | VIDEO | PC → teléfono | `flags:1` (bit0 = keyframe) · `ptsUs:int64 BE` · unidades de acceso (Annex‑B para H.264/HEVC, OBU para AV1) |
| 0x05 | PING | ambas | `int64 BE` marca de tiempo del emisor (µs) |
| 0x06 | PONG | ambas | eco de la carga del PING |
| 0x07 | STATS | teléfono → PC | JSON `{fps,decodeMs,recvKbps,dropped}` cada segundo |
| 0x08 | KEYFRAME_REQUEST | teléfono → PC | vacío |
| 0x09 | CLIENT_SETTINGS | teléfono → PC | JSON `{quality}` (`auto,maximum,high,balanced,saver`) |
| 0x0A | BYE | ambas | JSON `{reason}` |
| 0x0B | INPUT | receptor → PC | evento binario de entrada (ver abajo) |
| 0x0C | CURSOR | PC → receptor | posición `[2][visible u8][x u16][y u16]` (solo con `features: cursorPos`, para el modo touchpad; el puntero siempre va dibujado en el vídeo) |
| 0x0D | AUDIO | PC → receptor | `[frecuencia u32][canales u8][ptsUs i64][PCM s16le]` (si el receptor pidió `audio`) |
| 0x0E | HOST_STATE | PC → receptor | JSON con los ajustes actuales y opciones (modo, monitores, resoluciones, Hz, calidad, prioridad, códecs, audio) |
| 0x0F | HOST_SETTINGS | receptor → PC | JSON parcial con los mismos campos; la PC lo aplica a sus ajustes y reinicia la transmisión si hace falta |

`caps.features` (opcional): `cursorPos`, `audio`, `hostSettings`, `latency`.
PONG lleva, después del eco de 8 bytes, la hora de la PC (µs, `int64`): con el RTT más bajo el receptor estima el
desfase de relojes y calcula la latencia real `hora local + desfase − pts` (el `pts` de VIDEO es la hora de captura).
`STATS` incluye `latencyMs`.

### INPUT (receptores con pantalla táctil, lápiz, mouse o teclado)

Big-endian. `x`, `y` normalizados 0..65535 sobre el fotograma de vídeo (el PC deshace el *letterbox* del modo espejo).
El receptor declara lo que envía en `caps.input` (`["touch","pen","mouse","keyboard"]`) y `caps.platform` (`"windows"`).

| Primer byte | Evento | Resto |
|---|---|---|
| 1 | Toque | `id:u8` `estado:u8` (0 abajo, 1 mueve, 2 arriba, 3 cancelado) `x:u16` `y:u16` `presión:u16` (0..1024) |
| 2 | Lápiz | `estado:u8` (0 flota, 1 abajo, 2 mueve, 3 arriba, 4 sale) `x` `y` `presión` `botones:u8` (1 lateral, 2 invertido, 4 borrador) |
| 3 | Mouse: mover | `x` `y` |
| 4 | Mouse: botón | `botón:u8` (0 izq, 1 der, 2 medio, 3 X1, 4 X2) `abajo:u8` `x` `y` |
| 5 | Rueda | `horizontal:u8` `delta:i16` |
| 6 | Tecla | `scancode:u16` `flags:u8` (bit0 soltar, bit1 extendida) |
| 7 | Soltar todo | — (al minimizar o salir: nada queda presionado en el PC) |

El PC solo los aplica si «Permitir control desde el monitor vinculado» está activo, y únicamente sobre el monitor transmitido.

### HELLO

```json
{
  "proto": 1, "deviceId": "uuid", "deviceName": "Pixel 8", "model": "Google Pixel 8", "appVersion": "0.1.0",
  "auth": {"mode": "token", "token": "base64"}            // dispositivo emparejado
       // o {"mode":"pair","nonce":"hex≥16","proof":"hex"}  // primer emparejamiento
       // o {"mode":"lan"}  // sin código: solo por la red local y si el PC lo permite (si no, WELCOME error "code-required")
  "caps": {
    "screenWidth": 1080, "screenHeight": 2400, "densityDpi": 420,
    "refreshRates": [60.0, 120.0],
    "codecs": [{"mime":"video/avc","hardware":true,"maxWidth":4096,"maxHeight":2304,
                "modes":[{"width":2400,"height":1080,"maxFps":120}, …]}],
    "abi": "arm64-v8a", "sdkInt": 35
  },
  "quality": "auto"
}
```

`modes` se calcula con `MediaCodecInfo.VideoCapabilities` sobre la lista común de resoluciones
(`Negotiator.StandardResolutions`) más la nativa del panel.

### Emparejamiento

`proof = hex(HMAC-SHA256(key = código de 6 dígitos, "DSPAIR1|" + huellaPC + "|" + deviceId + "|" + nonce))`

El PC verifica en tiempo constante. El código caduca a los 5 minutos, es de un solo uso y se invalida tras
5 intentos fallidos. Si es correcto, el PC genera un token de 256 bits, guarda solo su SHA‑256 (cifrado con
DPAPI) y lo envía una única vez en `WELCOME.deviceToken`. El teléfono lo guarda cifrado con una clave AES‑GCM
del Android Keystore.

Errores de `WELCOME` (`ok:false`): `bad-code`, `code-expired`, `too-many-attempts`, `not-paired`, `busy`,
`protocol-mismatch`, `malformed`.

## Vinculación PC ↔ PC en la red local (sin reglas de firewall en el emisor)

1. El emisor difunde cada 1,5 s por UDP al puerto `47802` (solo salida):
   `{"magic":"DSBEACON1","hostId","name","fp","port","version","proto","s"}` (`port` = 0 si no acepta conexiones entrantes; `s` = señalización).
2. El receptor (escucha UDP `47802` solo mientras se usa *Usar como monitor*) abre un puerto TCP aleatorio y responde por unicast al
   origen del anuncio: `{"magic":"DSDIAL1","hostId","addrs":[…],"port"}`. Windows permite por defecto la respuesta unicast a una difusión.
   En paralelo envía la misma petición (`kind:"dial"`) por el broker MQTT.
3. El emisor (como mucho una vez por segundo, solo direcciones privadas) se conecta al receptor y actúa como servidor TLS; el receptor
   comprueba la huella fijada y que `hostId` = SHA‑256(SPKI) del certificado. Luego `HELLO`/`WELCOME` como cualquier cliente.

## Negociación

1. El PC cruza `caps` con sus codificadores por hardware (Media Foundation) → resoluciones y frecuencias
   ofrecibles. Solo se ofrecen frecuencias que el panel realmente ejecuta (más 30/60).
2. Aplica la configuración del usuario (o "auto" = resolución nativa y máxima frecuencia).
3. Crea el monitor virtual con esa lista de modos, envía `STREAM_CONFIG` y empieza a transmitir.
4. Durante la sesión, el control de congestión puede bajar bitrate y FPS (ver `BitrateController`).

## Señalización remota (WebSocket JSON)

```
servidor → cliente  {"t":"challenge","nonce":"…"}
PC       → servidor {"t":"hello-host","hostId":"…","cert":"base64 DER","sig":"base64 P1363(SHA256('DSHOST1|hostId|nonce'))"}
servidor → PC       {"t":"registered","iceServers":[{urls,username,credential}]}
teléfono → servidor {"t":"join","hostId":"…"}
servidor → teléfono {"t":"joined","hostId":"…","iceServers":[…]}
servidor → PC       {"t":"peer","peerId":"…"}
PC       → teléfono {"t":"signal","to":peerId,"data":{"kind":"offer","sdp":…,"cert":…,"sig":"P1363(SHA256('DSOFFER1|'+sdp))"}}
teléfono → PC       {"t":"signal","data":{"kind":"answer","sdp":…}}
ambos               {"t":"signal",…,"data":{"kind":"ice","candidate":…,"sdpMid":…,"sdpMLineIndex":…}}
```

### Señalización sin servidor propio (predeterminada): broker MQTT público

URL `mqtt+wss://broker.emqx.io:8084/mqtt` (MQTT 3.1.1 sobre WebSocket, QoS 0). Temas:

| Tema | Quién escucha | Mensajes |
|---|---|---|
| `dualscreen/v1/h/<hostId>` | PC | `{"t":"join","peerId"}`, `{"t":"signal","from":peerId,"data":…}` |
| `dualscreen/v1/p/<peerId>` | teléfono (`peerId` aleatorio de 96 bits) | `{"t":"signal","data":…}` |

El contenido de `data` es idéntico al del servidor propio. Cualquiera puede leer esos temas: por eso la
oferta va firmada y la autenticación (token o código) ocurre dentro del canal DTLS.

Credenciales TURN efímeras: esquema REST de coturn (`usuario = expiración:peerId`, `contraseña = base64(HMAC-SHA1(secreto, usuario))`).
