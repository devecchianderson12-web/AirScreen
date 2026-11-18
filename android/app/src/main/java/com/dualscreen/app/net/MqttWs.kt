package com.dualscreen.app.net

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import java.io.ByteArrayOutputStream
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit

/**
 * Minimal MQTT 3.1.1 client over WebSocket (QoS 0), mirror of windows/.../Remote/MqttWsClient.cs.
 * Used only for zero-setup WebRTC signaling through a public broker.
 */
class MqttWs(
    private val http: OkHttpClient,
    private val onConnected: () -> Unit,
    private val onMessage: (topic: String, payload: ByteArray) -> Unit,
    private val onFailure: (String) -> Unit,
) {
    private var ws: WebSocket? = null
    private val rx = ByteArrayOutputStream()
    private var packetId = 0
    private var pinger: ScheduledExecutorService? = null

    fun connect(url: String, clientId: String) {
        val wsUrl = if (url.startsWith("mqtt+")) url.substring(5) else url
        val req = Request.Builder().url(wsUrl).header("Sec-WebSocket-Protocol", "mqtt").build()
        ws = http.newWebSocket(req, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                val body = ByteArrayOutputStream()
                body.str("MQTT"); body.write(4); body.write(0x02); body.write(0); body.write(60); body.str(clientId)
                send(0x10, body.toByteArray())
                pinger = Executors.newSingleThreadScheduledExecutor().apply {
                    scheduleAtFixedRate({ send(0xC0, ByteArray(0)) }, 30, 30, TimeUnit.SECONDS)
                }
            }

            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                synchronized(rx) {
                    rx.write(bytes.toByteArray())
                    while (true) {
                        val (type, payload) = parse() ?: break
                        when (type shr 4) {
                            2 -> if (payload.size >= 2 && payload[1].toInt() == 0) onConnected() else onFailure("El broker MQTT rechazó la conexión")
                            3 -> {
                                val tl = ((payload[0].toInt() and 0xFF) shl 8) or (payload[1].toInt() and 0xFF)
                                val topic = String(payload, 2, tl)
                                val off = 2 + tl + if (((type shr 1) and 3) > 0) 2 else 0
                                onMessage(topic, payload.copyOfRange(off, payload.size))
                            }
                        }
                    }
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) =
                onFailure("No se pudo contactar con el servidor de señalización")
        })
    }

    fun subscribe(topic: String) {
        val b = ByteArrayOutputStream(); b.id(); b.str(topic); b.write(0)
        send(0x82, b.toByteArray())
    }

    fun publish(topic: String, payload: ByteArray) {
        val b = ByteArrayOutputStream(); b.str(topic); b.write(payload)
        send(0x30, b.toByteArray())
    }

    fun close() {
        pinger?.shutdownNow()
        runCatching { send(0xE0, ByteArray(0)) }
        ws?.close(1000, "bye")
    }

    private fun parse(): Pair<Int, ByteArray>? {
        val d = rx.toByteArray()
        if (d.size < 2) return null
        var len = 0; var mul = 1; var i = 1
        while (true) {
            if (i >= d.size) return null
            val b = d[i++].toInt() and 0xFF
            len += (b and 0x7F) * mul
            if (b and 0x80 == 0) break
            mul *= 128
        }
        if (d.size < i + len) return null
        val packet = (d[0].toInt() and 0xFF) to d.copyOfRange(i, i + len)
        rx.reset()
        rx.write(d, i + len, d.size - i - len)
        return packet
    }

    @Synchronized
    private fun send(header: Int, body: ByteArray) {
        val out = ByteArrayOutputStream()
        out.write(header)
        var len = body.size
        do {
            var b = len % 128
            len /= 128
            if (len > 0) b = b or 0x80
            out.write(b)
        } while (len > 0)
        out.write(body)
        ws?.send(out.toByteArray().toByteString())
    }

    private fun ByteArrayOutputStream.str(s: String) {
        val b = s.toByteArray()
        write(b.size shr 8); write(b.size and 0xFF); write(b)
    }

    private fun ByteArrayOutputStream.id() {
        packetId = packetId % 65535 + 1
        write(packetId shr 8); write(packetId and 0xFF)
    }
}
