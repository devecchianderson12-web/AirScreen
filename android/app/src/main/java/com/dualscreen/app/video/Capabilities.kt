package com.dualscreen.app.video

import android.content.Context
import android.hardware.display.DisplayManager
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.os.Build
import android.util.Log
import android.view.Display
import com.dualscreen.app.net.Protocol
import org.json.JSONArray
import org.json.JSONObject

/**
 * Describes what this phone can really display and decode, so the PC never offers impossible modes:
 * screen size, panel refresh rates, and per-codec hardware decoder limits tested with MediaCodec.
 */
object Capabilities {
    private val MIMES = listOf("video/avc", "video/hevc", "video/av01")

    fun build(context: Context): JSONObject {
        val dm = context.getSystemService(DisplayManager::class.java)!!
        val display = dm.getDisplay(Display.DEFAULT_DISPLAY)
        val modes = display.supportedModes
        val maxMode = modes.maxByOrNull { it.physicalWidth * it.physicalHeight } ?: display.mode
        val rates = modes.filter { it.physicalWidth == maxMode.physicalWidth && it.physicalHeight == maxMode.physicalHeight }
            .map { it.refreshRate }.distinct().sorted()

        val native = maxOf(maxMode.physicalWidth, maxMode.physicalHeight) to minOf(maxMode.physicalWidth, maxMode.physicalHeight)
        val candidates = (Protocol.STANDARD_RESOLUTIONS + native).distinct()

        val codecs = JSONArray()
        for (mime in MIMES) {
            val info = bestDecoder(mime) ?: continue
            val vc = info.getCapabilitiesForType(mime).videoCapabilities ?: continue
            val modesJson = JSONArray()
            for ((w, h) in candidates) {
                val fps = maxFps(vc, w, h)
                if (fps >= 30) modesJson.put(JSONObject().put("width", w).put("height", h).put("maxFps", fps))
            }
            codecs.put(
                JSONObject()
                    .put("mime", mime)
                    .put("hardware", isHardware(info))
                    .put("maxWidth", vc.supportedWidths.upper)
                    .put("maxHeight", vc.supportedHeights.upper)
                    .put("modes", modesJson)
            )
        }

        return JSONObject()
            .put("screenWidth", maxMode.physicalWidth)
            .put("screenHeight", maxMode.physicalHeight)
            .put("densityDpi", context.resources.displayMetrics.densityDpi)
            .put("refreshRates", JSONArray(rates.map { it.toDouble() }))
            .put("codecs", codecs)
            .put("abi", Build.SUPPORTED_ABIS.firstOrNull() ?: "")
            .put("sdkInt", Build.VERSION.SDK_INT)
            .put("platform", "android")
            .put("input", JSONArray(listOf("touch", "pen")))
            .put("features", JSONArray(listOf("audio", "hostSettings", "latency")))
    }

    /** Highest frame rate the decoder supports at w×h in either orientation (capped at 240). */
    private fun maxFps(vc: MediaCodecInfo.VideoCapabilities, w: Int, h: Int): Int {
        for ((a, b) in listOf(w to h, h to w)) {
            if (!vc.isSizeSupported(a, b)) continue
            val upper = runCatching { vc.getSupportedFrameRatesFor(a, b).upper }.getOrNull() ?: continue
            val fps = upper.toInt().coerceAtMost(240)
            // Some decoders report optimistic ranges; confirm with the combined check.
            var f = fps
            while (f > 30 && !vc.areSizeAndRateSupported(a, b, f.toDouble())) f -= 10
            return f
        }
        return 0
    }

    fun bestDecoder(mime: String): MediaCodecInfo? {
        val all = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
            .filter { !it.isEncoder && it.supportedTypes.any { t -> t.equals(mime, ignoreCase = true) } }
        return all.firstOrNull { isHardware(it) } ?: all.firstOrNull().also {
            if (it != null) Log.i("DualScreen", "Only software decoder for $mime: ${it.name}")
        }
    }

    fun isHardware(info: MediaCodecInfo): Boolean =
        if (Build.VERSION.SDK_INT >= 29) info.isHardwareAccelerated
        else !info.name.startsWith("OMX.google.") && !info.name.startsWith("c2.android.")
}
