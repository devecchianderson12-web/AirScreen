# Changelog

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); versiones [SemVer](https://semver.org/lang/es/).

## [0.3.4] - 2026-11-22

### Añadido (Android)
- **Control: Táctil o Mouse (touchpad)** en el punto de ajustes y en Configuración. En modo mouse la pantalla del teléfono funciona como touchpad: el dedo mueve el puntero por toda la pantalla de la PC desde cualquier lado (con aceleración), tocar = clic, dos dedos = clic derecho, deslizar con dos dedos = desplazar, tocar y mantener = arrastrar.
- **Estirar la pantalla**: la imagen ocupa todo el teléfono sea cual sea la resolución en espejo o extendida.

### Cambiado
- Cerrar DualScreen desde Recientes en el teléfono deja de transmitir.

## [0.3.3] - 2026-11-22

### Cambiado
- **La pantalla del receptor tiene prioridad**: al conectarse o al recibir una transmisión queda por encima de todo y ya no se minimiza ni la tapan notificaciones, otras ventanas o el giro de pantalla. Solo se minimiza cuando vos usás la tecla Windows, Alt+Tab, Ctrl+Esc, Ctrl+Shift+Esc o Ctrl+Alt+Supr (y se cierra con Esc). Al recibir una transmisión con la notebook sin tocar, la ventana pasa adelante igual.
- **Convertibles (Lenovo Flex, Yoga…)**: en modo carpa, atril o tablet se anulan el teclado y el mouse/touchpad y funciona solo el táctil (se sale manteniendo 3 s la esquina superior izquierda). En modo notebook vuelven a funcionar al instante.

## [0.3.2] - 2026-11-22

### Corregido
- Transmitir desde una PC donde el servicio de captura de Windows está deshabilitado (error «0x80070422», típico tras programas de optimización) cortaba la conexión al instante. Ahora DualScreen usa la duplicación de escritorio de DirectX como alternativa automática.
- Si el codificador HEVC/AV1 por hardware no arranca, se usa H.264 en lugar de cortar.
- El táctil no respondía al tocar ventanas WPF (como la propia DualScreen) en la PC controlada: WPF ahora recibe el toque por WM_POINTER.

### Añadido
- Botón **Transmitir** también en «Usar como monitor → Equipos vinculados», cuando ese equipo está vinculado en los dos sentidos.

## [0.3.1] - 2026-11-22

### Añadido
- **Transmitir desde la PC** (Windows → Inicio → «Transmitir a un equipo», y en el menú del icono): la PC le pide a una notebook o PC vinculada que muestre su pantalla, y esa notebook la abre sola, sin tocarla. Solo necesita estar encendida con DualScreen abierto (conviene «Iniciar con Windows»). En el receptor se puede desactivar con «Permitir que las PCs vinculadas transmitan acá».

### Cambiado
- **App Android rediseñada como la de Windows**: mismos colores y estilo (azul noche y violeta, tarjetas y filas de ajustes) y las mismas secciones: Inicio (estado, botón Conectar y estadísticas en vivo), Equipos (las computadoras de la red, un toque para conectar sin código), Pantalla (modo extendida/espejo, monitor, resolución, frecuencia, calidad, prioridad, códec y bitrate, aplicados en la PC) y Configuración (sonido, estadísticas, actualizaciones). El panel de ajustes durante la transmisión usa las mismas filas.

## [0.3.0] - 2026-11-21

### Añadido
- **Android igual que Windows**: la pantalla principal lista las computadoras con DualScreen de la red por su nombre; un toque y se conecta, sin código (queda guardada). Nuevo diseño con tarjetas, botón circular «Iniciar» e interruptores.
- **Táctil y lápiz desde el teléfono**: el teléfono funciona como pantalla táctil de la PC (multitáctil y lápiz con presión).
- **Ajustes durante la transmisión** (teléfono): un punto casi invisible en el borde superior derecho abre el panel para cambiar entre pantalla extendida y espejo, elegir qué monitor ver en espejo, prioridad (latencia mínima / equilibrado / calidad), calidad, resolución, frecuencia y códec. Se aplican en la PC (son sus mismos ajustes de Windows).
- **Monitor a mostrar en modo espejo** cuando la PC tiene varios monitores (también en Windows → Pantalla).
- **Sonido de la PC** en el teléfono o en la notebook receptora (sin compresión, con muy poco retraso).
- **Cursor local** en la notebook receptora: el puntero se dibuja en el receptor con la forma real del de la PC y se mueve sin demora.
- **Latencia real medida** de captura a pantalla (sincronizando relojes), visible en Inicio, en el receptor y en las estadísticas del teléfono.
- **Reconexión automática** del receptor Windows si se corta el Wi‑Fi, y detección de conexiones colgadas en ambos lados.

### Mejorado
- La pantalla virtual se comporta como un monitor real: respeta la rotación, la escala (125 %, 150 %…) y la resolución que elijas en Configuración de Windows, ofrece la lista completa de resoluciones y la transmisión se adapta sola al cambiarlas.
- Menos latencia: búfer de envío TCP chico (sin colas ocultas), Wi‑Fi en modo streaming durante la transmisión (sin pausas de ahorro de energía ni escaneos), hilos críticos con prioridad multimedia, temporizador de 1 ms y prioridad alta en la GPU.
- Más nitidez: cuando la imagen queda quieta se refina automáticamente con el ancho de banda libre (texto más nítido sin sumar latencia).
- Cambiar calidad, prioridad o códec ya no desconecta y reconecta el monitor virtual.

## [0.2.9] - 2026-11-21

### Corregido
- Pantalla extendida (sigue «IddCxMonitorArrival devolvió 0xC000000D»): el EDID del monitor virtual no tenía la temporización detallada del modo preferido ni los límites de frecuencia, que Windows exige. Ahora es un EDID 1.4 completo (temporización CVT con borrado reducido y límites que cubren todos los modos). Si aun así Windows lo rechazara, el controlador crea el monitor sin EDID.
- Al actualizar el controlador se reinicia el dispositivo para cargar la versión nueva sin reiniciar Windows.

## [0.2.8] - 2026-11-21

### Corregido
- La pantalla táctil del receptor no hacía nada en la PC emisora: Windows rechazaba los toques inyectados («InjectTouchInput failed (87)») porque se marcaban como contacto principal. Corregido y verificado. Si algún equipo igual rechazara los toques, DualScreen pasa automáticamente a manejar el toque como mouse (tocar = clic, arrastrar = arrastrar).

## [0.2.7] - 2026-11-21

### Corregido
- Pantalla extendida: Windows rechazaba el monitor virtual («IddCxMonitorArrival devolvió 0xC000000D»). Los modos del monitor se describían con un divisor de sincronía vertical inválido (1 en lugar de 0). Corregido; actualizá el controlador.

## [0.2.6] - 2026-11-21

### Corregido
- Pantalla extendida: si Windows rechaza la lista de modos del monitor virtual («Win32 error 87»), DualScreen reintenta con un solo modo a 60 Hz antes de pasar a modo espejo.

### Añadido
- El controlador informa en qué paso falla la creación del monitor (y qué consultas hizo Windows); queda en el registro de DualScreen para diagnosticar.

## [0.2.5] - 2026-11-21

### Corregido
- La pantalla extendida no se usaba después de actualizar: seguía instalado el controlador anterior (el que fallaba) y Windows lo había detenido, así que DualScreen pasaba a modo espejo sin decir por qué. Ahora DualScreen detecta si el controlador instalado es más viejo que el incluido o está detenido, avisa al iniciar y muestra **Actualizar controlador** (Pantalla y Configuración). Al actualizar se reinicia el dispositivo y se borran los paquetes viejos del almacén de controladores.
- La desinstalación solo borra paquetes de DualScreen (antes podía tomar el nombre de un paquete vecino).

## [0.2.4] - 2026-11-21

### Corregido
- El controlador de pantalla virtual se cerraba al crear el monitor («Driver IOCTL 0x222004 failed (Win32 error 1291)») y la conexión se cortaba, con o sin código. Causa: el controlador volvía a bloquear su propio cerrojo cuando Windows le consultaba los modos durante la creación del monitor. Corregido.
- Si la pantalla extendida falla por cualquier motivo, la conexión ya no se corta: se pasa a modo espejo y se avisa.

## [0.2.3] - 2026-11-21

### Corregido
- Al conectarse un monitor (por ejemplo con «Iniciar» desde la notebook) la PC mostraba «Access to the path '\?
oot#display#…' is denied» y cortaba: el controlador solo dejaba usarlo a administradores. Ahora se instala con permisos para el usuario, y si falta ese permiso la transmisión sigue en modo espejo en lugar de fallar.
- Si el controlador ya estaba instalado, Configuración → Controlador muestra **Reparar** (un aviso de administrador) para corregir los permisos.

## [0.2.2] - 2026-11-21

### Añadido
- **Conectar sin código en la red local**: en *Usar como monitor* aparecen todos los equipos con DualScreen de la red con su nombre y, debajo, su IP; un clic (o «Conectar») y listo. Queda guardado para la próxima. Se puede desactivar en la PC emisora (Configuración → Red local); entonces se usa «Vincular con código».
- Opción **Iniciar minimizado** (junto a «Iniciar con Windows»): si se desactiva, la ventana se abre al arrancar.

### Cambiado
- Nuevo tema: azul noche con acento violeta, tarjetas más redondeadas, botones con sombra suave y estados en turquesa/coral.

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
