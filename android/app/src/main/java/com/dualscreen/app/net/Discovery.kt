package com.dualscreen.app.net

import android.content.Context
import android.net.wifi.WifiManager
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

data class DiscoveredHost(
    val hostId: String,
    val name: String,
    val address: String,
    val port: Int,
    val fingerprint: String,
    val version: String,
)

/** Broadcasts "DSDISCOVER1" on UDP 47801 and collects the PCs that answer. */
object Discovery {
    fun scan(context: Context, timeoutMs: Int = 1500): List<DiscoveredHost> {
        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val lock = wifi.createMulticastLock("dualscreen-discovery").apply { setReferenceCounted(false); acquire() }
        val found = LinkedHashMap<String, DiscoveredHost>()
        try {
            DatagramSocket().use { s ->
                s.broadcast = true
                s.soTimeout = 300
                val probe = "DSDISCOVER1".toByteArray()
                val targets = listOf(InetAddress.getByName("255.255.255.255")) + subnetBroadcast(wifi)
                val deadline = System.currentTimeMillis() + timeoutMs
                var nextProbe = 0L
                val buf = ByteArray(2048)
                while (System.currentTimeMillis() < deadline) {
                    if (System.currentTimeMillis() >= nextProbe) {
                        for (t in targets) runCatching { s.send(DatagramPacket(probe, probe.size, t, Protocol.DISCOVERY_PORT)) }
                        nextProbe = System.currentTimeMillis() + 500
                    }
                    val p = DatagramPacket(buf, buf.size)
                    try {
                        s.receive(p)
                        val o = JSONObject(String(p.data, 0, p.length))
                        if (o.optString("magic") != "DSHOST1" || o.optInt("proto") != Protocol.VERSION) continue
                        val h = DiscoveredHost(
                            hostId = o.getString("hostId"), name = o.optString("name", "PC"),
                            address = p.address.hostAddress ?: continue, port = o.optInt("port", Protocol.LAN_PORT),
                            fingerprint = o.getString("fp"), version = o.optString("version"),
                        )
                        found[h.hostId] = h
                    } catch (_: SocketTimeoutException) {
                    } catch (_: Exception) {
                    }
                }
            }
        } finally {
            lock.release()
        }
        return found.values.toList()
    }

    @Suppress("DEPRECATION")
    private fun subnetBroadcast(wifi: WifiManager): List<InetAddress> = runCatching {
        val dhcp = wifi.dhcpInfo ?: return emptyList()
        if (dhcp.ipAddress == 0) return emptyList()
        val bcast = (dhcp.ipAddress and dhcp.netmask) or dhcp.netmask.inv()
        val bytes = ByteArray(4) { i -> (bcast shr (i * 8) and 0xFF).toByte() }
        listOf(InetAddress.getByAddress(bytes))
    }.getOrDefault(emptyList())
}
