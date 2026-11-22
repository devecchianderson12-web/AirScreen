package com.dualscreen.app

import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Surface
import com.dualscreen.app.audio.AudioPlayer
import com.dualscreen.app.data.PairedHost
import com.dualscreen.app.data.Store
import com.dualscreen.app.net.Beacons
import com.dualscreen.app.net.DiscoveredHost
import com.dualscreen.app.net.Discovery
import com.dualscreen.app.net.FrameIO
import com.dualscreen.app.net.LanTransport
import com.dualscreen.app.net.PairInfo
import com.dualscreen.app.net.Protocol
import com.dualscreen.app.net.RemoteTransport
import com.dualscreen.app.net.SeenHost
import com.dualscreen.app.net.Transport
import com.dualscreen.app.video.Capabilities
import com.dualscreen.app.video.VideoDecoder
import org.json.JSONObject
import java.io.IOException
import java.nio.ByteBuffer
import java.security.SecureRandom
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors

sealed class Target {
    data class Paired(val host: PairedHost) : Target()
    data class Qr(val info: PairInfo) : Target()
    data class Code(val host: DiscoveredHost, val code: String) : Target()
    /** One tap on a PC found on the network: no code (if the PC allows it), saved afterwards. */
    data class Lan(val host: SeenHost) : Target()
}

enum class LinkState { DISCONNECTED, CONNECTING, CONNECTED, ERROR }

data class StreamConfig(val mime: String, val width: Int, val height: Int, val fps: Int, val mode: String)

/** Connection state machine shared by the service and the activities. One phone ↔ one PC at a time. */
object StreamHub {
    private const val TAG = "DualScreen.Hub"
    private val main = Handler(Looper.getMainLooper())
    private val listeners = CopyOnWriteArrayList<() -> Unit>()

    @Volatile var state = LinkState.DISCONNECTED; private set
    @Volatile var message = ""; private set
    @Volatile var hostName = ""; private set
    @Volatile var transportDesc = ""; private set
    @Volatile var config: StreamConfig? = null; private set
    @Volatile var rttMs = Double.NaN; private set
    @Volatile var recvKbps = 0f; private set
    val decodeMs get() = decoder?.decodeMs ?: 0f
    val fps get() = decoder?.fpsNow ?: 0f
    /** Real capture-to-display latency (ms) once the PC clock offset is known, else NaN. */
    @Volatile var latencyMs = Double.NaN; private set
    /** Settings and options of the PC (HOST_STATE), for the in-stream settings panel. */
    @Volatile var hostState: JSONObject? = null; private set
    /** PC pointer position (0..1 over the video), from CURSOR position frames; used by the touchpad mode. */
    @Volatile var cursorX = 0.5f
    @Volatile var cursorY = 0.5f
    /** Last local touchpad move: the PC's (older) echo is ignored for a moment so the pointer never jumps back. */
    @Volatile var localCursorAtMs = 0L

    private val sendExec = Executors.newSingleThreadExecutor { r -> Thread(r, "DualScreen.Send").apply { isDaemon = true } }
    private val audio = AudioPlayer()
    @Volatile private var clockOffsetUs = 0L
    @Volatile private var haveOffset = false
    private var bestRttUs = Double.MAX_VALUE

    private var worker: Thread? = null
    @Volatile private var userStopped = false
    @Volatile private var transport: Transport? = null
    @Volatile private var io: FrameIO? = null
    @Volatile private var surface: Surface? = null
    @Volatile private var decoder: VideoDecoder? = null
    private val decoderLock = Any()
    private var bytesWindow = 0L
    private var windowStart = System.nanoTime()

    fun addListener(l: () -> Unit) = listeners.add(l)
    fun removeListener(l: () -> Unit) = listeners.remove(l)
    private fun changed() = main.post { listeners.forEach { it() } }

    private fun set(st: LinkState, msg: String) {
        state = st
        message = msg
        changed()
    }

    val isActive get() = state == LinkState.CONNECTING || state == LinkState.CONNECTED

    fun connect(context: Context, target: Target) {
        disconnect()
        userStopped = false
        val app = context.applicationContext
        val intent = Intent(app, StreamService::class.java)
        if (Build.VERSION.SDK_INT >= 26) app.startForegroundService(intent) else app.startService(intent)
        worker = Thread({ run(app, target) }, "DualScreen.Connection").apply { start() }
    }

    fun disconnect() {
        userStopped = true
        runCatching { io?.writeJson(Protocol.BYE, JSONObject().put("reason", "user")) }
        transport?.close()
        worker?.interrupt()
        worker = null
    }

    fun setQuality(q: String) = send(Protocol.CLIENT_SETTINGS, JSONObject().put("quality", q).toString().toByteArray())

    /** Never blocks the caller (UI thread): frames go out on a dedicated sender thread. */
    fun send(type: Int, payload: ByteArray) {
        val frameIo = io ?: return
        sendExec.execute { runCatching { frameIo.write(type, payload) } }
    }

    fun sendInput(payload: ByteArray) = send(Protocol.INPUT, payload)

    /** Changes a setting on the PC (applied to the PC's own DualScreen settings). */
    fun sendHostSettings(o: JSONObject) = send(Protocol.HOST_SETTINGS, o.toString().toByteArray())

    // ------------------------------------------------------------------ surface / decoder

    fun attachSurface(s: Surface) {
        surface = s
        restartDecoder()
    }

    fun detachSurface() {
        surface = null
        synchronized(decoderLock) {
            decoder?.release()
            decoder = null
        }
    }

    private fun restartDecoder() {
        val cfg = config ?: return
        val s = surface ?: return
        synchronized(decoderLock) {
            decoder?.release()
            decoder = runCatching {
                VideoDecoder(cfg.mime, cfg.width, cfg.height, cfg.fps, s) { requestKeyframe() }.also { it.start() }
            }.onFailure { Log.e(TAG, "decoder start failed", it) }.getOrNull()
        }
        requestKeyframe()
    }

    private fun requestKeyframe() = send(Protocol.KEYFRAME_REQUEST, ByteArray(0))

    // ------------------------------------------------------------------ connection

    private fun run(ctx: Context, initial: Target) {
        Beacons.start(ctx)
        try { runCore(ctx, initial) } finally { Beacons.stop() }
    }

    private fun runCore(ctx: Context, initial: Target) {
        val store = Store(ctx)
        var target = initial
        var attempt = 0
        var everConnected = false
        while (!userStopped) {
            set(LinkState.CONNECTING, if (attempt == 0) "Conectando…" else "Reconectando… (intento ${attempt + 1})")
            try {
                val t = open(ctx, store, target)
                transport = t
                transportDesc = t.description
                everConnected = true
                val paired = session(ctx, store, t, target)
                attempt = 0
                if (paired != null) target = Target.Paired(paired)
            } catch (e: InterruptedException) {
                break
            } catch (e: Exception) {
                Log.w(TAG, "connection failed", e)
                // Only retry silently after a drop of a working session; a first attempt that fails shows
                // its cause right away instead of looping.
                if (e is FatalError || target !is Target.Paired || userStopped || !everConnected) {
                    set(LinkState.ERROR, e.message ?: "Error de conexión")
                    break
                }
                message = e.message ?: "Error de conexión"
                set(LinkState.CONNECTING, "Reintentando… Último error: $message")
            } finally {
                transport?.close()
                transport = null
                io = null
                config = null
                hostState = null
                haveOffset = false; bestRttUs = Double.MAX_VALUE; latencyMs = Double.NaN
                audio.release()
                synchronized(decoderLock) { decoder?.release(); decoder = null }
            }
            if (userStopped) break
            // Automatic reconnection for a paired PC (Wi-Fi hiccup, PC restart, network change).
            attempt++
            if (attempt > 10) { set(LinkState.ERROR, "No se pudo reconectar con $hostName"); break }
            try { Thread.sleep(minOf(1000L * attempt, 5000L)) } catch (_: InterruptedException) { break }
        }
        if (userStopped || state != LinkState.ERROR) set(LinkState.DISCONNECTED, "Desconectado")
        ctx.stopService(Intent(ctx, StreamService::class.java))
    }

    class FatalError(msg: String) : IOException(msg)

    private fun localAddresses(): List<String> = runCatching {
        java.net.NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.toList() }
            .filterIsInstance<java.net.Inet4Address>()
            .filter { it.isSiteLocalAddress || it.hostAddress?.startsWith("100.") == true }
            .mapNotNull { it.hostAddress }
    }.getOrDefault(emptyList())

    /** Turns socket errors into hints the user can act on. */
    private fun explain(e: Exception): String = when (e) {
        is java.net.SocketTimeoutException -> "sin respuesta (¿otra red, o el firewall de Windows bloquea DualScreen?)"
        is java.net.ConnectException -> "conexión rechazada (¿DualScreen está abierto en el PC?)"
        is java.net.NoRouteToHostException -> "red inalcanzable (¿el teléfono está en otra red?)"
        is javax.net.ssl.SSLException -> "el certificado del PC no coincide (vuelve a escanear el QR)"
        else -> e.message ?: e.javaClass.simpleName
    }

    private fun open(ctx: Context, store: Store, target: Target): Transport {
        val errors = mutableListOf<String>()
        fun tryLan(addresses: List<String>, port: Int, fp: String?): Transport? {
            set(LinkState.CONNECTING, "Paso 1/3: buscando el PC en la red local…")
            for (a in addresses.distinct()) {
                try { return LanTransport.connect(a, port, fp) } catch (e: Exception) { errors += "$a: ${explain(e)}" }
            }
            return null
        }
        /** PC dials the phone: works even when Windows Firewall blocks incoming connections (no admin needed). */
        fun tryReverse(url: String?, hostId: String, fp: String): Transport? {
            if (url == null || !url.startsWith("mqtt+")) return null
            set(LinkState.CONNECTING, "Paso 2/3: pidiendo al PC que se conecte al teléfono…")
            val addrs = localAddresses()
            if (addrs.isEmpty()) return null
            return try {
                java.net.ServerSocket(0).use { server ->
                    server.soTimeout = 7000
                    if (!RemoteTransport.requestDial(url, hostId, addrs, server.localPort)) {
                        errors += "conexión inversa: sin acceso al servidor de señalización"; return null
                    }
                    LanTransport.acceptReverse(server, fp)
                }
            } catch (e: Exception) { errors += "conexión inversa: ${explain(e)}"; null }
        }
        /** PC on the same network found by its beacon: ask it to connect to us (works with no Internet and no firewall rule). */
        fun tryBeacon(host: SeenHost?, fp: String?): Transport? {
            if (host?.beaconSource == null) return null
            set(LinkState.CONNECTING, "Paso 2/3: pidiendo a ${host.name} que se conecte…")
            val addrs = localAddresses()
            if (addrs.isEmpty()) return null
            return try { LanTransport.viaBeacon(host, addrs, fp) } catch (e: Exception) { errors += "red local: ${explain(e)}"; null }
        }
        fun tryRemote(url: String?, hostId: String, fp: String): Transport? {
            if (url == null) { errors += "conexión remota: no configurada en el PC"; return null }
            set(LinkState.CONNECTING, "Paso 3/3: conectando por Internet (puede tardar hasta 30 s)…")
            return try { RemoteTransport.connect(ctx, url, hostId, fp) } catch (e: Exception) { errors += "remota: ${e.message}"; null }
        }
        fun details() = errors.joinToString("\n• ", prefix = "\n\n• ")
        return when (target) {
            is Target.Paired -> {
                val h = target.host
                hostName = h.name
                // Prefer a fresh discovery answer (the PC's IP may have changed).
                val seen = Beacons.find(h.hostId) ?: run { Thread.sleep(1600); Beacons.find(h.hostId) }
                val found = runCatching { Discovery.scan(ctx, 600) }.getOrDefault(emptyList()).filter { it.hostId == h.hostId }
                val port = found.firstOrNull()?.port ?: seen?.port?.takeIf { it > 0 } ?: h.port
                (if (found.isNotEmpty() || (seen?.port ?: 0) > 0) tryLan(found.map { it.address } + listOfNotNull(seen?.address) + h.addresses, port, h.fingerprint) else null)
                    ?: tryBeacon(seen, h.fingerprint)
                    ?: tryReverse(h.signalingUrl ?: seen?.signalingUrl, h.hostId, h.fingerprint)
                    ?: tryRemote(h.signalingUrl, h.hostId, h.fingerprint)
                    ?: throw IOException("No se encontró ${h.name}." + details())
            }
            is Target.Qr -> {
                val i = target.info
                hostName = i.name
                tryLan(i.addresses, i.port, i.fingerprint)
                    ?: tryReverse(i.signalingUrl, i.hostId, i.fingerprint)
                    ?: tryRemote(i.signalingUrl, i.hostId, i.fingerprint)
                    ?: throw FatalError("No se pudo conectar con ${i.name}." + details())
            }
            is Target.Lan -> {
                val h = Beacons.find(target.host.hostId) ?: target.host
                hostName = h.name
                (if (h.port > 0) tryLan(listOf(h.address), h.port, null) else null)
                    ?: tryBeacon(h, null)
                    ?: throw FatalError("No se pudo conectar con ${h.name}." + details())
            }
            is Target.Code -> {
                hostName = target.host.name
                (if (target.host.port > 0) tryLan(listOf(target.host.address), target.host.port, null) else null)
                    ?: tryBeacon(Beacons.find(target.host.hostId), null)
                    ?: throw FatalError("No se pudo conectar con ${target.host.name}." + details())
            }
        }
    }

    /** Runs one authenticated session. Returns the PairedHost if this session created/used a pairing. */
    private fun session(ctx: Context, store: Store, t: Transport, target: Target): PairedHost? {
        val frameIo = FrameIO(t.input, t.output)
        io = frameIo
        val auth = JSONObject()
        val code = when (target) { is Target.Qr -> target.info.code; is Target.Code -> target.code; else -> null }
        if (target is Target.Lan) {
            auth.put("mode", "lan")
        } else if (target is Target.Paired) {
            val token = store.decrypt(target.host.tokenEnc) ?: throw FatalError("Emparejamiento dañado; vuelve a emparejar")
            auth.put("mode", "token").put("token", token)
        } else {
            val nonce = Protocol.hex(ByteArray(16).also { SecureRandom().nextBytes(it) })
            auth.put("mode", "pair").put("nonce", nonce).put("proof", Protocol.pairingProof(code!!, t.fingerprint, store.deviceId, nonce))
        }
        val hello = JSONObject()
            .put("proto", Protocol.VERSION)
            .put("deviceId", store.deviceId)
            .put("deviceName", store.deviceName)
            .put("model", "${Build.MANUFACTURER} ${Build.MODEL}")
            .put("appVersion", BuildConfigCompat.versionName(ctx))
            .put("auth", auth)
            .put("caps", Capabilities.build(ctx))
            .put("quality", store.quality)
        frameIo.writeJson(Protocol.HELLO, hello)

        val welcome = frameIo.read()
        if (welcome.type != Protocol.WELCOME) throw IOException("respuesta inesperada")
        val w = welcome.json()
        if (!w.optBoolean("ok")) {
            throw FatalError(when (w.optString("error")) {
                "bad-code" -> "Código incorrecto"
                "code-expired" -> "El código caducó. Genera uno nuevo en el PC."
                "too-many-attempts" -> "Demasiados intentos. Genera un código nuevo en el PC."
                "not-paired" -> "Este PC revocó el acceso. Vuelve a emparejar."
                "busy" -> "El PC ya está transmitiendo a otro dispositivo."
                "protocol-mismatch" -> "Versiones incompatibles. Actualiza DualScreen en ambos dispositivos."
                "code-required" -> "Ese PC pide código. Tocá ⋮ junto a su nombre → «Conectar con código»."
                else -> "Conexión rechazada"
            })
        }
        hostName = w.optString("hostName", hostName)
        var paired: PairedHost? = (target as? Target.Paired)?.host
        w.optString("deviceToken").takeIf { it.isNotEmpty() }?.let { token ->
            val (addresses, port, signaling) = when (target) {
                is Target.Qr -> Triple(target.info.addresses, target.info.port, target.info.signalingUrl)
                is Target.Code -> Triple(listOf(target.host.address), target.host.port, null)
                is Target.Lan -> Triple(listOf(target.host.address), target.host.port, target.host.signalingUrl)
                else -> Triple(emptyList(), Protocol.LAN_PORT, null)
            }
            paired = PairedHost(w.getString("hostId"), hostName, t.fingerprint, addresses, port, signaling, store.encrypt(token), System.currentTimeMillis())
            store.saveHost(paired!!)
        }
        paired?.let { store.touch(it.hostId) }
        set(LinkState.CONNECTED, "Conectado a $hostName")
        send(Protocol.HOST_SETTINGS, JSONObject().put("audio", store.audio).toString().toByteArray())

        val pinger = Thread({
            try {
                while (true) {
                    frameIo.writeLong(Protocol.PING, System.nanoTime() / 1000)
                    Thread.sleep(1000)
                    val d = decoder
                    frameIo.writeJson(Protocol.STATS, JSONObject()
                        .put("fps", (d?.fpsNow ?: 0f).toDouble())
                        .put("decodeMs", (d?.decodeMs ?: 0f).toDouble())
                        .put("recvKbps", recvKbps.toDouble())
                        .put("dropped", d?.dropped ?: 0)
                        .put("latencyMs", if (latencyMs.isNaN()) 0.0 else latencyMs))
                    changed()
                }
            } catch (_: Exception) { }
        }, "DualScreen.Ping").apply { isDaemon = true; start() }

        try {
            while (true) {
                val f = frameIo.read()
                when (f.type) {
                    Protocol.VIDEO -> onVideo(f.payload)
                    Protocol.PING -> frameIo.write(Protocol.PONG, f.payload)
                    Protocol.PONG -> {
                        val bb = ByteBuffer.wrap(f.payload)
                        val sent = bb.long
                        val now = System.nanoTime() / 1000
                        val rtt = (now - sent) / 1000.0
                        rttMs = if (rttMs.isNaN()) rtt else rttMs * 0.7 + rtt * 0.3
                        if (f.payload.size >= 16) {
                            // NTP-style clock offset from the lowest-RTT samples: enables real latency measurement.
                            val hostNow = bb.long
                            val rttUs = (now - sent).toDouble()
                            if (!haveOffset || rttUs <= bestRttUs * 1.2) {
                                if (rttUs < bestRttUs) bestRttUs = rttUs
                                clockOffsetUs = hostNow - (sent + (rttUs / 2).toLong())
                                haveOffset = true
                            }
                        }
                    }
                    Protocol.AUDIO -> audio.feed(f.payload)
                    Protocol.CURSOR -> if (f.payload.size >= 6 && f.payload[0].toInt() == 2 && f.payload[1].toInt() != 0
                        && android.os.SystemClock.uptimeMillis() - localCursorAtMs > 400) {
                        val bb = ByteBuffer.wrap(f.payload, 2, 4)
                        cursorX = (bb.short.toInt() and 0xFFFF) / 65535f
                        cursorY = (bb.short.toInt() and 0xFFFF) / 65535f
                    }
                    Protocol.HOST_STATE -> { hostState = runCatching { f.json() }.getOrNull(); changed() }
                    Protocol.STREAM_CONFIG -> {
                        val c = f.json()
                        config = StreamConfig(c.getString("mime"), c.getInt("width"), c.getInt("height"), c.getInt("fps"), c.optString("mode"))
                        restartDecoder()
                        changed()
                    }
                    Protocol.BYE -> {
                        val reason = runCatching { f.json().optString("reason") }.getOrDefault("")
                        Log.i(TAG, "PC closed the session: $reason")
                        if (reason.contains("disconnected from the PC") || reason.contains("closed")) userStopped = true
                        throw IOException("El PC cerró la conexión")
                    }
                }
            }
        } finally {
            pinger.interrupt()
        }
    }

    private fun onVideo(p: ByteArray) {
        if (p.size < 9) return
        val flags = p[0].toInt()
        val pts = ByteBuffer.wrap(p, 1, 8).long
        bytesWindow += p.size
        val now = System.nanoTime()
        if (now - windowStart > 1_000_000_000L) {
            recvKbps = bytesWindow * 8f / 1000f / ((now - windowStart) / 1e9f)
            bytesWindow = 0
            windowStart = now
        }
        decoder?.submit(p.copyOfRange(9, p.size), pts, flags and Protocol.FLAG_KEYFRAME != 0)
        if (haveOffset) {
            val lat = (System.nanoTime() / 1000 + clockOffsetUs - pts) / 1000.0 + decodeMs
            if (lat in 0.0..2000.0) latencyMs = if (latencyMs.isNaN()) lat else latencyMs * 0.9 + lat * 0.1
        }
    }
}

object BuildConfigCompat {
    fun versionName(ctx: Context): String = runCatching {
        ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionName ?: "?"
    }.getOrDefault("?")
}
