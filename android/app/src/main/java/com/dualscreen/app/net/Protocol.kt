package com.dualscreen.app.net

import org.json.JSONObject
import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** Wire protocol v1. Keep in sync with shared/PROTOCOL.md and windows/.../Net/Protocol.cs. */
object Protocol {
    const val VERSION = 1
    const val LAN_PORT = 47800
    const val DISCOVERY_PORT = 47801
    const val MAX_PAYLOAD = 16 * 1024 * 1024

    const val HELLO: Int = 0x01
    const val WELCOME: Int = 0x02
    const val STREAM_CONFIG: Int = 0x03
    const val VIDEO: Int = 0x04
    const val PING: Int = 0x05
    const val PONG: Int = 0x06
    const val STATS: Int = 0x07
    const val KEYFRAME_REQUEST: Int = 0x08
    const val CLIENT_SETTINGS: Int = 0x09
    const val BYE: Int = 0x0A

    const val FLAG_KEYFRAME = 1

    /** Same candidate list as Negotiator.StandardResolutions on Windows. */
    val STANDARD_RESOLUTIONS = listOf(
        3840 to 2160, 3200 to 1800, 2960 to 1848, 2560 to 1600, 2560 to 1440, 2400 to 1080, 2340 to 1080,
        2048 to 1536, 1920 to 1200, 1920 to 1080, 1680 to 1050, 1600 to 900, 1440 to 900, 1366 to 768,
        1280 to 800, 1280 to 720,
    )

    fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it) }

    fun sha256Hex(bytes: ByteArray): String = hex(MessageDigest.getInstance("SHA-256").digest(bytes))

    /** HMAC-SHA256(code, "DSPAIR1|fp|deviceId|nonce") — proves knowledge of the pairing code bound to the PC certificate. */
    fun pairingProof(code: String, fingerprint: String, deviceId: String, nonce: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(code.toByteArray(), "HmacSHA256"))
        return hex(mac.doFinal("DSPAIR1|$fingerprint|$deviceId|$nonce".toByteArray()))
    }
}

class Frame(val type: Int, val payload: ByteArray) {
    fun json(): JSONObject = JSONObject(String(payload, Charsets.UTF_8))
}

/** [type:1][len:4 BE][payload] framing over any byte stream. Writes are serialized by the caller. */
class FrameIO(input: InputStream, private val output: OutputStream) {
    private val din = DataInputStream(input)

    @Throws(IOException::class)
    fun read(): Frame {
        val type = din.read()
        if (type < 0) throw EOFException()
        val len = din.readInt()
        if (len < 0 || len > Protocol.MAX_PAYLOAD) throw IOException("bad frame length $len")
        val payload = ByteArray(len)
        din.readFully(payload)
        return Frame(type, payload)
    }

    @Synchronized
    @Throws(IOException::class)
    fun write(type: Int, payload: ByteArray) {
        val header = ByteBuffer.allocate(5).put(type.toByte()).putInt(payload.size).array()
        output.write(header)
        output.write(payload)
        output.flush()
    }

    fun writeJson(type: Int, obj: JSONObject) = write(type, obj.toString().toByteArray(Charsets.UTF_8))

    fun writeLong(type: Int, value: Long) = write(type, ByteBuffer.allocate(8).putLong(value).array())
}
