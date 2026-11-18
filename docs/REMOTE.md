# Conexión remota (otra red, datos móviles)

Para conectar el teléfono cuando **no** está en la misma red que el PC, DualScreen usa WebRTC:

1. **Servidor de señalización** (`signaling-server/`, Go): presenta PC y teléfono e intercambia ofertas/ICE.
   No ve el vídeo. Los PC se autentican firmando un reto con su clave de identidad; las ofertas SDP van
   firmadas, así que ni siquiera un servidor comprometido puede interceptar la sesión.
2. **ICE**: primero intenta conexión **directa P2P** (direcciones locales y públicas vía STUN, *hole punching*).
3. **TURN** (coturn): si ambos NAT son estrictos (CGNAT de operadoras móviles es habitual), el tráfico cifrado
   se retransmite por tu servidor TURN. No hace falta abrir puertos en el router de casa.

## Sin configurar nada (predeterminado)

DualScreen trae activada la conexión remota usando un **broker MQTT público y gratuito**
(`mqtt+wss://broker.emqx.io:8084/mqtt`) solo para presentarse; el vídeo va directo entre PC y teléfono.
Empareja con el **código QR** (lleva la URL y la identidad del PC) y el teléfono podrá conectarse aunque esté
en datos móviles u otra Wi‑Fi.

Limitaciones honestas de este modo:
- Sin TURN: si **ambos** extremos están tras NAT estrictos (p. ej. CGNAT simétrico de algunas operadoras),
  la conexión directa no es posible y fallará con "No se pudo establecer la conexión P2P ni por TURN".
  Para esos casos hace falta tu propio TURN (abajo).
- El broker público no garantiza disponibilidad. Puedes cambiar la URL en Configuración → Conexión remota.

## Servidor propio (opcional, más fiable)

Sin servidor propio el modo remoto depende del broker público: no se depende de servicios de pago obligatorios.
Los STUN públicos de Google (`stun.l.google.com`) se usan por defecto y son gratuitos; TURN consume ancho de
banda, por eso debe ser tuyo.

## Despliegue (VPS con Docker, ~5 minutos)

Requisitos: un VPS con IP pública (1 vCPU basta para señalización; el ancho de banda TURN equivale al bitrate
del vídeo, p. ej. 10–30 Mbps por sesión), dos nombres DNS apuntando a él, puertos abiertos:
`80/443 TCP` (señalización), `3478 TCP/UDP` y `49160-49200 UDP` (TURN).

```bash
git clone https://github.com/devecchianderson12-web/AirScreen.git
cd AirScreen/signaling-server
cp .env.example .env
openssl rand -hex 32            # pega el valor en TURN_SECRET dentro de .env
nano .env                       # SIGNALING_DOMAIN y TURN_DOMAIN
docker compose up -d
curl https://<SIGNALING_DOMAIN>/healthz
```

Caddy obtiene el certificado HTTPS automáticamente. La URL a configurar es `wss://<SIGNALING_DOMAIN>/ws`.

Sin Docker:
```bash
cd signaling-server && go build -o dualscreen-signaling .
DS_TURN_URLS="turn:turn.example.com:3478?transport=udp" DS_TURN_SECRET=... \
  ./dualscreen-signaling -addr :8443 -cert fullchain.pem -key privkey.pem
```

## Configuración en DualScreen

- Windows → Configuración → *Conexión remota*: activa el interruptor y pega `wss://…/ws`.
  (Opcional: servidor TURN propio con usuario/contraseña estáticos; la contraseña se guarda cifrada con DPAPI.)
- Para que todas las copias lo usen por defecto, pon la URL en `defaultSignalingUrl` de `dualscreen.repo.json`
  antes de compilar.
- Empareja el teléfono escaneando el QR **mientras el modo remoto está activo**: el QR incluye la URL de
  señalización y el teléfono la recordará. Desde entonces, si no encuentra el PC en la red local, se conecta
  por la ruta remota automáticamente.

## Limitaciones conocidas
- El canal de datos de SIPSorcery (implementación WebRTC en C# del lado del PC) tiene un rendimiento SCTP
  menor que libwebrtc; en remoto conviene calidad *Equilibrada* y 60 Hz. El control adaptativo baja bitrate y
  FPS automáticamente si el enlace no da más.
- La latencia remota depende del trayecto de red (y de si se usa TURN); no puede igualar a la LAN.
