package com.dualscreen.app.video

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.util.Log
import android.view.Surface
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

/**
 * Hardware decoder (MediaCodec) rendering straight to the SurfaceView, tuned for minimum latency:
 * low-latency mode (Android 11+, plus vendor keys), real-time priority, and frames are released for
 * display the instant they are decoded. If decoding falls behind, queued frames are dropped up to the
 * next keyframe (and one is requested) instead of letting latency grow.
 */
class VideoDecoder(
    private val mime: String,
    private val width: Int,
    private val height: Int,
    private val fps: Int,
    private val surface: Surface,
    private val requestKeyframe: () -> Unit,
) {
    private class Packet(val data: ByteArray, val ptsUs: Long, val key: Boolean, val arrivalNs: Long)

    private val queue = ArrayBlockingQueue<Packet>(32)
    private val inFlight = ConcurrentHashMap<Long, Long>()
    private var codec: MediaCodec? = null
    private var thread: Thread? = null
    @Volatile private var running = false
    @Volatile private var waitKeyframe = true

    @Volatile var decodeMs = 0f; private set
    @Volatile var fpsNow = 0f; private set
    @Volatile var dropped = 0; private set
    private var frameCount = 0
    private var windowStart = System.nanoTime()

    fun start() {
        val format = MediaFormat.createVideoFormat(mime, width, height).apply {
            setInteger(MediaFormat.KEY_PRIORITY, 0) // real-time
            setInteger(MediaFormat.KEY_OPERATING_RATE, (fps * 2).coerceAtMost(480))
            if (Build.VERSION.SDK_INT >= 30) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            // Vendor extensions for SoCs that predate KEY_LOW_LATENCY. Unknown keys are ignored.
            setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
            setInteger("vendor.low-latency.enable", 1)
            setInteger("vendor.rtc-ext-dec-low-latency.enable", 1)
            setInteger(MediaFormat.KEY_MAX_WIDTH, width)
            setInteger(MediaFormat.KEY_MAX_HEIGHT, height)
        }
        val info = Capabilities.bestDecoder(mime)
        val c = if (info != null) MediaCodec.createByCodecName(info.name) else MediaCodec.createDecoderByType(mime)
        c.configure(format, surface, null, 0)
        c.start()
        codec = c
        running = true
        waitKeyframe = true
        thread = Thread(::loop, "DualScreen.Decoder").apply { priority = Thread.MAX_PRIORITY; start() }
        Log.i(TAG, "Decoder ${c.name} $mime ${width}x$height@$fps")
    }

    fun submit(data: ByteArray, ptsUs: Long, key: Boolean) {
        if (!running) return
        if (waitKeyframe && !key) return
        if (key) waitKeyframe = false
        if (queue.size >= 6 || !queue.offer(Packet(data, ptsUs, key, System.nanoTime()))) {
            // Falling behind: discard everything and resync on the next IDR rather than adding latency.
            dropped += queue.size + 1
            queue.clear()
            waitKeyframe = true
            requestKeyframe()
        }
    }

    private fun loop() {
        val c = codec ?: return
        val info = MediaCodec.BufferInfo()
        try {
            while (running) {
                val p = queue.poll(2, TimeUnit.MILLISECONDS)
                if (p != null) {
                    var idx = c.dequeueInputBuffer(10_000)
                    while (idx < 0 && running) {
                        drain(c, info)
                        idx = c.dequeueInputBuffer(10_000)
                    }
                    if (idx >= 0) {
                        val buf = c.getInputBuffer(idx)!!
                        buf.clear()
                        buf.put(p.data)
                        inFlight[p.ptsUs] = p.arrivalNs
                        c.queueInputBuffer(idx, 0, p.data.size, p.ptsUs, if (p.key) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
                    }
                }
                drain(c, info)
            }
        } catch (e: IllegalStateException) {
            if (running) Log.e(TAG, "decoder failure", e)
        }
    }

    private fun drain(c: MediaCodec, info: MediaCodec.BufferInfo) {
        while (true) {
            val out = c.dequeueOutputBuffer(info, 0)
            if (out < 0) return
            c.releaseOutputBuffer(out, true) // render now
            inFlight.remove(info.presentationTimeUs)?.let { t0 ->
                val ms = (System.nanoTime() - t0) / 1e6f
                decodeMs = if (decodeMs == 0f) ms else decodeMs * 0.9f + ms * 0.1f
            }
            if (inFlight.size > 64) inFlight.clear()
            frameCount++
            val now = System.nanoTime()
            if (now - windowStart >= 1_000_000_000L) {
                fpsNow = frameCount * 1e9f / (now - windowStart)
                frameCount = 0
                windowStart = now
            }
        }
    }

    fun release() {
        running = false
        thread?.join(500)
        runCatching { codec?.stop() }
        runCatching { codec?.release() }
        codec = null
        queue.clear()
    }

    companion object { private const val TAG = "DualScreen.Decoder" }
}
