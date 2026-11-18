# Guía de uso

## Windows
- `DualScreen.exe` no se instala: ejecútalo desde cualquier carpeta. Arranca en segundo plano con un icono en
  el área de notificaciones.
  - **Clic izquierdo**: abre el panel.
  - **Clic derecho**: Abrir, Conectar dispositivo, Desconectar dispositivo, Buscar actualizaciones, Configuración, Salir.
- Cerrar la ventana no cierra DualScreen; usa *Salir* en el menú.
- Modo portátil total: crea un archivo vacío `portable.txt` junto al exe y los datos se guardarán en `DualScreenData\`.
- DualScreen no pide permisos de administrador. La única excepción, opcional y solo si tú la pulsas, es instalar
  el controlador del modo extendido (Windows exige administrador para cualquier controlador).
- Conexión: el teléfono escanea el QR; el PC se conecta al teléfono por la red local (sin reglas de firewall) y,
  si están en redes distintas, por WebRTC.

### Panel
- **Inicio**: estado, teléfono conectado, resolución, frecuencia, calidad, latencia estimada, velocidad y códec;
  botón grande Conectar/Desconectar.
- **Pantalla**: modo extendido/espejo, resolución, Hz, orientación, calidad, prioridad, códec y bitrate máximo.
  Con un teléfono conectado solo aparecen las opciones que el teléfono soporta; los cambios se aplican en vivo.
- **Conexión**: QR y código de emparejamiento (5 min, un solo uso), botón para generar otro, direcciones,
  estado remoto, huella del PC y dispositivos emparejados (con **Revocar**).
- **Actualizaciones**: versión actual y disponible, *Actualizar ahora*, *Posponer*.
- **Configuración**: inicio con Windows, notificaciones, red local/remota, controlador, registros.

## Android
1. Instala `DualScreen.apk` (permite "instalar apps desconocidas" para tu navegador/gestor de archivos).
2. Abre DualScreen → **Escanear QR** (o *Introducir código* si el PC aparece en "PCs en esta red").
3. La pantalla pasa a modo completo, sin barras ni controles encima. **Doble toque**: estadísticas durante 4 s.
   **Atrás**: desconectar o minimizar.
4. Los PCs emparejados se reconectan solos (también si cambia la IP o hay un corte breve).

## Consejos de rendimiento
- Wi‑Fi de 5 GHz o 6 GHz, PC por cable al router si es posible.
- 120 Hz requiere un teléfono con panel de 120 Hz y buena señal; si la red no lo sostiene, DualScreen baja
  automáticamente a 60 Hz y lo indica en *Inicio*.
- HEVC reduce el bitrate necesario en resoluciones altas; H.264 suele dar la menor latencia.
