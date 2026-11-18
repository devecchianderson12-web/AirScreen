package com.dualscreen.app.ui

import android.app.Activity
import android.app.AlertDialog
import android.content.pm.ActivityInfo
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.view.GestureDetector
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

/**
 * Full-screen receiver. Nothing is drawn over the image: the stats overlay only appears on a double tap
 * and hides itself after a few seconds. The screen stays on and system bars are hidden (immersive).
 */
class DisplayActivity : Activity(), SurfaceHolder.Callback {
    private lateinit var surfaceView: SurfaceView
    private lateinit var overlay: TextView
    private lateinit var waiting: View
    private lateinit var waitingText: TextView
    private val listener: () -> Unit = { render() }
    private val hideOverlay = Runnable { overlay.visibility = View.GONE }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
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
            textSize = 12f
            setPadding((12 * d).toInt(), (8 * d).toInt(), (12 * d).toInt(), (8 * d).toInt())
            visibility = View.GONE
        }
        root.addView(overlay, FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.START).apply {
            leftMargin = (16 * d).toInt(); topMargin = (16 * d).toInt()
        })

        val gestures = GestureDetector(this, object : GestureDetector.SimpleOnGestureListener() {
            override fun onDoubleTap(e: MotionEvent): Boolean {
                overlay.visibility = View.VISIBLE
                overlay.removeCallbacks(hideOverlay)
                overlay.postDelayed(hideOverlay, 4000)
                render()
                return true
            }
        })
        root.setOnTouchListener { _, e -> gestures.onTouchEvent(e); true }
        setContentView(root)
        enterImmersive()
    }

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
        super.onPause()
    }

    override fun surfaceCreated(holder: SurfaceHolder) = StreamHub.attachSurface(holder.surface)
    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}
    override fun surfaceDestroyed(holder: SurfaceHolder) = StreamHub.detachSurface()

    private var lastConfigKey = ""

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
            if (st == LinkState.ERROR && !isFinishing) {
                AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
                    .setMessage(StreamHub.message).setPositiveButton("Aceptar") { _, _ -> finish() }.setCancelable(false).show()
            } else finish()
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
        if (overlay.visibility == View.VISIBLE && cfg != null) {
            val rtt = StreamHub.rttMs
            overlay.text = "${cfg.width}×${cfg.height} · ${"%.0f".format(StreamHub.fps)}/${cfg.fps} fps\n" +
                "Latencia ≈ ${if (rtt.isNaN()) "—" else "%.0f".format(rtt / 2 + StreamHub.decodeMs)} ms · " +
                "%.1f Mbps\n${StreamHub.transportDesc}".format(StreamHub.recvKbps / 1000f) +
                "\nAtrás para salir de la pantalla"
        }
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
