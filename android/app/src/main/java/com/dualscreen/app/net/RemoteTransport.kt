package com.dualscreen.app.net

import android.content.Context
import android.util.Base64
import android.util.Log
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONArray
import org.json.JSONObject
import org.webrtc.DataChannel
import org.webrtc.IceCandidate
import org.webrtc.MediaConstraints
import org.webrtc.PeerConnection
import org.webrtc.PeerConnectionFactory
import org.webrtc.RtpReceiver
import org.webrtc.SdpObserver
import org.webrtc.SessionDescription
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.io.PipedInputStream
import java.io.PipedOutputStream
import java.math.BigInteger
import java.nio.ByteBuffer
import java.security.SecureRandom
import java.security.Signature
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * Remote connection through the signaling server + WebRTC data channel (ICE: direct P2P via STUN, TURN relay
 * as fallback). The PC signs its SDP offer (which carries the DTLS fingerprint) with its identity key; we verify
 * that signature against the pinned certificate fingerprint, so the signaling server cannot intercept the session.
 */
class RemoteTransport private constructor(
    private val ws: WebSocket?,
    private val mqtt: MqttWs?,
    private val pc: PeerConnection,
    private val dc: DataChannel,
    private val pipeIn: PipedInputStream,
    override val fingerprint: String,
) : Transport {
    override val input: InputStream = pipeIn
    override val description = "Remota (WebRTC)"
    @Volatile private var closed = false

    override val output: OutputStream = object : OutputStream() {
        override fun write(b: Int) = write(byteArrayOf(b.toByte()), 0, 1)
        override fun write(b: ByteArray, off: Int, len: Int) {
            var pos = off
            while (pos < off + len) {
                if (closed || dc.state() != DataChannel.State.OPEN) throw IOException("data channel closed")
                while (dc.bufferedAmount() > 1_000_000) {
                    Thread.sleep(2)
                    if (closed) throw IOException("data channel closed")
                }
                val n = minOf(CHUNK, off + len - pos)
                dc.send(DataChannel.Buffer(ByteBuffer.wrap(b.copyOfRange(pos, pos + n)), true))
                pos += n
            }
        }
    }

    override fun close() {
        if (closed) return
        closed = true
        runCatching { dc.close() }
        runCatching { pc.close() }
        runCatching { ws?.close(1000, "bye") }
        runCatching { mqtt?.close() }
        runCatching { pipeIn.close() }
    }

    companion object {
        private const val TAG = "DualScreen.Remote"
        private const val CHUNK = 16 * 1024
        private var factory: PeerConnectionFactory? = null
        private val http = OkHttpClient.Builder().pingInterval(20, TimeUnit.SECONDS).build()

        @Synchronized
        private fun factory(context: Context): PeerConnectionFactory {
            factory?.let { return it }
            PeerConnectionFactory.initialize(PeerConnectionFactory.InitializationOptions.builder(context.applicationContext).createInitializationOptions())
            return PeerConnectionFactory.builder().createPeerConnectionFactory().also { factory = it }
        }

        /**
         * @param signalingUrl wss://… (DualScreen signaling server) or mqtt+wss://… (public MQTT broker, zero setup).
         * @param expectedFingerprint pinned PC certificate fingerprint (from the QR code or a previous pairing).
         */
        @Throws(IOException::class)
        fun connect(
            context: Context, signalingUrl: String, hostId: String,
            expectedFingerprint: String, timeoutSec: Long = 30,
        ): RemoteTransport {
            val f = factory(context)
            val ready = CountDownLatch(1)
            var error: String? = null
            var pc: PeerConnection? = null
            var channel: DataChannel? = null
            var fingerprint: String? = null
            val pipeIn = PipedInputStream(8 * 1024 * 1024)
            val pipeOut = PipedOutputStream(pipeIn)
            var ws: WebSocket? = null
            var mqtt: MqttWs? = null
            val peerId = Protocol.hex(ByteArray(12).also { SecureRandom().nextBytes(it) })
            val hostTopic = "dualscreen/v1/h/$hostId"

            fun send(o: JSONObject) {
                val m = mqtt
                if (m != null) m.publish(hostTopic, o.put("from", peerId).toString().toByteArray()) else ws?.send(o.toString())
            }

            val listener = object : WebSocketListener() {
                override fun onMessage(webSocket: WebSocket, text: String) {
                    try { handle(JSONObject(text)) } catch (e: Exception) { Log.w(TAG, "signaling message failed", e) }
                }

                fun handle(m: JSONObject) {
                        when (m.optString("t")) {
                            "challenge" -> send(JSONObject().put("t", "join").put("hostId", hostId))
                            "joined" -> {
                                val servers = mutableListOf(
                                    PeerConnection.IceServer.builder("stun:stun.l.google.com:19302").createIceServer(),
                                )
                                val arr = m.optJSONArray("iceServers") ?: JSONArray()
                                for (i in 0 until arr.length()) {
                                    val s = arr.getJSONObject(i)
                                    servers += PeerConnection.IceServer.builder(s.getString("urls"))
                                        .setUsername(s.optString("username")).setPassword(s.optString("credential")).createIceServer()
                                }
                                val cfg = PeerConnection.RTCConfiguration(servers).apply {
                                    sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
                                    continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
                                }
                                pc = f.createPeerConnection(cfg, object : PeerConnection.Observer {
                                    override fun onIceCandidate(c: IceCandidate) {
                                        send(JSONObject().put("t", "signal").put("data", JSONObject()
                                            .put("kind", "ice").put("candidate", c.sdp).put("sdpMid", c.sdpMid).put("sdpMLineIndex", c.sdpMLineIndex)))
                                    }
                                    override fun onDataChannel(dc: DataChannel) {
                                        channel = dc
                                        dc.registerObserver(object : DataChannel.Observer {
                                            override fun onBufferedAmountChange(previousAmount: Long) {}
                                            override fun onStateChange() {
                                                when (dc.state()) {
                                                    DataChannel.State.OPEN -> ready.countDown()
                                                    DataChannel.State.CLOSED -> runCatching { pipeOut.close() }
                                                    else -> {}
                                                }
                                            }
                                            override fun onMessage(buffer: DataChannel.Buffer) {
                                                val bytes = ByteArray(buffer.data.remaining())
                                                buffer.data.get(bytes)
                                                runCatching { pipeOut.write(bytes); pipeOut.flush() }
                                            }
                                        })
                                        if (dc.state() == DataChannel.State.OPEN) ready.countDown()
                                    }
                                    override fun onConnectionChange(newState: PeerConnection.PeerConnectionState) {
                                        Log.i(TAG, "peer connection: $newState")
                                        if (newState == PeerConnection.PeerConnectionState.FAILED) {
                                            error = "No se pudo establecer la conexión P2P ni por TURN"
                                            ready.countDown()
                                            runCatching { pipeOut.close() }
                                        }
                                    }
                                    override fun onSignalingChange(s: PeerConnection.SignalingState) {}
                                    override fun onIceConnectionChange(s: PeerConnection.IceConnectionState) {}
                                    override fun onIceConnectionReceivingChange(b: Boolean) {}
                                    override fun onIceGatheringChange(s: PeerConnection.IceGatheringState) {}
                                    override fun onIceCandidatesRemoved(c: Array<out IceCandidate>) {}
                                    override fun onAddStream(s: org.webrtc.MediaStream) {}
                                    override fun onRemoveStream(s: org.webrtc.MediaStream) {}
                                    override fun onRenegotiationNeeded() {}
                                    override fun onAddTrack(r: RtpReceiver, s: Array<out org.webrtc.MediaStream>) {}
                                })
                            }
                            "signal" -> {
                                val d = m.getJSONObject("data")
                                val peer = pc ?: return
                                when (d.optString("kind")) {
                                    "offer" -> {
                                        val sdp = d.getString("sdp")
                                        val fp = verifyOffer(sdp, d.getString("cert"), d.getString("sig"), expectedFingerprint)
                                        if (fp == null) {
                                            error = "La firma del PC no es válida (posible ataque)"
                                            ready.countDown()
                                            return
                                        }
                                        fingerprint = fp
                                        peer.setRemoteDescription(object : SimpleSdp() {
                                            override fun onSetSuccess() {
                                                peer.createAnswer(object : SimpleSdp() {
                                                    override fun onCreateSuccess(answer: SessionDescription) {
                                                        peer.setLocalDescription(SimpleSdp(), answer)
                                                        send(JSONObject().put("t", "signal").put("data", JSONObject().put("kind", "answer").put("sdp", answer.description)))
                                                    }
                                                }, MediaConstraints())
                                            }
                                        }, SessionDescription(SessionDescription.Type.OFFER, sdp))
                                    }
                                    "ice" -> peer.addIceCandidate(IceCandidate(d.optString("sdpMid", "0"), d.optInt("sdpMLineIndex"), d.getString("candidate")))
                                }
                            }
                            "error" -> {
                                error = when (m.optString("error")) {
                                    "host-offline" -> "El PC no está disponible para conexiones remotas"
                                    else -> "Servidor: " + m.optString("error")
                                }
                                ready.countDown()
                            }
                        }
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    error = "No se pudo contactar con el servidor de señalización"
                    ready.countDown()
                    runCatching { pipeOut.close() }
                }
            }

            if (signalingUrl.startsWith("mqtt+")) {
                mqtt = MqttWs(http,
                    onConnected = {
                        val m = mqtt!!
                        m.subscribe("dualscreen/v1/p/$peerId")
                        listener.handle(JSONObject().put("t", "joined")) // create the peer connection first
                        m.publish(hostTopic, JSONObject().put("t", "join").put("peerId", peerId).toString().toByteArray())
                    },
                    onMessage = { _, payload ->
                        try { listener.handle(JSONObject(String(payload))) } catch (e: Exception) { Log.w(TAG, "signaling message failed", e) }
                    },
                    onFailure = { msg -> error = msg; ready.countDown() })
                mqtt!!.connect(signalingUrl, "ds-p-$peerId")
            } else {
                ws = http.newWebSocket(Request.Builder().url(signalingUrl).build(), listener)
            }
            val ok = ready.await(timeoutSec, TimeUnit.SECONDS)
            val dc = channel
            val peer = pc
            if (!ok || error != null || dc == null || peer == null || fingerprint == null) {
                runCatching { dc?.close() }
                runCatching { peer?.close() }
                ws?.cancel()
                mqtt?.close()
                throw IOException(error ?: "Tiempo de espera agotado en la conexión remota")
            }
            return RemoteTransport(ws, mqtt, peer, dc, pipeIn, fingerprint!!)
        }

        /** Verifies the offer signature (ECDSA P-256, P1363 r||s) and the pinned certificate. Returns the cert fingerprint. */
        private fun verifyOffer(sdp: String, certB64: String, sigB64: String, expected: String?): String? = runCatching {
            val der = Base64.decode(certB64, Base64.DEFAULT)
            val fp = Protocol.sha256Hex(der)
            if (expected != null && !fp.equals(expected, ignoreCase = true)) return null
            val cert = CertificateFactory.getInstance("X.509").generateCertificate(der.inputStream()) as X509Certificate
            val raw = Base64.decode(sigB64, Base64.DEFAULT)
            val v = Signature.getInstance("SHA256withECDSA")
            v.initVerify(cert.publicKey)
            v.update("DSOFFER1|$sdp".toByteArray())
            if (v.verify(p1363ToDer(raw))) fp else null
        }.getOrNull()

        private fun p1363ToDer(sig: ByteArray): ByteArray {
            val half = sig.size / 2
            fun encInt(b: ByteArray): ByteArray {
                val v = BigInteger(1, b).toByteArray()
                return byteArrayOf(0x02, v.size.toByte()) + v
            }
            val body = encInt(sig.copyOfRange(0, half)) + encInt(sig.copyOfRange(half, sig.size))
            return byteArrayOf(0x30, body.size.toByte()) + body
        }
    }

    private open class SimpleSdp : SdpObserver {
        override fun onCreateSuccess(sdp: SessionDescription) {}
        override fun onSetSuccess() {}
        override fun onCreateFailure(error: String?) { Log.w(TAG, "SDP create failed: $error") }
        override fun onSetFailure(error: String?) { Log.w(TAG, "SDP set failed: $error") }
    }
}
