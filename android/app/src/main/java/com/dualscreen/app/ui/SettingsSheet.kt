package com.dualscreen.app.ui

import android.app.Activity
import android.app.Dialog
import android.graphics.drawable.ColorDrawable
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.ViewGroup
import android.view.Window
import android.view.WindowManager
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.dualscreen.app.R
import com.dualscreen.app.StreamHub
import com.dualscreen.app.data.Store
import org.json.JSONObject

/**
 * In-stream settings panel (slides in from the right), with the same rows as the "Pantalla" page and the Windows
 * app. Everything is applied on the PC; the PC answers with its new state (HOST_STATE), which redraws the panel.
 */
object SettingsSheet {
    private var dialog: Dialog? = null
    private var content: LinearLayout? = null
    private var activity: Activity? = null
    private var store: Store? = null
    private var lastState = ""
    private var statsView: TextView? = null

    fun show(a: Activity, s: Store) {
        if (dialog?.isShowing == true) return
        activity = a; store = s
        val dlg = Dialog(a)
        dlg.requestWindowFeature(Window.FEATURE_NO_TITLE)
        val box = LinearLayout(a).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(Ui.dp(a, 20f), Ui.dp(a, 22f), Ui.dp(a, 20f), Ui.dp(a, 22f))
        }
        content = box
        val scroll = ScrollView(a).apply {
            val r = Ui.dp(a, 14f).toFloat()
            background = GradientDrawable().apply {
                setColor(a.getColor(R.color.panel)); cornerRadii = floatArrayOf(r, r, 0f, 0f, 0f, 0f, r, r)
                setStroke(Ui.dp(a, 1f), a.getColor(R.color.stroke))
            }
            addView(box)
        }
        dlg.setContentView(scroll)
        dlg.window?.apply {
            setBackgroundDrawable(ColorDrawable(0))
            setGravity(Gravity.END or Gravity.FILL_VERTICAL)
            setLayout(minOf(Ui.dp(a, 400f), (a.resources.displayMetrics.widthPixels * 0.92f).toInt()), ViewGroup.LayoutParams.MATCH_PARENT)
            addFlags(WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS)
            setDimAmount(0.35f)
        }
        dlg.setOnDismissListener { dialog = null; content = null; lastState = "" }
        dialog = dlg
        build()
        dlg.show()
    }

    /** Called when the stream state changes; rebuilds only if the PC's settings changed. */
    fun refresh() {
        if (dialog?.isShowing != true) return
        val key = StreamHub.hostState?.toString() ?: ""
        if (key != lastState) build() else updateStats()
    }

    private fun updateStats() {
        val cfg = StreamHub.config ?: return
        val lat = StreamHub.latencyMs
        statsView?.text = "${cfg.width} × ${cfg.height} · ${"%.0f".format(StreamHub.fps)} fps · " +
            (if (lat.isNaN()) "latencia midiendo…" else "latencia %.0f ms".format(lat)) +
            " · %.1f Mbps".format(StreamHub.recvKbps / 1000f)
    }

    private fun build() {
        val a = activity ?: return
        val box = content ?: return
        val s = store ?: return
        val st = StreamHub.hostState
        lastState = st?.toString() ?: ""
        box.removeAllViews()

        box.addView(Ui.title(a, "Ajustes").apply { setPadding(0, 0, 0, Ui.dp(a, 2f)) })
        box.addView(Ui.caption(a, "Se aplican en ${st?.optString("hostName")?.ifEmpty { null } ?: StreamHub.hostName}"))
        statsView = Ui.caption(a, "", R.color.text3).apply { setPadding(0, Ui.dp(a, 4f), 0, Ui.dp(a, 14f)) }
        box.addView(statsView)
        updateStats()

        if (st == null) box.addView(Ui.caption(a, "Esta PC tiene una versión de DualScreen sin ajustes remotos. Actualizala para cambiar el modo y la calidad desde acá.", R.color.warning))
        else HostSettingsView.populate(a, box, st)

        box.addView(Ui.subtitle(a, "En el teléfono"))
        box.addView(Ui.toggleRow(a, "Sonido de la PC", "Reproduce en el teléfono lo que suena en la PC.", s.audio) {
            s.audio = it
            StreamHub.sendHostSettings(JSONObject().put("audio", it))
        })
        box.addView(Ui.toggleRow(a, "Estadísticas en pantalla", "Resolución, fps, latencia y velocidad en una esquina.", s.showStats) { s.showStats = it })

        box.addView(Ui.button(a, "Desconectar", false) { dialog?.dismiss(); StreamHub.disconnect(); a.finish() }.apply {
            setTextColor(a.getColor(R.color.error))
        }, LinearLayout.LayoutParams(-1, Ui.dp(a, 46f)).apply { topMargin = Ui.dp(a, 18f) })
    }
}
