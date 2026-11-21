package com.dualscreen.app.net

import android.content.Context
import android.net.wifi.WifiManager
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList

/** A DualScreen PC seen on the local network (beacon or discovery answer). */
data class SeenHost(
    val hostId: String,
    val name: String,
    val address: String,
    val port: Int,
    val fingerprint: String,
    val signalingUrl: String?,
    val beaconSource: InetSocketAddress?,
    val lastSeen: Long,
)

/**
 * Finds DualScreen PCs without any setup: PCs broadcast a small beacon on UDP 47802 every 1.5 s (outbound only on
 * the PC, so no firewall prompt there); this listens for them and also probes PCs that accept incoming
 * connections. A PC is asked to connect to the phone by answering its beacon ("DSDIAL1").
 */
object Beacons {
    private const val TAG = "DualScreen.Beacons"
    private val hosts = ConcurrentHashMap<String, SeenHost>()
    private val listeners = CopyOnWriteArrayList<(SeenHost) -> Unit>()
    @Volatile private var socket: DatagramSocket? = null
    @Volatile private var thread: Thread? = null
    private var lock: WifiManager.MulticastLock? = null
    private var users = 0

    fun addListener(l: (SeenHost) -> Unit) = listeners.add(l)
    fun removeListener(l: (SeenHost) -> Unit) = listeners.remove(l)

    /** Hosts seen in the last 10 s. */
    fun current(): List<SeenHost> = hosts.values.filter { System.currentTimeMillis() - it.lastSeen < 10_000 }.sortedBy { it.name }

    fun find(hostId: String): SeenHost? = hosts[hostId]?.takeIf { System.currentTimeMillis() - it.lastSeen < 10_000 }

    @Synchronized
    fun start(context: Context) {
        users++
        if (thread != null) return
        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        lock = wifi.createMulticastLock("dualscreen-beacons").apply { setReferenceCounted(false); acquire() }
        val t = Thread({ loop(wifi) }, "DualScreen.Beacons").apply { isDaemon = true }
        thread = t
        t.start()
    }

    @Synchronized
    fun stop() {
        if (--users > 0) return
        users = 0
        thread?.interrupt()
        thread = null
        runCatching { socket?.close() }
        socket = null
        runCatching { lock?.release() }
        lock = null
    }

    private fun loop(wifi: WifiManager) {
        try {
            val s = DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                soTimeout = 500
                bind(InetSocketAddress(Protocol.BEACON_PORT))
            }
            socket = s
            val buf = ByteArray(4096)
            var nextProbe = 0L
            val probe = "DSDISCOVER1".toByteArray()
            while (!Thread.currentThread().isInterrupted) {
                if (System.currentTimeMillis() >= nextProbe) {
                    nextProbe = System.currentTimeMillis() + 3000
                    for (t in broadcastTargets(wifi)) runCatching { s.send(DatagramPacket(probe, probe.size, t, Protocol.DISCOVERY_PORT)) }
                }
                val p = DatagramPacket(buf, buf.size)
                try { s.receive(p) } catch (_: SocketTimeoutException) { continue }
                runCatching { handle(p) }
            }
        } catch (e: Exception) {
            if (!Thread.currentThread().isInterrupted) Log.w(TAG, "beacon listener stopped", e)
        }
    }

    private fun handle(p: DatagramPacket) {
        if (p.length == 0 || p.data[0] != '{'.code.toByte()) return
        val o = JSONObject(String(p.data, 0, p.length))
        val magic = o.optString("magic")
        if (magic != "DSBEACON1" && magic != "DSHOST1") return
        if (o.optInt("proto", Protocol.VERSION) != Protocol.VERSION) return
        val id = o.optString("hostId")
        if (id.length != 32) return
        val prev = hosts[id]
        val beacon = magic == "DSBEACON1"
        val h = SeenHost(
            hostId = id,
            name = o.optString("name", "PC").take(64),
            address = p.address.hostAddress ?: return,
            port = o.optInt("port", prev?.port ?: 0),
            fingerprint = o.optString("fp"),
            signalingUrl = if (beacon) o.optString("s").ifEmpty { null } else prev?.signalingUrl,
            beaconSource = if (beacon) InetSocketAddress(p.address, p.port) else prev?.beaconSource,
            lastSeen = System.currentTimeMillis(),
        )
        hosts[id] = h
        listeners.forEach { runCatching { it(h) } }
    }

    /** Asks [host] (through its beacon socket) to open a TLS connection to us on [port]. */
    fun sendDial(host: SeenHost, addresses: List<String>, port: Int) {
        val s = socket ?: return
        val src = host.beaconSource ?: return
        val msg = JSONObject().put("magic", "DSDIAL1").put("hostId", host.hostId)
            .put("addrs", JSONArray(addresses)).put("port", port).toString().toByteArray()
        runCatching { s.send(DatagramPacket(msg, msg.size, src)) }
    }

    @Suppress("DEPRECATION")
    private fun broadcastTargets(wifi: WifiManager): List<InetAddress> {
        val list = mutableListOf(InetAddress.getByName("255.255.255.255"))
        runCatching {
            val dhcp = wifi.dhcpInfo
            if (dhcp != null && dhcp.ipAddress != 0) {
                val b = (dhcp.ipAddress and dhcp.netmask) or dhcp.netmask.inv()
                list += InetAddress.getByAddress(ByteArray(4) { i -> (b shr (i * 8) and 0xFF).toByte() })
            }
        }
        return list
    }
}
