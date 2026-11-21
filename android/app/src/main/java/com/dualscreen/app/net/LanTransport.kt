package com.dualscreen.app.net

import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/** A connected byte stream to the PC plus the SHA-256 fingerprint of the PC certificate it is bound to. */
interface Transport {
    val input: InputStream
    val output: OutputStream
    val fingerprint: String
    val description: String
    fun close()
}

/**
 * TLS over TCP to the PC. The PC uses a self-signed certificate; trust comes from pinning its SHA-256
 * fingerprint (learned from the QR code or from a previous pairing) — never from a CA.
 * When [expectedFingerprint] is null (first pairing by typed code), the certificate is accepted and its
 * fingerprint is bound into the pairing proof, so a MITM cannot complete the pairing without the code.
 */
class LanTransport private constructor(private val socket: SSLSocket, override val fingerprint: String, override val description: String) : Transport {
    override val input: InputStream = socket.inputStream
    override val output: OutputStream = socket.outputStream

    override fun close() {
        runCatching { socket.close() }
    }

    companion object {
        @Throws(IOException::class)
        fun connect(address: String, port: Int, expectedFingerprint: String?, timeoutMs: Int = 2500): LanTransport {
            val raw = Socket()
            raw.connect(InetSocketAddress(address, port), timeoutMs)
            return secure(raw, address, port, expectedFingerprint, "Wi-Fi / LAN ($address)")
        }

        /**
         * Reverse connection: the phone listens on a random port, asks the PC (through the signaling channel) to
         * dial it, and runs the TLS *client* side on the accepted socket. Outbound connections from the PC are not
         * blocked by Windows Firewall, so no rule or administrator rights are needed on the PC.
         */
        @Throws(IOException::class)
        fun acceptReverse(server: java.net.ServerSocket, expectedFingerprint: String?, expectedHostId: String? = null): LanTransport {
            val raw = server.accept()
            val address = raw.inetAddress.hostAddress ?: "?"
            return secure(raw, address, raw.port, expectedFingerprint, "Wi-Fi / LAN ($address, PC → teléfono)", expectedHostId)
        }

        /**
         * Reverse connection through the PC's LAN beacon (no Internet, no firewall rule on the PC): listens on a random
         * port, answers the PC's beacons with "connect to me" until it does, then runs TLS as the client.
         * Connections from other PCs are rejected by the host-id check.
         */
        @Throws(IOException::class)
        fun viaBeacon(host: SeenHost, localAddresses: List<String>, expectedFingerprint: String?, timeoutMs: Int = 9000): LanTransport {
            java.net.ServerSocket(0).use { server ->
                server.soTimeout = timeoutMs
                val stop = java.util.concurrent.atomic.AtomicBoolean(false)
                val dialer = Thread {
                    while (!stop.get()) {
                        Beacons.find(host.hostId)?.let { Beacons.sendDial(it, localAddresses, server.localPort) }
                        try { Thread.sleep(900) } catch (_: InterruptedException) { break }
                    }
                }.apply { isDaemon = true; start() }
                try {
                    return acceptReverse(server, expectedFingerprint, host.hostId)
                } finally {
                    stop.set(true)
                    dialer.interrupt()
                }
            }
        }

        private fun secure(raw: Socket, address: String, port: Int, expectedFingerprint: String?, description: String, expectedHostId: String? = null): LanTransport {
            var seen: String? = null
            val tm = object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = throw CertificateException()
                override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String?) {
                    val fp = Protocol.sha256Hex(chain[0].encoded)
                    seen = fp
                    if (expectedFingerprint != null && !fp.equals(expectedFingerprint, ignoreCase = true))
                        throw CertificateException("PC certificate fingerprint mismatch")
                    // hostId = first 16 bytes of SHA-256(SubjectPublicKeyInfo): proves it is the PC we asked for.
                    if (expectedHostId != null) {
                        val id = Protocol.sha256Hex(chain[0].publicKey.encoded).take(32)
                        if (!id.equals(expectedHostId, ignoreCase = true)) throw CertificateException("another PC answered")
                    }
                }
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            }
            val ctx = SSLContext.getInstance("TLS").apply { init(null, arrayOf(tm), SecureRandom()) }
            raw.tcpNoDelay = true
            raw.receiveBufferSize = 4 * 1024 * 1024
            raw.keepAlive = true
            val ssl = ctx.socketFactory.createSocket(raw, address, port, true) as SSLSocket
            ssl.soTimeout = 15000 // the PC pings every second; 15 s of silence means the link is dead
            ssl.startHandshake()
            return LanTransport(ssl, seen ?: throw IOException("no certificate"), description)
        }
    }
}
