// DualScreen TURN credentials endpoint (Cloudflare Worker).
//
// GET /ice  ->  {"iceServers":[{urls, username?, credential?}, ...]}
//
// Holds the Cloudflare Realtime TURN key as Worker secrets (never shipped inside DualScreen.exe) and hands out
// credentials valid for 24 hours. Secrets to set in the Worker settings:
//   TURN_KEY_ID         - the "Turn Token ID" of your TURN key
//   TURN_KEY_API_TOKEN  - the "API Token" of that TURN key
const TTL_SECONDS = 86400;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method !== "GET" || url.pathname !== "/ice") {
      return new Response("DualScreen TURN endpoint. Use GET /ice", { status: 404 });
    }
    if (!env.TURN_KEY_ID || !env.TURN_KEY_API_TOKEN) {
      return json({ error: "TURN_KEY_ID / TURN_KEY_API_TOKEN secrets are not configured" }, 500);
    }
    const base = `https://rtc.live.cloudflare.com/v1/turn/keys/${env.TURN_KEY_ID}/credentials`;
    const init = {
      method: "POST",
      headers: { Authorization: `Bearer ${env.TURN_KEY_API_TOKEN}`, "Content-Type": "application/json" },
      body: JSON.stringify({ ttl: TTL_SECONDS }),
    };
    // Newer API returns a ready-made iceServers array; older one returns a single object.
    let res = await fetch(`${base}/generate-ice-servers`, init);
    if (res.status === 404) res = await fetch(`${base}/generate`, init);
    if (!res.ok) return json({ error: `Cloudflare TURN API returned ${res.status}` }, 502);
    const data = await res.json();
    const iceServers = Array.isArray(data.iceServers) ? data.iceServers : [data.iceServers];
    return json({ iceServers, ttl: TTL_SECONDS });
  },
};

function json(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store" },
  });
}
