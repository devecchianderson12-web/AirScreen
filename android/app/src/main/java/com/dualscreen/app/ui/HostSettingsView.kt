package com.dualscreen.app.ui

import android.app.Activity
import android.widget.LinearLayout
import com.dualscreen.app.R
import com.dualscreen.app.StreamHub
import org.json.JSONObject

/**
 * The Windows "Pantalla" page, driven by the PC's HOST_STATE. Every change is sent as HOST_SETTINGS and applied
 * to the PC's own settings (the same ones shown in DualScreen for Windows); the PC replies with its new state.
 */
object HostSettingsView {
    fun populate(a: Activity, box: LinearLayout, st: JSONObject) {
        fun send(key: String, value: Any) = StreamHub.sendHostSettings(JSONObject().put(key, value))

        box.addView(Ui.subtitle(a, "Modo").apply { setPadding(Ui.dp(a, 2f), 0, 0, Ui.dp(a, 8f)) })
        val mode = st.optString("mode")
        val modes = LinearLayout(a).apply { orientation = LinearLayout.HORIZONTAL }
        modes.addView(Ui.optionCard(a, R.drawable.ic_nav_display, "Pantalla extendida",
            "Un monitor más: arrastrá ventanas hacia él.", mode == "extended") { if (mode != "extended") send("mode", "extended") },
            LinearLayout.LayoutParams(0, -1, 1f).apply { rightMargin = Ui.dp(a, 6f) })
        modes.addView(Ui.optionCard(a, R.drawable.ic_nav_devices, "Modo espejo",
            "Muestra un monitor de la PC.", mode == "mirror") { if (mode != "mirror") send("mode", "mirror") },
            LinearLayout.LayoutParams(0, -1, 1f))
        box.addView(modes)
        if (mode == "extended" && !st.optBoolean("driverOk", true))
            box.addView(Ui.caption(a, "La pantalla extendida necesita el controlador de DualScreen en la PC (Configuración → Controlador). Mientras tanto se usa el espejo.", R.color.warning)
                .apply { setPadding(Ui.dp(a, 2f), Ui.dp(a, 8f), 0, 0) })

        val monitors = st.optJSONArray("monitors")
        if (mode == "mirror" && monitors != null && monitors.length() > 1) {
            val opts = mutableListOf("" to "Monitor principal")
            for (i in 0 until monitors.length()) {
                val m = monitors.getJSONObject(i)
                opts += m.optString("id") to "${m.optString("name")} · ${m.optInt("width")} × ${m.optInt("height")}"
            }
            box.addView(LinearLayout(a).apply { setPadding(0, Ui.dp(a, 8f), 0, 0) })
            box.addView(Ui.choiceRow(a, "Monitor a mostrar", "Con varios monitores, elegí cuál ves.", opts, st.optString("mirrorMonitor")) { send("mirrorMonitor", it) })
        }

        box.addView(Ui.subtitle(a, "Imagen"))
        val res = st.optJSONArray("resolutions")
        val resOpts = mutableListOf("auto" to "Automática (nativa)")
        if (res != null) for (i in 0 until res.length()) resOpts += res.getString(i) to res.getString(i).replace("x", " × ")
        box.addView(Ui.choiceRow(a, "Resolución", "Solo las que este teléfono puede decodificar.", resOpts, st.optString("resolution", "auto")) { send("resolution", it) })
        val rates = st.optJSONArray("rates")
        val rateOpts = mutableListOf("0" to "Máxima disponible")
        if (rates != null) for (i in 0 until rates.length()) rateOpts += rates.getInt(i).toString() to "${rates.getInt(i)} Hz"
        box.addView(Ui.choiceRow(a, "Frecuencia de actualización", "Se reduce sola si la red no la sostiene.", rateOpts, st.optInt("refreshRate").toString()) { send("refreshRate", it.toInt()) })
        box.addView(Ui.choiceRow(a, "Calidad de imagen", "Define el bitrate objetivo.",
            listOf("auto" to "Automática (adaptativa)", "maximum" to "Máxima", "high" to "Alta", "balanced" to "Equilibrada", "saver" to "Ahorro de datos"),
            st.optString("quality", "auto")) { send("quality", it) })

        box.addView(Ui.subtitle(a, "Rendimiento"))
        box.addView(Ui.choiceRow(a, "Prioridad", null,
            listOf("lowLatency" to "Latencia mínima", "balanced" to "Equilibrado", "quality" to "Calidad"),
            st.optString("performance", "lowLatency")) { send("performance", it) })
        val codecs = st.optJSONArray("codecs")
        val codecOpts = mutableListOf("auto" to "Automático")
        if (codecs != null) for (i in 0 until codecs.length()) {
            val c = codecs.getString(i)
            codecOpts += c to when (c) { "hevc" -> "H.265 / HEVC"; "av1" -> "AV1"; else -> "H.264" }
        }
        box.addView(Ui.choiceRow(a, "Códec", "Disponibles en ambos equipos.", codecOpts.distinctBy { it.first }, st.optString("codec", "auto")) { send("codec", it) })
        val mb = st.optInt("maxBitrateMbps")
        box.addView(Ui.choiceRow(a, "Bitrate máximo", "Automático según resolución y frecuencia.",
            listOf(0, 10, 20, 30, 50, 80, 120).map { it.toString() to if (it == 0) "Automático" else "$it Mbps" },
            mb.toString()) { send("maxBitrateMbps", it.toInt()) })
    }
}
