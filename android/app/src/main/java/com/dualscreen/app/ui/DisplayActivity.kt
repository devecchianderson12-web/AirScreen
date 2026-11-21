package com.dualscreen.app.ui

import android.app.Activity
import android.app.AlertDialog
import android.content.pm.ActivityInfo
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.view.Gravity
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ProgressBar
import android.widget.TextView
import com.dualscreen.app.LinkState
import com.dualscreen.app.R
import com.dualscreen.app.StreamHub
import com.dualscreen.app.data.Store
import com.dualscreen.app.net.InputCodec

/**
 * Full-screen receiver. Nothing covers the image except a tiny, almost transparent settings dot at the top-right
 * edge: it opens the settings panel (mode, monitor, quality, latency, sound…), which applies the changes on the PC.
 * Touch and stylus are forwarded to the PC, so the phone works as a touch screen for it.
 */
class DisplayActivity : Activity(), SurfaceHolder.Callback {
    private lateinit var surfaceView: SurfaceView
    private lateinit var overlay: TextView
    private lateinit var waiting: View
    private lateinit var waitingText: TextView
    private lateinit var store: Store
    private val listener: () -> Unit = { render() }
    private val contactIds = HashMap<Int, Int>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = Store(this)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        if (Build.VERSION.SDK_INT >= 30) {
            // Ask the panel for its highest refresh rate while streaming.
            window.attributes = window.attributes.apply { preferredDisplayModeId = bestModeId() }
        }

        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        surfaceView = SurfaceView(this)
        surfaceView.holder.addCallback(this)
        root.addView(surfaceView, FrameLayout.LayoutParams(-1, -1, Gravity.CENTER))

        val d = resources.displayMetrics.density
        waiting = FrameLayout(this).apply {
            addView(ProgressBar(context).apply { isIndeterminate = true }, FrameLayout.LayoutParams(-2, -2, Gravity.CENTER))
        }
        waitingText = TextView(this).apply {
            setTextColor(getColor(R.color.text2)); textSize = 15f; gravity = Gravity.CENTER
        }
        (waiting as FrameLayout).addView(waitingText, FrameLayout.LayoutParams(-1, -2, Gravity.CENTER).apply { topMargin = (70 * d).toInt() })
        root.addView(waiting, FrameLayout.LayoutParams(-1, -1))

        overlay = TextView(this).apply {
            setBackgroundResource(R.drawable.overlay)
            setTextColor(Color.WHITE)
            textSize = 11f
            setPadding((10 * d).toInt(), (6 * d).toInt(), (10 * d).toInt(), (6 * d).toInt())
            visibility = View.GONE
        }
        root.addView(overlay, FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.START).apply {
            leftMargin = (12 * d).toInt(); topMargin = (12 * d).toInt()
        })

        // Settings: a small, nearly invisible dot on the top-right edge (big enough to tap, not to distract).
        val dot = View(this).apply {
            background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(0x99FFFFFF.toInt()) }
            alpha = 0.35f
        }
        val tap = FrameLayout(this).apply {
            contentDescription = "Ajustes"
            addView(dot, FrameLayout.LayoutParams((12 * d).toInt(), (12 * d).toInt(), Gravity.CENTER))
            setOnClickListener { SettingsSheet.show(this@DisplayActivity, store) }
        }
        root.addView(tap, FrameLayout.LayoutParams((40 * d).toInt(), (40 * d).toInt(), Gravity.TOP or Gravity.END))

        root.setOnTouchListener { _, e -> forwardTouch(e); true }
        setContentView(root)
        enterImmersive()
    }

    // ------------------------------------------------------------------ touch -> PC

    /** Sends every finger (and the stylus, with pressure) to the PC, in video coordinates. */
    private fun forwardTouch(e: MotionEvent) {
        val sv = surfaceView
        if (sv.width == 0 || sv.height == 0) return
        val ox = sv.left.toFloat(); val oy = sv.top.toFloat()
        fun nx(x: Float) = (((x - ox) / sv.width).coerceIn(0f, 1f) * 65535).toInt()
        fun ny(y: Float) = (((y - oy) / sv.height).coerceIn(0f, 1f) * 65535).toInt()
        val ai = e.actionIndex
        val pid = e.getPointerId(ai)
        val tool = e.getToolType(ai)

        if (tool == MotionEvent.TOOL_TYPE_STYLUS || tool == MotionEvent.TOOL_TYPE_ERASER) {
            val state = when (e.actionMasked) {
                MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> InputCodec.PEN_DOWN
                MotionEvent.ACTION_MOVE -> InputCodec.PEN_MOVE
                MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> InputCodec.PEN_UP
                MotionEvent.ACTION_CANCEL -> InputCodec.PEN_LEAVE
                else -> return
            }
            var buttons = 0
            if (e.buttonState and MotionEvent.BUTTON_STYLUS_PRIMARY != 0) buttons = buttons or 1
            if (tool == MotionEvent.TOOL_TYPE_ERASER) buttons = buttons or 4
            val pressure = (e.getPressure(ai).coerceIn(0f, 1f) * 1024).toInt()
            StreamHub.sendInput(InputCodec.pen(state, nx(e.getX(ai)), ny(e.getY(ai)), pressure, buttons))
            return
        }

        fun idFor(pointerId: Int): Int = contactIds.getOrPut(pointerId) {
            var c = 0
            while (contactIds.containsValue(c)) c++
            c
        }
        fun pressure(i: Int) = (e.getPressure(i).coerceIn(0.05f, 1f) * 1024).toInt()
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN ->
                StreamHub.sendInput(InputCodec.touch(idFor(pid), InputCodec.DOWN, nx(e.getX(ai)), ny(e.getY(ai)), pressure(ai)))
            MotionEvent.ACTION_MOVE -> for (i in 0 until e.pointerCount) {
                val id = contactIds[e.getPointerId(i)] ?: continue
                StreamHub.sendInput(InputCodec.touch(id, InputCodec.MOVE, nx(e.getX(i)), ny(e.getY(i)), pressure(i)))
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                val id = contactIds.remove(pid) ?: return
                StreamHub.sendInput(InputCodec.touch(id, InputCodec.UP, nx(e.getX(ai)), ny(e.getY(ai)), 0))
            }
            MotionEvent.ACTION_CANCEL -> {
                for (id in contactIds.values) StreamHub.sendInput(InputCodec.touch(id, InputCodec.CANCEL, 0, 0, 0))
                contactIds.clear()
            }
        }
    }

    // ------------------------------------------------------------------ window

    private fun bestModeId(): Int {
        val disp = if (Build.VERSION.SDK_INT >= 30) display else null
        val modes = disp?.supportedModes ?: return 0
        val cur = disp.mode
        return modes.filter { it.physicalWidth == cur.physicalWidth && it.physicalHeight == cur.physicalHeight }
            .maxByOrNull { it.refreshRate }?.modeId ?: 0
    }

    @Suppress("DEPRECATION")
    private fun enterImmersive() {
        if (Build.VERSION.SDK_INT >= 30) {
            window.setDecorFitsSystemWindows(false)
            window.insetsController?.let {
                it.hide(WindowInsets.Type.systemBars())
                it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_FULLSCREEN
                or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN)
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) enterImmersive()
    }

    override fun onResume() {
        super.onResume()
        StreamHub.addListener(listener)
        render()
    }

    override fun onPause() {
        StreamHub.removeListener(listener)
        if (contactIds.isNotEmpty()) { StreamHub.sendInput(InputCodec.releaseAll()); contactIds.clear() }
        super.onPause()
    }

    override fun surfaceCreated(holder: SurfaceHolder) = StreamHub.attachSurface(holder.surface)
    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}
    override fun surfaceDestroyed(holder: SurfaceHolder) = StreamHub.detachSurface()

    private var lastConfigKey = ""
    private var errorShown = false

    private fun render() {
        val st = StreamHub.state
        val cfg = StreamHub.config
        waiting.visibility = if (st == LinkState.CONNECTED && cfg != null) View.GONE else View.VISIBLE
        waitingText.text = when (st) {
            LinkState.CONNECTED -> "Preparando la pantalla…"
            LinkState.DISCONNECTED -> "Desconectado"
            else -> StreamHub.message
        }
        if (st == LinkState.DISCONNECTED || st == LinkState.ERROR) {
            waiting.visibility = View.GONE // the dialog already shows the message
            if (st == LinkState.ERROR && !isFinishing && !errorShown) {
                errorShown = true
                AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
                    .setMessage(StreamHub.message).setPositiveButton("Aceptar") { _, _ -> finish() }.setCancelable(false).show()
            } else if (st == LinkState.DISCONNECTED) finish()
            return
        }
        if (cfg != null) {
            val key = "${cfg.width}x${cfg.height}"
            if (key != lastConfigKey) {
                lastConfigKey = key
                requestedOrientation = if (cfg.height > cfg.width) ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
                else ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                fitSurface(cfg.width, cfg.height)
            }
        }
        overlay.visibility = if (store.showStats && cfg != null) View.VISIBLE else View.GONE
        if (overlay.visibility == View.VISIBLE && cfg != null) {
            val lat = StreamHub.latencyMs
            val rtt = StreamHub.rttMs
            val latText = when {
                !lat.isNaN() -> "%.0f ms".format(lat)
                !rtt.isNaN() -> "≈ %.0f ms".format(rtt / 2 + StreamHub.decodeMs)
                else -> "—"
            }
            overlay.text = "${cfg.width}×${cfg.height} · ${"%.0f".format(StreamHub.fps)}/${cfg.fps} fps · $latText · " +
                "%.1f Mbps".format(StreamHub.recvKbps / 1000f)
        }
        SettingsSheet.refresh()
    }

    /** Keeps the stream's aspect ratio (letterboxing only if the PC resolution differs from the panel's). */
    private fun fitSurface(w: Int, h: Int) {
        surfaceView.post {
            val root = surfaceView.parent as View
            val rw = root.width.toFloat(); val rh = root.height.toFloat()
            if (rw == 0f || rh == 0f) return@post
            val scale = minOf(rw / w, rh / h)
            surfaceView.layoutParams = FrameLayout.LayoutParams((w * scale).toInt(), (h * scale).toInt(), Gravity.CENTER)
        }
    }

    override fun onConfigurationChanged(newConfig: android.content.res.Configuration) {
        super.onConfigurationChanged(newConfig)
        StreamHub.config?.let { fitSurface(it.width, it.height) }
    }

    @Deprecated("Back handling without androidx")
    override fun onBackPressed() {
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setMessage("¿Dejar de usar el teléfono como monitor?")
            .setPositiveButton("Desconectar") { _, _ -> StreamHub.disconnect(); finish() }
            .setNeutralButton("Minimizar") { _, _ -> finish() }
            .setNegativeButton("Cancelar", null)
            .show()
    }
}
