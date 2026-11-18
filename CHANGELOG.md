# Changelog

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); versiones [SemVer](https://semver.org/lang/es/).

## [Sin publicar]

### Corregido
- Si el controlador no está instalado, el modo extendido pasa automáticamente a modo espejo en lugar de fallar.
- Mensajes de error del teléfono con la causa concreta por cada dirección probada.
- El mensaje de error ya no aparece duplicado en la pantalla del teléfono.

### Añadido
- Conexión remota sin configurar nada: activada por defecto mediante un broker MQTT público para la señalización (WebRTC P2P directo).
- Botón y aviso para permitir DualScreen en el firewall de Windows (conexiones Wi‑Fi locales).

### Cambiado
- Las releases publican solo `DualScreen.exe` y `DualScreen.apk`; el hash y la firma del exe viajan en un comentario invisible de las notas.
- Las copias 0.1.0 no pueden autoactualizarse a este formato: descarga la siguiente versión manualmente una vez.

## [0.1.0] - 2026-11-18

### Añadido
- Aplicación de Windows portable (`DualScreen.exe`) con icono en la bandeja, panel estilo Windows 11 (Mica, modo oscuro) y menú contextual.
- Controlador de pantalla indirecta (IddCx) que crea un monitor real para el modo extendido.
- Modo espejo sin controlador.
- Captura con Windows.Graphics.Capture, conversión NV12 en GPU y codificación H.264/HEVC/AV1 por hardware (Media Foundation: NVENC, Quick Sync, AMF) con respaldo por software.
- Negociación de resoluciones, frecuencias (60–240 Hz) y códecs según las capacidades reales del teléfono.
- Bitrate adaptativo basado en retardo y reducción automática de FPS.
- Conexión LAN (TLS 1.2/1.3 con huella fijada) con descubrimiento UDP y emparejamiento por QR o código.
- Conexión remota por WebRTC (P2P + TURN) con servidor de señalización propio y ofertas firmadas.
- App Android (receptor a pantalla completa con MediaCodec en modo de baja latencia, servicio en primer plano, escáner QR).
- Actualizaciones automáticas desde GitHub Releases verificadas con SHA-256 y firma ECDSA, con actualizador independiente y reversión.
- Flujos de GitHub Actions para CI y publicación.
