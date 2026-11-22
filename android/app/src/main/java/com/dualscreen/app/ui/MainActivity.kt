package com.dualscreen.app.ui

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.text.InputFilter
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import com.dualscreen.app.BuildConfigCompat
import com.dualscreen.app.LinkState
import com.dualscreen.app.R
import com.dualscreen.app.StreamHub
import com.dualscreen.app.Target
import com.dualscreen.app.data.PairedHost
import com.dualscreen.app.data.Store
import com.dualscreen.app.net.Beacons
import com.dualscreen.app.net.DiscoveredHost
import com.dualscreen.app.net.PairInfo
import com.dualscreen.app.net.SeenHost
import com.dualscreen.app.update.ApkRelease
import com.dualscreen.app.update.AppUpdater
import org.json.JSONObject

/**
 * DualScreen for Android, laid out like DualScreen for Windows: Inicio (status + live stats), Equipos (the
 * computers on the network, one tap to connect, no code), Pantalla (the PC's display settings, applied on the PC)
 * and Configuración (sound, stats, updates).
 */
class MainActivity : Activity() {
    private enum class Page { HOME, DEVICES, DISPLAY, SETTINGS }

    private lateinit var store: Store
    private lateinit var scroll: ScrollView
    private lateinit var pageBox: LinearLayout
    private lateinit var navBar: LinearLayout
    private lateinit var updateBadge: TextView
    private var page = Page.HOME
    private val hubListener: () -> Unit = { scheduleRender() }
    private val beaconListener: (SeenHost) -> Unit = { scheduleRender() }
    private var renderPending = false
    private var lastSignature = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = Store(this)

        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(getColor(R.color.bg)) }
        // Title bar (as in Windows): logo, name, update badge.
        val header = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            setPadding(Ui.dp(context, 18f), Ui.dp(context, 14f), Ui.dp(context, 18f), Ui.dp(context, 6f))
        }
        header.addView(ImageView(this).apply { setImageResource(R.mipmap.ic_launcher) }, LinearLayout.LayoutParams(Ui.dp(this@MainActivity, 24f), Ui.dp(this@MainActivity, 24f)))
        header.addView(TextView(this).apply { text = getString(R.string.app_name); textSize = 14f; setTextColor(getColor(R.color.text)); setPadding(Ui.dp(context, 10f), 0, 0, 0) })
        updateBadge = TextView(this).apply {
            text = "Actualización disponible"; textSize = 11f; setTextColor(getColor(R.color.on_accent))
            background = Ui.shape(context, getColor(R.color.accent), 8f)
            setPadding(Ui.dp(context, 8f), Ui.dp(context, 2f), Ui.dp(context, 8f), Ui.dp(context, 2f))
            visibility = View.GONE
            setOnClickListener { show(Page.SETTINGS) }
        }
        header.addView(updateBadge, LinearLayout.LayoutParams(-2, -2).apply { leftMargin = Ui.dp(this@MainActivity, 12f) })
        root.addView(header)

        // Content panel (rounded top-left like the Windows content area).
        val panel = FrameLayout(this).apply {
            background = GradientDrawable().apply {
                setColor(getColor(R.color.panel)); val r = Ui.dp(context, 14f).toFloat()
                cornerRadii = floatArrayOf(r, r, r, r, 0f, 0f, 0f, 0f); setStroke(Ui.dp(context, 1f), getColor(R.color.stroke))
            }
        }
        scroll = ScrollView(this).apply { isFillViewport = true }
        pageBox = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(Ui.dp(context, 20f), Ui.dp(context, 22f), Ui.dp(context, 20f), Ui.dp(context, 28f))
        }
        scroll.addView(pageBox)
        panel.addView(scroll)
        root.addView(panel, LinearLayout.LayoutParams(-1, 0, 1f))

        navBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(getColor(R.color.bg))
            setPadding(Ui.dp(context, 8f), Ui.dp(context, 6f), Ui.dp(context, 8f), Ui.dp(context, 8f))
        }
        root.addView(navBar)
        setContentView(root)

        checkForUpdate(userInitiated = false)
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 2)
        handleIntent(intent)
        show(Page.HOME)
    }

    // ------------------------------------------------------------------ navigation

    private fun show(p: Page) {
        page = p
        lastSignature = ""
        buildNav()
        render()
        scroll.scrollTo(0, 0)
    }

    private fun buildNav() {
        navBar.removeAllViews()
        val items = listOf(
            Triple(Page.HOME, R.drawable.ic_nav_home, "Inicio"),
            Triple(Page.DEVICES, R.drawable.ic_nav_devices, "Equipos"),
            Triple(Page.DISPLAY, R.drawable.ic_nav_display, "Pantalla"),
            Triple(Page.SETTINGS, R.drawable.ic_nav_settings, "Configuración"),
        )
        for ((p, icon, label) in items) {
            val on = p == page
            navBar.addView(LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER
                setPadding(0, Ui.dp(context, 6f), 0, Ui.dp(context, 6f))
                background = if (on) Ui.shape(context, getColor(R.color.accent_soft), 7f) else null
                addView(ImageView(context).apply {
                    setImageResource(icon)
                    imageTintList = ColorStateList.valueOf(getColor(if (on) R.color.accent else R.color.text2))
                }, LinearLayout.LayoutParams(Ui.dp(context, 22f), Ui.dp(context, 22f)))
                addView(TextView(context).apply {
                    text = label; textSize = 11f; gravity = Gravity.CENTER
                    setTextColor(getColor(if (on) R.color.text else R.color.text2)); setPadding(0, Ui.dp(context, 2f), 0, 0)
                })
                setOnClickListener { if (page != p) show(p) }
            }, LinearLayout.LayoutParams(0, -2, 1f).apply { marginStart = Ui.dp(this@MainActivity, 3f); marginEnd = Ui.dp(this@MainActivity, 3f) })
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    /** dualscreen://pair?... links (QR scanned with the system camera) and results from ScanActivity. */
    private fun handleIntent(i: Intent?) {
        val text = i?.dataString ?: i?.getStringExtra(ScanActivity.EXTRA_RESULT) ?: return
        val info = PairInfo.parse(text)
        if (info == null) { toastDialog("El código QR no es de DualScreen."); return }
        connect(Target.Qr(info))
    }

    override fun onResume() {
        super.onResume()
        StreamHub.addListener(hubListener)
        Beacons.addListener(beaconListener)
        Beacons.start(this)
        lastSignature = ""
        render()
        tick.run()
    }

    override fun onPause() {
        StreamHub.removeListener(hubListener)
        Beacons.removeListener(beaconListener)
        Beacons.stop()
        pageBox.removeCallbacks(tick)
        super.onPause()
    }

    private val tick: Runnable = Runnable {
        render()
        pageBox.postDelayed(tick, 2000)
    }

    private fun scheduleRender() {
        if (renderPending) return
        renderPending = true
        runOnUiThread { renderPending = false; render() }
    }

    // ------------------------------------------------------------------ actions

    private fun connect(target: Target) {
        StreamHub.connect(this, target)
        startActivity(Intent(this, DisplayActivity::class.java))
    }

    /** Big button: connect to the most likely computer (linked and online first). */
    private fun connectDefault() {
        val saved = store.hosts()
        val seen = Beacons.current()
        val target = saved.firstOrNull { h -> seen.any { it.hostId == h.hostId } }?.let { Target.Paired(it) }
            ?: seen.singleOrNull()?.let { Target.Lan(it) }
            ?: saved.firstOrNull()?.let { Target.Paired(it) }
        if (target != null) connect(target) else show(Page.DEVICES)
    }

    private fun startScan() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.CAMERA), 1)
            return
        }
        startActivityForResult(Intent(this, ScanActivity::class.java), 1)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        if (requestCode == 1 && grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) startScan()
    }

    @Deprecated("Activity result API without androidx")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        if (requestCode == 1 && resultCode == RESULT_OK) handleIntent(data)
    }

    /** Code pairing, only needed when a PC has "connect without code" turned off. */
    private fun askCode(host: SeenHost?) {
        val target = host ?: Beacons.current().firstOrNull()
        if (target == null) {
            toastDialog("No se encontró ninguna computadora en esta red. Comprobá que DualScreen esté abierto en Windows y que ambos estén en la misma Wi‑Fi, o escaneá el código QR.")
            return
        }
        val input = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_NUMBER
            filters = arrayOf(InputFilter.LengthFilter(6))
            hint = "000000"; textSize = 28f; gravity = Gravity.CENTER; letterSpacing = 0.3f
            setTextColor(getColor(R.color.text)); setHintTextColor(getColor(R.color.text3))
            backgroundTintList = ColorStateList.valueOf(getColor(R.color.accent))
        }
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle("Código de ${target.name}")
            .setMessage("Escribí el código de 6 dígitos que muestra DualScreen en Windows (Conexión).")
            .setView(LinearLayout(this).apply { setPadding(60, 20, 60, 0); addView(input, LinearLayout.LayoutParams(-1, -2)) })
            .setPositiveButton("Conectar") { _, _ ->
                val code = input.text.toString()
                val dh = DiscoveredHost(target.hostId, target.name, target.address, target.port, target.fingerprint, "")
                if (code.length == 6) connect(Target.Code(dh, code)) else toastDialog("El código tiene 6 dígitos.")
            }
            .setNegativeButton("Cancelar", null)
            .show()
    }

    // ------------------------------------------------------------------ pages

    private fun render() {
        updateBadge.visibility = if (pendingRelease != null) View.VISIBLE else View.GONE
        // Rebuild only when something visible changed (keeps scroll position and open menus stable).
        val sig = signature()
        if (sig == lastSignature) return
        lastSignature = sig
        val y = scroll.scrollY
        pageBox.removeAllViews()
        when (page) {
            Page.HOME -> buildHome()
            Page.DEVICES -> buildDevices()
            Page.DISPLAY -> buildDisplay()
            Page.SETTINGS -> buildSettings()
        }
        scroll.post { scroll.scrollTo(0, y) }
    }

    private fun signature(): String {
        val base = "${page}|${StreamHub.state}|${StreamHub.message}|${StreamHub.hostName}|${pendingRelease?.version}|$updateText|$updateBusy"
        return when (page) {
            Page.HOME -> base + "|${StreamHub.config?.width}x${StreamHub.config?.height}|${"%.0f".format(StreamHub.fps)}|" +
                "${StreamHub.latencyMs.toInt()}|${(StreamHub.recvKbps / 500).toInt()}|${StreamHub.hostState?.optString("quality")}"
            Page.DEVICES -> base + "|" + Beacons.current().joinToString { it.hostId + it.name } + "|" + store.hosts().joinToString { it.hostId + it.name }
            Page.DISPLAY -> base + "|" + StreamHub.hostState?.toString()
            Page.SETTINGS -> base + "|${store.audio}|${store.showStats}|${store.controlMode}|${store.stretch}"
        }
    }

    private fun buildHome() {
        val box = pageBox
        box.addView(Ui.title(this, "Inicio"))
        val st = StreamHub.state

        // Status card (Windows: halo + dot + status + device + big button).
        val card = Ui.card(this).apply { setPadding(Ui.dp(context, 18f), Ui.dp(context, 18f), Ui.dp(context, 18f), Ui.dp(context, 18f)) }
        val top = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        top.addView(FrameLayout(this).apply {
            background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(getColor(R.color.accent_soft)) }
            addView(ImageView(context).apply { setImageResource(R.drawable.ic_nav_display); imageTintList = ColorStateList.valueOf(getColor(R.color.text)) },
                FrameLayout.LayoutParams(Ui.dp(context, 26f), Ui.dp(context, 26f), Gravity.CENTER))
        }, LinearLayout.LayoutParams(Ui.dp(this@MainActivity, 56f), Ui.dp(this@MainActivity, 56f)).apply { rightMargin = Ui.dp(this@MainActivity, 16f) })
        val texts = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        texts.addView(LinearLayout(this).apply {
            gravity = Gravity.CENTER_VERTICAL
            addView(Ui.dot(context, when (st) {
                LinkState.CONNECTED -> R.color.success; LinkState.CONNECTING -> R.color.warning
                LinkState.ERROR -> R.color.error; else -> R.color.text3
            }).apply { (layoutParams as LinearLayout.LayoutParams).rightMargin = Ui.dp(context, 8f) })
            addView(TextView(context).apply {
                text = when (st) {
                    LinkState.CONNECTED -> "Conectado"; LinkState.CONNECTING -> "Conectando…"
                    LinkState.ERROR -> "No se pudo conectar"; else -> "Sin conectar"
                }
                textSize = 20f; setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL)); setTextColor(getColor(R.color.text))
            })
        })
        texts.addView(Ui.caption(this, when (st) {
            LinkState.CONNECTED -> "${StreamHub.hostName} · ${StreamHub.transportDesc}"
            LinkState.CONNECTING -> StreamHub.message
            LinkState.ERROR -> ""
            else -> "Elegí una computadora en Equipos o tocá Conectar."
        }).apply { setPadding(0, Ui.dp(context, 4f), 0, 0) })
        if (st == LinkState.ERROR) texts.addView(Ui.caption(this, StreamHub.message, R.color.error).apply { setPadding(0, Ui.dp(context, 4f), 0, 0) })
        top.addView(texts, LinearLayout.LayoutParams(0, -2, 1f))
        card.addView(top)

        val buttons = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; setPadding(0, Ui.dp(context, 16f), 0, 0) }
        if (StreamHub.isActive) {
            buttons.addView(Ui.button(this, "Mostrar pantalla", true) { startActivity(Intent(this, DisplayActivity::class.java)) },
                LinearLayout.LayoutParams(0, Ui.dp(this@MainActivity, 48f), 1f).apply { rightMargin = Ui.dp(this@MainActivity, 8f) })
            buttons.addView(Ui.button(this, "Desconectar", false) { StreamHub.disconnect() }, LinearLayout.LayoutParams(0, Ui.dp(this@MainActivity, 48f), 1f))
        } else {
            buttons.addView(Ui.button(this, "Conectar", true) { connectDefault() }, LinearLayout.LayoutParams(-1, Ui.dp(this@MainActivity, 52f)))
        }
        card.addView(buttons)
        box.addView(card)

        // Live stats (Windows "Transmisión" grid).
        box.addView(Ui.subtitle(this, "Transmisión"))
        val cfg = StreamHub.config
        val connected = st == LinkState.CONNECTED && cfg != null
        val lat = StreamHub.latencyMs
        val rtt = StreamHub.rttMs
        val tiles = listOf(
            "Resolución" to (if (connected) "${cfg!!.width} × ${cfg.height}" else "—"),
            "Frecuencia" to (if (connected) "${cfg!!.fps} Hz" else "—"),
            "Latencia" to (if (!connected) "—" else if (!lat.isNaN()) "%.0f ms".format(lat) else if (!rtt.isNaN()) "≈ %.0f ms".format(rtt / 2 + StreamHub.decodeMs) else "midiendo…"),
            "Velocidad" to (if (connected) "%.1f Mbps".format(StreamHub.recvKbps / 1000f) else "—"),
            "FPS actuales" to (if (connected) "%.0f".format(StreamHub.fps) else "—"),
            "Códec" to (if (connected) when (cfg!!.mime) { "video/hevc" -> "HEVC"; "video/av01" -> "AV1"; else -> "H.264" } else "—"),
        )
        for (i in tiles.indices step 2) {
            val rowL = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            rowL.addView(Ui.statTile(this, tiles[i].first, tiles[i].second), LinearLayout.LayoutParams(0, -2, 1f).apply { rightMargin = Ui.dp(this@MainActivity, 6f); bottomMargin = Ui.dp(this@MainActivity, 6f) })
            rowL.addView(Ui.statTile(this, tiles[i + 1].first, tiles[i + 1].second), LinearLayout.LayoutParams(0, -2, 1f).apply { bottomMargin = Ui.dp(this@MainActivity, 6f) })
            box.addView(rowL)
        }
        if (connected) box.addView(Ui.caption(this, "Modo: ${if (cfg!!.mode == "mirror") "espejo" else "pantalla extendida"} · el punto del borde superior derecho de la pantalla abre los ajustes.").apply { setPadding(Ui.dp(context, 2f), Ui.dp(context, 6f), 0, 0) })
    }

    private data class Row(val hostId: String, val name: String, val saved: PairedHost?, val seen: SeenHost?)

    private fun buildDevices() {
        val box = pageBox
        box.addView(Ui.title(this, "Equipos"))
        box.addView(Ui.caption(this, "Las computadoras con DualScreen abierto en tu red aparecen solas. Tocá una para conectarte, sin código; queda guardada para la próxima.").apply { setPadding(Ui.dp(context, 2f), 0, 0, Ui.dp(context, 12f)) })

        val saved = store.hosts()
        val seen = Beacons.current()
        val rows = LinkedHashMap<String, Row>()
        for (s in seen) rows[s.hostId] = Row(s.hostId, s.name, saved.firstOrNull { it.hostId == s.hostId }, s)
        for (h in saved) if (h.hostId !in rows) rows[h.hostId] = Row(h.hostId, h.name, h, null)

        if (rows.isEmpty()) box.addView(Ui.card(this).apply {
            addView(Ui.body(this@MainActivity, "Buscando computadoras…"))
            addView(Ui.caption(this@MainActivity, "Abrí DualScreen en la PC con Windows y verificá que el teléfono esté en la misma Wi‑Fi.").apply { setPadding(0, Ui.dp(context, 4f), 0, 0) })
        })
        for (r in rows.values) box.addView(deviceCard(r))

        box.addView(Ui.subtitle(this, "Otras formas de conectar"))
        val btns = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        btns.addView(Ui.button(this, "Escanear QR", false) { startScan() }, LinearLayout.LayoutParams(0, Ui.dp(this@MainActivity, 46f), 1f).apply { rightMargin = Ui.dp(this@MainActivity, 8f) })
        btns.addView(Ui.button(this, "Usar código", false) { askCode(null) }, LinearLayout.LayoutParams(0, Ui.dp(this@MainActivity, 46f), 1f))
        box.addView(btns)
        box.addView(Ui.caption(this, "El QR sirve también desde otra red (Internet). El código solo hace falta si la PC tiene desactivado «Conectar sin código».").apply { setPadding(Ui.dp(context, 2f), Ui.dp(context, 10f), 0, 0) })
    }

    private fun deviceCard(r: Row): View {
        val connectedHere = StreamHub.isActive && StreamHub.hostName == r.name
        val online = r.seen != null
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            setBackgroundResource(if (connectedHere) R.drawable.card_selected else R.drawable.card)
            setPadding(Ui.dp(context, 14f), Ui.dp(context, 12f), Ui.dp(context, 4f), Ui.dp(context, 12f))
            layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = Ui.dp(context, 6f) }
        }
        card.addView(FrameLayout(this).apply {
            background = Ui.shape(context, getColor(R.color.accent_soft), 10f)
            addView(ImageView(context).apply { setImageResource(R.drawable.ic_windows); imageTintList = ColorStateList.valueOf(getColor(R.color.text)) },
                FrameLayout.LayoutParams(Ui.dp(context, 22f), Ui.dp(context, 22f), Gravity.CENTER))
        }, LinearLayout.LayoutParams(Ui.dp(this@MainActivity, 44f), Ui.dp(this@MainActivity, 44f)).apply { rightMargin = Ui.dp(this@MainActivity, 14f) })
        val texts = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        texts.addView(Ui.body(this, r.name, 16f).apply { setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL)); maxLines = 1 })
        texts.addView(LinearLayout(this).apply {
            gravity = Gravity.CENTER_VERTICAL; setPadding(0, Ui.dp(context, 3f), 0, 0)
            addView(Ui.dot(context, if (online || connectedHere) R.color.success else R.color.text3, 8f).apply { (layoutParams as LinearLayout.LayoutParams).rightMargin = Ui.dp(context, 6f) })
            addView(Ui.caption(context, when {
                connectedHere && StreamHub.state == LinkState.CONNECTED -> "Conectado"
                connectedHere -> "Conectando…"
                online && r.saved != null -> "Disponible · vinculado"
                online -> "Disponible en tu red · ${r.seen!!.address}"
                else -> "Vinculado · fuera de la red"
            }))
        })
        card.addView(texts, LinearLayout.LayoutParams(0, -2, 1f))
        card.addView(TextView(this).apply {
            text = "⋮"; textSize = 22f; gravity = Gravity.CENTER; setTextColor(getColor(R.color.text2))
            setOnClickListener { v -> menu(v, r) }
        }, LinearLayout.LayoutParams(Ui.dp(this@MainActivity, 40f), Ui.dp(this@MainActivity, 44f)))
        card.setOnClickListener {
            if (connectedHere) startActivity(Intent(this, DisplayActivity::class.java))
            else if (r.saved != null) connect(Target.Paired(r.saved))
            else if (r.seen != null) connect(Target.Lan(r.seen))
        }
        return card
    }

    private fun menu(anchor: View, r: Row) {
        val pm = PopupMenu(this, anchor)
        if (r.seen != null) pm.menu.add(0, 1, 0, "Conectar con código")
        if (r.saved != null) pm.menu.add(0, 2, 1, "Olvidar")
        pm.setOnMenuItemClickListener {
            when (it.itemId) {
                1 -> askCode(r.seen)
                2 -> AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
                    .setMessage("¿Olvidar ${r.name}? Para volver a usarla, tocála de nuevo en la lista.")
                    .setPositiveButton("Olvidar") { _, _ -> store.forget(r.hostId); lastSignature = ""; render() }
                    .setNegativeButton("Cancelar", null).show()
            }
            true
        }
        pm.show()
    }

    private fun buildDisplay() {
        val box = pageBox
        box.addView(Ui.title(this, "Pantalla"))
        val st = StreamHub.hostState
        if (StreamHub.state != LinkState.CONNECTED || st == null) {
            box.addView(Ui.card(this).apply {
                addView(Ui.body(this@MainActivity, if (StreamHub.state == LinkState.CONNECTED) "Esta PC tiene una versión de DualScreen sin ajustes remotos" else "Conectate a una computadora"))
                addView(Ui.caption(this@MainActivity, if (StreamHub.state == LinkState.CONNECTED) "Actualizá DualScreen en la PC para cambiar el modo y la calidad desde el teléfono."
                    else "Acá elegís pantalla extendida o espejo, el monitor, la resolución, la frecuencia, la calidad y la prioridad. Se aplican en la PC, igual que desde DualScreen para Windows.").apply { setPadding(0, Ui.dp(context, 4f), 0, 0) })
            })
            if (StreamHub.state != LinkState.CONNECTED)
                box.addView(Ui.button(this, "Ir a Equipos", true) { show(Page.DEVICES) }, LinearLayout.LayoutParams(-1, Ui.dp(this@MainActivity, 46f)).apply { topMargin = Ui.dp(this@MainActivity, 8f) })
            return
        }
        box.addView(Ui.caption(this, "Ajustes de ${st.optString("hostName").ifEmpty { StreamHub.hostName }}: se aplican en la PC al instante.").apply { setPadding(Ui.dp(context, 2f), 0, 0, Ui.dp(context, 14f)) })
        HostSettingsView.populate(this, box, st)
    }

    private fun buildSettings() {
        val box = pageBox
        box.addView(Ui.title(this, "Configuración"))
        box.addView(Ui.subtitle(this, "General").apply { setPadding(Ui.dp(context, 2f), 0, 0, Ui.dp(context, 8f)) })
        box.addView(Ui.toggleRow(this, "Sonido de la PC", "Reproduce en el teléfono lo que suena en la computadora.", store.audio) {
            store.audio = it
            StreamHub.sendHostSettings(JSONObject().put("audio", it))
        })
        box.addView(Ui.toggleRow(this, "Estadísticas en pantalla", "Resolución, fps, latencia real y velocidad en una esquina.", store.showStats) { store.showStats = it })
        box.addView(Ui.choiceRow(this, "Control", "Táctil: el dedo es el puntero. Mouse: la pantalla es un touchpad (mover, tocar = clic, dos dedos = clic derecho o desplazar).",
            listOf("touch" to "Táctil directo", "mouse" to "Mouse (touchpad)"), store.controlMode) { store.controlMode = it; lastSignature = ""; render() })
        box.addView(Ui.toggleRow(this, "Estirar la pantalla", "Ocupa todo el teléfono aunque la resolución de la PC sea otra.", store.stretch) { store.stretch = it })

        box.addView(Ui.subtitle(this, "Actualizaciones"))
        val upd = Ui.card(this)
        upd.addView(Ui.body(this, updateText, 17f))
        pendingRelease?.let { rel ->
            upd.addView(Ui.caption(this, "Tenés la ${BuildConfigCompat.versionName(this)}." + if (rel.notes.isNotBlank()) "\n\n" + rel.notes.take(600) else "").apply { setPadding(0, Ui.dp(context, 4f), 0, 0) })
        }
        if (updateBusy && updateProgress >= 0) upd.addView(ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100; progress = updateProgress; progressTintList = ColorStateList.valueOf(getColor(R.color.accent))
        }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = Ui.dp(this@MainActivity, 8f) })
        upd.addView(Ui.button(this, if (pendingRelease != null) "Actualizar ahora" else "Buscar actualizaciones", pendingRelease != null) { onUpdateButton() }
            .apply { isEnabled = !updateBusy; alpha = if (updateBusy) 0.5f else 1f }, LinearLayout.LayoutParams(-1, Ui.dp(this@MainActivity, 44f)).apply { topMargin = Ui.dp(this@MainActivity, 12f) })
        box.addView(upd)

        box.addView(Ui.subtitle(this, "Acerca de"))
        box.addView(Ui.card(this).apply {
            addView(Ui.caption(this@MainActivity, "DualScreen ${BuildConfigCompat.versionName(this@MainActivity)} · Protocolo v1 · ID ${store.deviceId.take(8)}"))
        })
    }

    // ------------------------------------------------------------------ updates

    private var pendingRelease: ApkRelease? = null
    @Volatile private var updateBusy = false
    private var updateText = "DualScreen está actualizado"
    private var updateProgress = -1

    private fun onUpdateButton() {
        val rel = pendingRelease
        if (rel == null) checkForUpdate(userInitiated = true) else installUpdate(rel)
    }

    private fun checkForUpdate(userInitiated: Boolean) {
        if (updateBusy) return
        updateBusy = true
        if (userInitiated) updateText = "Buscando actualizaciones…"
        render()
        Thread {
            val result = runCatching { AppUpdater.check(this) }
            runOnUiThread {
                updateBusy = false
                result.onSuccess { rel ->
                    pendingRelease = rel
                    updateText = if (rel != null) "Nueva versión disponible: ${rel.version}" else "DualScreen está actualizado"
                }.onFailure { e -> if (userInitiated) updateText = "No se pudo comprobar: ${e.message}" }
                render()
            }
        }.start()
    }

    private fun installUpdate(rel: ApkRelease) {
        if (updateBusy) return
        if (!AppUpdater.ensureInstallPermission(this)) {
            toastDialog("Permití que DualScreen instale aplicaciones (se abrió la pantalla de Ajustes) y volvé a tocar «Actualizar ahora».")
            return
        }
        updateBusy = true
        updateProgress = 0
        updateText = "Descargando la versión ${rel.version}…"
        render()
        Thread {
            val result = runCatching { AppUpdater.downloadAndInstall(this, rel) { p -> runOnUiThread { updateProgress = p; lastSignature = ""; render() } } }
            runOnUiThread {
                updateBusy = false
                updateProgress = -1
                updateText = result.fold({ "Confirmá la instalación en la ventana de Android" }, { e -> "No se pudo actualizar: ${e.message}" })
                render()
            }
        }.start()
    }

    private fun toastDialog(msg: String) {
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert).setMessage(msg).setPositiveButton("Aceptar", null).show()
    }
}
