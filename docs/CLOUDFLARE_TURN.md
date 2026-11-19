# Relé TURN gratuito con Cloudflare (conexión desde otra red)

Cuando el teléfono está en datos móviles, la conexión directa suele ser imposible (CGNAT de la operadora).
DualScreen usa entonces el relé TURN de Cloudflare Realtime (1000 GB/mes gratis). La clave de Cloudflare vive
en un *Worker* tuyo; DualScreen.exe solo pide credenciales temporales (24 h) a `https://…workers.dev/ice`.

## Pasos (una sola vez, ~10 minutos)

1. Crea una cuenta gratuita en https://dash.cloudflare.com/sign-up
2. **Clave TURN**: en el panel, menú **Realtime** (puede aparecer como *Calls*) → **TURN Server** → **Create**.
   Nombre: `DualScreen`. Copia los dos valores que muestra: **Turn Token ID** y **API Token**
   (el API Token solo se muestra una vez).
3. **Worker**: menú **Workers & Pages** → **Create** → **Create Worker** → nombre `dualscreen-turn` → **Deploy**.
4. Pulsa **Edit code**, borra todo, pega el contenido de
   [`signaling-server/cloudflare-turn-worker/worker.js`](../signaling-server/cloudflare-turn-worker/worker.js)
   y pulsa **Deploy**.
5. En el Worker: **Settings** → **Variables and Secrets** → **Add** dos variables de tipo **Secret**:
   - `TURN_KEY_ID` = el *Turn Token ID* del paso 2
   - `TURN_KEY_API_TOKEN` = el *API Token* del paso 2

   y pulsa **Deploy**.
6. Abre en el navegador `https://dualscreen-turn.<tu-subdominio>.workers.dev/ice`. Debe mostrar un texto que
   empieza por `{"iceServers":[`. Esa dirección es la que se pone en `iceServersUrl` de `dualscreen.repo.json`.

Para desarrolladores: también se puede desplegar con `npx wrangler deploy` desde esa carpeta y
`npx wrangler secret put TURN_KEY_ID` / `TURN_KEY_API_TOKEN`.
