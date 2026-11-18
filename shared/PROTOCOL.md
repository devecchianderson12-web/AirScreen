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

### HELLO

```json
{
  "proto": 1, "deviceId": "uuid", "deviceName": "Pixel 8", "model": "Google Pixel 8", "appVersion": "0.1.0",
  "auth": {"mode": "token", "token": "base64"}            // dispositivo emparejado
       // o {"mode":"pair","nonce":"hex≥16","proof":"hex"}  // primer emparejamiento
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
PC       → servidor {"t":"set-pair-key","key":"hex SHA256('DSRDV1|'+código)"}
teléfono → servidor {"t":"join","hostId":"…"}  o  {"t":"join","pairKey":"…"}
servidor → teléfono {"t":"joined","hostId":"…","iceServers":[…]}
servidor → PC       {"t":"peer","peerId":"…"}
PC       → teléfono {"t":"signal","to":peerId,"data":{"kind":"offer","sdp":…,"cert":…,"sig":"P1363(SHA256('DSOFFER1|'+sdp))"}}
teléfono → PC       {"t":"signal","data":{"kind":"answer","sdp":…}}
ambos               {"t":"signal",…,"data":{"kind":"ice","candidate":…,"sdpMid":…,"sdpMLineIndex":…}}
```

Credenciales TURN efímeras: esquema REST de coturn (`usuario = expiración:peerId`, `contraseña = base64(HMAC-SHA1(secreto, usuario))`).
