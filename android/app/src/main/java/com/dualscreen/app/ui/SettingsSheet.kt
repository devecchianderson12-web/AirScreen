package com.dualscreen.app.ui

import android.app.Activity
import android.app.Dialog
import android.graphics.Typeface
import android.graphics.drawable.ColorDrawable
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.ViewGroup
import android.view.Window
import android.view.WindowManager
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import com.dualscreen.app.R
import com.dualscreen.app.StreamHub
import com.dualscreen.app.data.Store
import org.json.JSONObject

/**
 * In-stream settings panel (slides in from the right). Everything here is applied on the PC — the same settings
 * shown in DualScreen for Windows — and the PC answers with its new state (HOST_STATE), which redraws the panel.
 */
object SettingsSheet {
    private var dialog: Dialog? = null
    private var content: LinearLayout? = null
    private var activity: Activity? = null
    private var store: Store? = null
    private var lastState = ""

    fun show(a: Activity, s: Store) {
        if (dialog?.isShowing == true) return
        activity = a; store = s
        val d = a.resources.displayMetrics.density
        val dlg = Dialog(a)
        dlg.requestWindowFeature(Window.FEATURE_NO_TITLE)
        val box = LinearLayout(a).apply {
            orientation = LinearLayout.VERTICAL
            setPadding((20 * d).toInt(), (20 * d).toInt(), (20 * d).toInt(), (20 * d).toInt())
        }
        content = box
        val scroll = ScrollView(a).apply {
            background = GradientDrawable().apply {
                setColor(a.getColor(R.color.bg)); cornerRadii = floatArrayOf(24 * d, 24 * d, 0f, 0f, 0f, 0f, 24 * d, 24 * d)
            }
            addView(box)
        }
        dlg.setContentView(scroll)
        dlg.window?.apply {
            setBackgroundDrawable(ColorDrawable(0))
            setGravity(Gravity.END or Gravity.FILL_VERTICAL)
            setLayout(minOf((380 * d).toInt(), (a.resources.displayMetrics.widthPixels * 0.9f).toInt()), ViewGroup.LayoutParams.MATCH_PARENT)
            addFlags(WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS)
            setDimAmount(0.35f)
            setWindowAnimations(android.R.style.Animation_Translucent)
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

    private var statsView: TextView? = null

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
        val d = a.resources.displayMetrics.density

        box.addView(TextView(a).apply {
            text = "Ajustes"; textSize = 26f; setTypeface(typeface, Typeface.BOLD); setTextColor(a.getColor(R.color.text))
        })
        box.addView(TextView(a).apply {
            text = "Se aplican en ${st?.optString("hostName")?.ifEmpty { null } ?: StreamHub.hostName}"
            textSize = 14f; setTextColor(a.getColor(R.color.text2))
        })
        statsView = TextView(a).apply { textSize = 12f; setTextColor(a.getColor(R.color.text3)); setPadding(0, (6 * d).toInt(), 0, 0) }
        box.addView(statsView)
        updateStats()

        if (st == null) {
            box.addView(note(a, "Este PC tiene una versión de DualScreen sin ajustes remotos. Actualizalo para cambiar el modo y la calidad desde acá."))
        } else {
            section(a, box, "Modo")
            pills(a, box, listOf("extended" to "Pantalla extendida", "mirror" to "Espejo"), st.optString("mode")) { send("mode", it) }
            if (st.optString("mode") == "extended" && !st.optBoolean("driverOk", true))
                box.addView(note(a, "La pantalla extendida necesita el controlador de DualScreen en la PC (Configuración → Controlador). Mientras tanto se usa el espejo."))

            val monitors = st.optJSONArray("monitors")
            if (st.optString("mode") == "mirror" && monitors != null && monitors.length() > 1) {
                section(a, box, "Monitor")
                val opts = mutableListOf("" to "Principal")
                for (i in 0 until monitors.length()) {
                    val m = monitors.getJSONObject(i)
                    opts += m.optString("id") to "${m.optString("name")} (${m.optInt("width")}×${m.optInt("height")})"
                }
                pills(a, box, opts, st.optString("mirrorMonitor")) { send("mirrorMonitor", it) }
            }

            section(a, box, "Prioridad")
            pills(a, box, listOf("lowLatency" to "Latencia mínima", "balanced" to "Equilibrado", "quality" to "Calidad"), st.optString("performance")) { send("performance", it) }

            section(a, box, "Calidad de imagen")
            pills(a, box, listOf("auto" to "Auto", "maximum" to "Máxima", "high" to "Alta", "balanced" to "Equilibrada", "saver" to "Ahorro"), st.optString("quality")) { send("quality", it) }

            val res = st.optJSONArray("resolutions")
            if (res != null && res.length() > 0) {
                section(a, box, "Resolución")
                val opts = mutableListOf("auto" to "Nativa")
                for (i in 0 until res.length()) opts += res.getString(i) to res.getString(i).replace("x", " × ")
                pills(a, box, opts, st.optString("resolution")) { send("resolution", it) }
            }
            val rates = st.optJSONArray("rates")
            if (rates != null && rates.length() > 0) {
                section(a, box, "Frecuencia")
                val opts = mutableListOf("0" to "Máxima")
                for (i in 0 until rates.length()) opts += rates.getInt(i).toString() to "${rates.getInt(i)} Hz"
                pills(a, box, opts, st.optInt("refreshRate").toString()) { send("refreshRate", it.toInt()) }
            }
            val codecs = st.optJSONArray("codecs")
            if (codecs != null && codecs.length() > 1) {
                section(a, box, "Códec")
                val opts = mutableListOf("auto" to "Auto")
                for (i in 0 until codecs.length()) {
                    val c = codecs.getString(i)
                    opts += c to when (c) { "hevc" -> "HEVC"; "av1" -> "AV1"; else -> "H.264" }
                }
                pills(a, box, opts, st.optString("codec")) { send("codec", it) }
            }
        }

        section(a, box, "En el teléfono")
        toggle(a, box, "Sonido de la PC", "Reproduce en el teléfono lo que suena en la PC.", s.audio) {
            s.audio = it
            StreamHub.sendHostSettings(JSONObject().put("audio", it))
        }
        toggle(a, box, "Estadísticas en pantalla", "Resolución, fps, latencia y velocidad en una esquina.", s.showStats) {
            s.showStats = it
        }

        box.addView(TextView(a).apply {
            text = "Desconectar"
            gravity = Gravity.CENTER
            textSize = 16f
            setTextColor(a.getColor(R.color.error))
            background = GradientDrawable().apply { setColor(a.getColor(R.color.surface)); cornerRadius = 27 * d }
            setOnClickListener { dialog?.dismiss(); StreamHub.disconnect(); a.finish() }
        }, LinearLayout.LayoutParams(-1, (52 * d).toInt()).apply { topMargin = (24 * d).toInt() })
    }

    private fun send(key: String, value: Any) {
        StreamHub.sendHostSettings(JSONObject().put(key, value))
    }

    private fun section(a: Activity, box: LinearLayout, title: String) {
        val d = a.resources.displayMetrics.density
        box.addView(TextView(a).apply {
            text = title; textSize = 15f; setTypeface(typeface, Typeface.BOLD); setTextColor(a.getColor(R.color.text))
            setPadding(0, (20 * d).toInt(), 0, (8 * d).toInt())
        })
    }

    private fun note(a: Activity, text: String) = TextView(a).apply {
        this.text = text; textSize = 12f; setTextColor(a.getColor(R.color.warning))
        setPadding(0, (6 * d(a)).toInt(), 0, 0)
    }

    private fun d(a: Activity) = a.resources.displayMetrics.density

    /** Horizontally scrollable pill selector; the selected pill is filled with the accent color. */
    private fun pills(a: Activity, box: LinearLayout, options: List<Pair<String, String>>, selected: String, onPick: (String) -> Unit) {
        val d = d(a)
        val row = LinearLayout(a).apply { orientation = LinearLayout.HORIZONTAL }
        for ((value, label) in options) {
            val on = value == selected
            row.addView(TextView(a).apply {
                text = label
                textSize = 14f
                gravity = Gravity.CENTER
                setTextColor(a.getColor(if (on) R.color.on_accent else R.color.text))
                setPadding((16 * d).toInt(), 0, (16 * d).toInt(), 0)
                background = GradientDrawable().apply {
                    setColor(a.getColor(if (on) R.color.accent else R.color.surface)); cornerRadius = 20 * d
                }
                setOnClickListener { if (!on) onPick(value) }
            }, LinearLayout.LayoutParams(-2, (40 * d).toInt()).apply { rightMargin = (8 * d).toInt() })
        }
        box.addView(HorizontalScrollView(a).apply { isHorizontalScrollBarEnabled = false; addView(row) })
    }

    private fun toggle(a: Activity, box: LinearLayout, title: String, sub: String, value: Boolean, onChange: (Boolean) -> Unit) {
        val d = d(a)
        val card = LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding((16 * d).toInt(), (14 * d).toInt(), (12 * d).toInt(), (14 * d).toInt())
            background = GradientDrawable().apply { setColor(a.getColor(R.color.surface)); cornerRadius = 18 * d }
        }
        val texts = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL }
        texts.addView(TextView(a).apply { text = title; textSize = 15f; setTextColor(a.getColor(R.color.text)) })
        texts.addView(TextView(a).apply { text = sub; textSize = 12f; setTextColor(a.getColor(R.color.text2)) })
        card.addView(texts, LinearLayout.LayoutParams(0, -2, 1f))
        val sw = Switch(a).apply {
            isChecked = value
            thumbTintList = android.content.res.ColorStateList.valueOf(a.getColor(R.color.text))
            trackTintList = android.content.res.ColorStateList(
                arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
                intArrayOf(a.getColor(R.color.accent), a.getColor(R.color.surface_high)))
            setOnCheckedChangeListener { _, c -> onChange(c) }
        }
        card.addView(sw)
        box.addView(card, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = (8 * d).toInt() })
        card.setOnClickListener { sw.toggle() }
    }
}
