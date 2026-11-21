package com.dualscreen.app.audio

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Build
import android.util.Log
import java.nio.ByteBuffer

/**
 * Plays the PC's sound (AUDIO frames: rate u32, channels u8, pts i64, PCM s16le) through a low-latency AudioTrack.
 * If more than ~80 ms piles up (Wi-Fi burst), the backlog is flushed so sound stays in sync with the picture.
 */
class AudioPlayer {
    private var track: AudioTrack? = null
    private var rate = 0
    private var channels = 0
    private var written = 0L

    @Synchronized
    fun feed(p: ByteArray) {
        if (p.size < 13) return
        val bb = ByteBuffer.wrap(p)
        val r = bb.int
        val ch = p[4].toInt()
        if (r <= 0 || ch !in 1..2) return
        if (track == null || r != rate || ch != channels) open(r, ch)
        val t = track ?: return
        val frameBytes = 2 * ch
        val queuedFrames = written / frameBytes - t.playbackHeadPosition.toLong()
        if (queuedFrames > rate * 80 / 1000) { // > 80 ms behind: drop the backlog
            t.pause(); t.flush(); t.play(); written = 0
        }
        val n = t.write(p, 13, p.size - 13, AudioTrack.WRITE_NON_BLOCKING)
        if (n > 0) written += n
    }

    private fun open(r: Int, ch: Int) {
        release()
        rate = r; channels = ch
        val mask = if (ch == 2) AudioFormat.CHANNEL_OUT_STEREO else AudioFormat.CHANNEL_OUT_MONO
        val min = AudioTrack.getMinBufferSize(r, mask, AudioFormat.ENCODING_PCM_16BIT)
        try {
            val b = AudioTrack.Builder()
                .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC).build())
                .setAudioFormat(AudioFormat.Builder().setSampleRate(r).setChannelMask(mask)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                .setBufferSizeInBytes(maxOf(min, r * 2 * ch / 10))
                .setTransferMode(AudioTrack.MODE_STREAM)
            if (Build.VERSION.SDK_INT >= 26) b.setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            track = b.build().also { it.play() }
            written = 0
        } catch (e: Exception) {
            Log.w("DualScreen.Audio", "AudioTrack failed", e)
            track = null
        }
    }

    @Synchronized
    fun release() {
        runCatching { track?.stop() }
        runCatching { track?.release() }
        track = null
    }
}
