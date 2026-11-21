# Changelog

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); versiones [SemVer](https://semver.org/lang/es/).

## [0.2.1] - 2026-11-21

### Corregido
- «Instalar controlador» fallaba con «certificado de raíz no compatible con el proveedor de confianza». El instalador ahora agrega el certificado propio del controlador a los certificados de confianza del equipo (raíz y editores de confianza) antes de instalarlo. Como es un controlador de modo usuario (UMDF) no hace falta el modo de prueba y Secure Boot sigue activo. Al desinstalar se quita el certificado.

## [0.2.0] - 2026-11-21

### Añadido
- **Usar como monitor** (nueva sección en Windows): una PC o notebook con DualScreen puede ser el segundo monitor de otra PC. DualScreen es a la vez emisor y receptor.
- Vinculación con el código de 6 dígitos de la otra PC; queda guardada y después se inicia con **un clic** (también desde el menú del icono de notificaciones).
- Pantalla completa real, sin barras ni menús: decodificación por hardware (DXVA, H.264/HEVC) directa a la GPU, presentación inmediata con *flip* y *tearing* permitido, sin búfer intermedio.
- La pantalla táctil (multitáctil), el lápiz (presión, botón, borrador), el mouse y el teclado del receptor controlan la PC emisora.
- Salida solo por teclado: Esc / Alt+F4 cierran; tecla Windows, Alt+Tab, Ctrl+Esc o Ctrl+Shift+Esc muestran la barra de tareas y minimizan sin cortar la conexión. Los gestos táctiles desde los bordes están desactivados.
- Modo **No molestar**: desactiva todo el teclado; se sale manteniendo presionada 3 s la esquina superior izquierda, con Ctrl+Alt+Supr o desde la otra PC.
- La PC emisora se anuncia en la red local (solo envía, sin aviso del firewall) y se conecta al receptor; también funciona con la conexión directa o pidiendo la conexión por el broker MQTT.
- Configuración: «Anunciar este PC en la red local» y «Permitir control desde el monitor vinculado».

## [0.1.8] - 2026-11-19

### Corregido
- La actualización del PC fallaba y dejaba la app cerrada: el proceso anterior tardaba en terminar y el actualizador intentaba cerrar también su propio proceso. Ahora la app siempre termina en pocos segundos y el actualizador solo cierra la versión anterior.

## [0.1.7] - 2026-11-19

### Añadido
- Registro detallado de WebRTC (STUN/TURN/ICE) en el archivo de registro del PC para diagnosticar la conexión remota.

## [0.1.6] - 2026-11-19

### Añadido
- App Android: botón «Buscar actualizaciones» que descarga e instala la nueva versión desde la propia app.

### Cambiado
- La APK se firma siempre con la misma clave: desde esta versión las actualizaciones se instalan encima sin desinstalar.

## [0.1.5] - 2026-11-19

### Corregido
- El teléfono ya no repite los pasos en bucle: si el primer intento falla, muestra la causa.
- Candidatos ICE que llegaban antes que la oferta/respuesta se perdían; ahora se guardan y se aplican después.
- Más detalle de la conexión remota en el registro del PC.

## [0.1.4] - 2026-11-19

### Añadido
- Relé TURN de Cloudflare: la conexión desde otra red (datos móviles, CGNAT) funciona aunque la conexión directa sea imposible.

## [0.1.3] - 2026-11-18

### Añadido
- El teléfono muestra en qué paso de la conexión está (red local, conexión inversa, Internet).

## [0.1.2] - 2026-11-18

### Corregido
- El QR no transmitía la dirección de la conexión remota al teléfono (el teléfono descartaba las URL `mqtt+wss://`).

### Cambiado
- Conexión inversa: el PC se conecta al teléfono, así el firewall de Windows nunca bloquea la conexión y no hace falta ninguna regla ni permiso de administrador.
- DualScreen ya no pide permisos de administrador por sí solo: se quitó el botón de firewall, la escucha de conexiones entrantes viene desactivada (evita el aviso del firewall) y el actualizador ya no se eleva.

## [0.1.1] - 2026-11-18

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
