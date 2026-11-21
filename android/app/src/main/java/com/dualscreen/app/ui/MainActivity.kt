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
import android.widget.Button
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.Switch
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
 * Home: every computer with DualScreen on the network (and the ones already linked) by name; one tap connects.
 * No code is needed on the local network unless the PC asks for it.
 */
class MainActivity : Activity() {
    private lateinit var store: Store
    private val hubListener: () -> Unit = { render() }
    private val beaconListener: (SeenHost) -> Unit = { scheduleRender() }
    private var renderPending = false

    private fun <T : View> view(id: Int): T = findViewById<T>(id)!!

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        store = Store(this)

        view<View>(R.id.scanButton).setOnClickListener { startScan() }
        view<View>(R.id.codeButton).setOnClickListener { askCode(null) }
        view<View>(R.id.startCircle).setOnClickListener { onStartTap() }
        view<View>(R.id.disconnectLink).setOnClickListener { StreamHub.disconnect() }

        val audio = view<Switch>(R.id.audioSwitch)
        val stats = view<Switch>(R.id.statsSwitch)
        styleSwitch(audio); styleSwitch(stats)
        audio.isChecked = store.audio
        stats.isChecked = store.showStats
        audio.setOnCheckedChangeListener { _, c ->
            store.audio = c
            StreamHub.sendHostSettings(JSONObject().put("audio", c))
        }
        stats.setOnCheckedChangeListener { _, c -> store.showStats = c }
        view<View>(R.id.audioRow).setOnClickListener { audio.toggle() }
        view<View>(R.id.statsRow).setOnClickListener { stats.toggle() }

        view<TextView>(R.id.versionText).text = "DualScreen ${BuildConfigCompat.versionName(this)}"
        view<Button>(R.id.updateButton).setOnClickListener { onUpdateButton() }
        checkForUpdate(userInitiated = false)

        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 2)
        handleIntent(intent)
    }

    private fun styleSwitch(s: Switch) {
        s.thumbTintList = ColorStateList.valueOf(getColor(R.color.text))
        s.trackTintList = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
            intArrayOf(getColor(R.color.accent), getColor(R.color.surface_high)))
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    /** dualscreen://pair?... links (QR scanned with the system camera) and results from ScanActivity. */
    private fun handleIntent(i: Intent?) {
        val text = i?.dataString ?: i?.getStringExtra(ScanActivity.EXTRA_RESULT) ?: return
        val info = PairInfo.parse(text)
        if (info == null) {
            toastDialog("El código QR no es de DualScreen.")
            return
        }
        connect(Target.Qr(info))
    }

    override fun onResume() {
        super.onResume()
        StreamHub.addListener(hubListener)
        Beacons.addListener(beaconListener)
        Beacons.start(this)
        render()
        // Beacons expire after 10 s: refresh the list now and then even without new packets.
        tick.run()
    }

    override fun onPause() {
        StreamHub.removeListener(hubListener)
        Beacons.removeListener(beaconListener)
        Beacons.stop()
        view<View>(R.id.computerList).removeCallbacks(tick)
        super.onPause()
    }

    private val tick: Runnable = Runnable {
        render()
        view<View>(R.id.computerList).postDelayed(tick, 3000)
    }

    private fun scheduleRender() {
        if (renderPending) return
        renderPending = true
        runOnUiThread { renderPending = false; render() }
    }

    private fun connect(target: Target) {
        StreamHub.connect(this, target)
        startActivity(Intent(this, DisplayActivity::class.java))
    }

    /** Big circle: show the stream if connected, otherwise connect to the most likely computer. */
    private fun onStartTap() {
        if (StreamHub.isActive) {
            startActivity(Intent(this, DisplayActivity::class.java))
            return
        }
        val saved = store.hosts()
        val seen = Beacons.current()
        val target = saved.firstOrNull { h -> seen.any { it.hostId == h.hostId } }?.let { Target.Paired(it) }
            ?: saved.firstOrNull()?.let { Target.Paired(it) }
            ?: seen.singleOrNull()?.let { Target.Lan(it) }
        if (target != null) connect(target)
        else if (seen.isEmpty()) toastDialog("No se encontró ninguna computadora con DualScreen en esta red. Abrí DualScreen en Windows y verificá que estén en la misma Wi‑Fi.")
        else toastDialog("Tocá la computadora que querés usar.")
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
            hint = "000000"
            textSize = 28f
            gravity = Gravity.CENTER
            letterSpacing = 0.3f
            setTextColor(getColor(R.color.text))
            setHintTextColor(getColor(R.color.text3))
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

    // ------------------------------------------------------------------ list

    private data class Row(val hostId: String, val name: String, val saved: PairedHost?, val seen: SeenHost?)

    private fun render() {
        val st = StreamHub.state
        val active = StreamHub.isActive
        val circle = view<TextView>(R.id.startCircle)
        circle.text = if (st == LinkState.CONNECTED) "Mostrar" else if (st == LinkState.CONNECTING) "…" else "Iniciar"
        view<TextView>(R.id.startCaption).text = when (st) {
            LinkState.CONNECTED -> "Conectado a ${StreamHub.hostName}"
            LinkState.CONNECTING -> StreamHub.message
            LinkState.ERROR -> StreamHub.message
            else -> "Tocá una computadora o el círculo"
        }
        view<View>(R.id.disconnectLink).visibility = if (active) View.VISIBLE else View.GONE

        val saved = store.hosts()
        val seen = Beacons.current()
        val rows = LinkedHashMap<String, Row>()
        for (s in seen) rows[s.hostId] = Row(s.hostId, s.name, saved.firstOrNull { it.hostId == s.hostId }, s)
        for (h in saved) if (h.hostId !in rows) rows[h.hostId] = Row(h.hostId, h.name, h, null)

        val list = view<LinearLayout>(R.id.computerList)
        list.removeAllViews()
        rows.values.forEachIndexed { i, r -> list.addView(card(r, i)) }
        view<View>(R.id.listEmpty).visibility = if (rows.isEmpty()) View.VISIBLE else View.GONE
    }

    private val tileColors = intArrayOf(0xFF1E3A5F.toInt(), 0xFF0E4D64.toInt(), 0xFF1F4E79.toInt(), 0xFF134E4A.toInt())

    private fun card(r: Row, index: Int): View {
        val d = resources.displayMetrics.density
        val connectedHere = StreamHub.isActive && StreamHub.hostName == r.name
        val online = r.seen != null
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setBackgroundResource(if (connectedHere) R.drawable.card_selected else R.drawable.card)
            setPadding((14 * d).toInt(), (14 * d).toInt(), (6 * d).toInt(), (14 * d).toInt())
            layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = (12 * d).toInt() }

            addView(ImageView(context).apply {
                setImageResource(R.drawable.ic_windows)
                val pad = (14 * d).toInt()
                setPadding(pad, pad, pad, pad)
                background = GradientDrawable().apply { setColor(tileColors[index % tileColors.size]); cornerRadius = 16 * d }
            }, LinearLayout.LayoutParams((56 * d).toInt(), (56 * d).toInt()).apply { rightMargin = (14 * d).toInt() })

            addView(LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL
                addView(LinearLayout(context).apply {
                    gravity = Gravity.CENTER_VERTICAL
                    addView(View(context).apply {
                        background = GradientDrawable().apply {
                            shape = GradientDrawable.OVAL
                            setColor(getColor(if (online || connectedHere) R.color.success else R.color.text3))
                        }
                    }, LinearLayout.LayoutParams((9 * d).toInt(), (9 * d).toInt()).apply { rightMargin = (8 * d).toInt() })
                    addView(TextView(context).apply {
                        text = r.name; textSize = 19f; setTypeface(typeface, Typeface.BOLD); setTextColor(getColor(R.color.text))
                        maxLines = 1
                    })
                })
                addView(TextView(context).apply {
                    text = when {
                        connectedHere && StreamHub.state == LinkState.CONNECTED -> "Conectada · ${StreamHub.transportDesc}"
                        connectedHere -> "Conectando…"
                        online -> "Disponible en tu red"
                        else -> "Tocá para conectarte"
                    }
                    textSize = 14f; setTextColor(getColor(R.color.text2)); maxLines = 1
                    setPadding(0, (2 * d).toInt(), 0, 0)
                })
            }, LinearLayout.LayoutParams(0, -2, 1f))

            addView(TextView(context).apply {
                text = "⋮"; textSize = 24f; gravity = Gravity.CENTER; setTextColor(getColor(R.color.text))
                setOnClickListener { v -> menu(v, r) }
            }, LinearLayout.LayoutParams((44 * d).toInt(), (48 * d).toInt()))

            setOnClickListener { tap(r) }
        }
    }

    private fun tap(r: Row) {
        if (StreamHub.isActive && StreamHub.hostName == r.name) {
            startActivity(Intent(this, DisplayActivity::class.java))
            return
        }
        when {
            r.saved != null -> connect(Target.Paired(r.saved))
            r.seen != null -> connect(Target.Lan(r.seen))
        }
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
                    .setPositiveButton("Olvidar") { _, _ -> store.forget(r.hostId); render() }
                    .setNegativeButton("Cancelar", null).show()
            }
            true
        }
        pm.show()
    }

    // ------------------------------------------------------------------ updates

    private var pendingRelease: ApkRelease? = null
    @Volatile private var updateBusy = false

    private fun onUpdateButton() {
        val rel = pendingRelease
        if (rel == null) checkForUpdate(userInitiated = true) else installUpdate(rel)
    }

    private fun checkForUpdate(userInitiated: Boolean) {
        if (updateBusy) return
        updateBusy = true
        val text = view<TextView>(R.id.updateText)
        val button = view<Button>(R.id.updateButton)
        if (userInitiated) { text.text = "Buscando actualizaciones…"; button.isEnabled = false }
        Thread {
            val result = runCatching { AppUpdater.check(this) }
            runOnUiThread {
                updateBusy = false
                button.isEnabled = true
                result.onSuccess { rel ->
                    pendingRelease = rel
                    val detail = view<TextView>(R.id.updateDetail)
                    if (rel != null) {
                        text.text = "Nueva versión disponible: ${rel.version}"
                        detail.text = "Tenés la ${BuildConfigCompat.versionName(this)}." +
                            if (rel.notes.isNotBlank()) "\n\n" + rel.notes.take(600) else ""
                        detail.visibility = View.VISIBLE
                        button.text = "Actualizar ahora"
                    } else {
                        text.text = "DualScreen está actualizado"
                        detail.visibility = View.GONE
                        button.text = "Buscar actualizaciones"
                    }
                }.onFailure { e ->
                    if (userInitiated) text.text = "No se pudo comprobar: ${e.message}"
                }
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
        val text = view<TextView>(R.id.updateText)
        val button = view<Button>(R.id.updateButton)
        val bar = view<android.widget.ProgressBar>(R.id.updateProgress)
        button.isEnabled = false
        bar.progress = 0
        bar.visibility = View.VISIBLE
        text.text = "Descargando la versión ${rel.version}…"
        Thread {
            val result = runCatching { AppUpdater.downloadAndInstall(this, rel) { p -> runOnUiThread { bar.progress = p } } }
            runOnUiThread {
                updateBusy = false
                button.isEnabled = true
                bar.visibility = View.GONE
                result.onSuccess { text.text = "Confirmá la instalación en la ventana de Android" }
                    .onFailure { e -> text.text = "No se pudo actualizar: ${e.message}" }
            }
        }.start()
    }

    private fun toastDialog(msg: String) {
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert).setMessage(msg).setPositiveButton("Aceptar", null).show()
    }
}
