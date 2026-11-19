package com.dualscreen.app.ui

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.os.Build
import android.os.Bundle
import android.text.InputFilter
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.RadioGroup
import android.widget.TextView
import com.dualscreen.app.LinkState
import com.dualscreen.app.R
import com.dualscreen.app.StreamHub
import com.dualscreen.app.Target
import com.dualscreen.app.BuildConfigCompat
import com.dualscreen.app.data.PairedHost
import com.dualscreen.app.data.Store
import com.dualscreen.app.net.DiscoveredHost
import com.dualscreen.app.net.Discovery
import com.dualscreen.app.net.PairInfo
import com.dualscreen.app.update.ApkRelease
import com.dualscreen.app.update.AppUpdater

class MainActivity : Activity() {
    private lateinit var store: Store
    private var discovered: List<DiscoveredHost> = emptyList()
    @Volatile private var scanning = false
    private val listener: () -> Unit = { render() }

    private val statusDot by lazy { view<View>(R.id.statusDot) }
    private val statusText by lazy { view<TextView>(R.id.statusText) }
    private val statusDetail by lazy { view<TextView>(R.id.statusDetail) }
    private val connectButton by lazy { view<Button>(R.id.connectButton) }

    private fun <T : View> view(id: Int): T = findViewById<T>(id)!!

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        store = Store(this)

        connectButton.setOnClickListener {
            if (StreamHub.isActive) StreamHub.disconnect() else connectDefault()
        }
        view<Button>(R.id.scanButton).setOnClickListener { startScan() }
        view<Button>(R.id.codeButton).setOnClickListener { askCode() }
        view<Button>(R.id.showButton).setOnClickListener { startActivity(Intent(this, DisplayActivity::class.java)) }

        val group = view<RadioGroup>(R.id.qualityGroup)
        val ids = mapOf("auto" to R.id.qAuto, "maximum" to R.id.qMax, "high" to R.id.qHigh, "balanced" to R.id.qBalanced, "saver" to R.id.qSaver)
        group.check(ids[store.quality] ?: R.id.qAuto)
        group.setOnCheckedChangeListener { _, id ->
            store.quality = ids.entries.first { it.value == id }.key
            StreamHub.setQuality(store.quality)
        }
        view<TextView>(R.id.versionText).text = "DualScreen ${BuildConfigCompat.versionName(this)}"
        view<Button>(R.id.updateButton).setOnClickListener { onUpdateButton() }
        checkForUpdate(userInitiated = false)

        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 2)
        handleIntent(intent)
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
        StreamHub.addListener(listener)
        render()
        refreshDiscovery()
    }

    override fun onPause() {
        StreamHub.removeListener(listener)
        super.onPause()
    }

    private fun connect(target: Target) {
        StreamHub.connect(this, target)
        startActivity(Intent(this, DisplayActivity::class.java))
    }

    private fun connectDefault() {
        val hosts = store.hosts()
        val pairedOnline = hosts.firstOrNull { h -> discovered.any { it.hostId == h.hostId } }
        when {
            pairedOnline != null -> connect(Target.Paired(pairedOnline))
            hosts.isNotEmpty() -> connect(Target.Paired(hosts.first()))
            discovered.size == 1 -> askCode(discovered.first())
            else -> startScan()
        }
    }

    private fun refreshDiscovery() {
        if (scanning) return
        scanning = true
        view<TextView>(R.id.foundEmpty).text = "Buscando…"
        Thread {
            val found = runCatching { Discovery.scan(this) }.getOrDefault(emptyList())
            runOnUiThread {
                scanning = false
                discovered = found
                render()
            }
        }.start()
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

    private fun askCode(host: DiscoveredHost? = null) {
        val target = host ?: discovered.firstOrNull()
        if (target == null) {
            toastDialog("No se encontró ningún PC en esta red. Comprueba que DualScreen esté abierto en Windows y que ambos estén en la misma Wi-Fi, o escanea el código QR.")
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
            .setMessage("Escribe el código de 6 dígitos que muestra DualScreen en Windows (Conexión).")
            .setView(LinearLayout(this).apply { setPadding(60, 20, 60, 0); addView(input, LinearLayout.LayoutParams(-1, -2)) })
            .setPositiveButton("Conectar") { _, _ ->
                val code = input.text.toString()
                if (code.length == 6) connect(Target.Code(target, code)) else toastDialog("El código tiene 6 dígitos.")
            }
            .setNegativeButton("Cancelar", null)
            .show()
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
                        detail.text = "Tienes la ${BuildConfigCompat.versionName(this)}." +
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
            toastDialog("Permite que DualScreen instale aplicaciones (se abrió la pantalla de Ajustes) y vuelve a pulsar «Actualizar ahora».")
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
                result.onSuccess { text.text = "Confirma la instalación en la ventana de Android" }
                    .onFailure { e -> text.text = "No se pudo actualizar: ${e.message}" }
            }
        }.start()
    }

    private fun toastDialog(msg: String) {
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert).setMessage(msg).setPositiveButton("Aceptar", null).show()
    }

    private fun render() {
        val st = StreamHub.state
        statusText.text = when (st) {
            LinkState.CONNECTED -> "Conectado"
            LinkState.CONNECTING -> "Conectando…"
            LinkState.ERROR -> "Error"
            LinkState.DISCONNECTED -> "Desconectado"
        }
        statusDetail.text = when (st) {
            LinkState.CONNECTED -> StreamHub.hostName
            LinkState.DISCONNECTED -> "Abre DualScreen en tu PC con Windows y pulsa Conectar."
            else -> StreamHub.message
        }
        statusDot.backgroundTintList = ColorStateList.valueOf(getColor(when (st) {
            LinkState.CONNECTED -> R.color.success
            LinkState.CONNECTING -> R.color.warning
            LinkState.ERROR -> R.color.error
            else -> R.color.text3
        }))
        connectButton.text = if (StreamHub.isActive) "Desconectar" else "Conectar"

        val info = view<View>(R.id.infoCard)
        info.visibility = if (st == LinkState.CONNECTED) View.VISIBLE else View.GONE
        StreamHub.config?.let { c ->
            view<TextView>(R.id.infoResolution).text = "${c.width} × ${c.height} · ${c.fps} Hz"
        }
        view<TextView>(R.id.infoFps).text = "%.0f".format(StreamHub.fps)
        val rtt = StreamHub.rttMs
        view<TextView>(R.id.infoLatency).text = if (rtt.isNaN()) "—" else "≈ %.0f ms".format(rtt / 2 + StreamHub.decodeMs)
        view<TextView>(R.id.infoBitrate).text = "%.1f Mbps".format(StreamHub.recvKbps / 1000f)
        view<TextView>(R.id.infoTransport).text = StreamHub.transportDesc

        renderDiscovered()
        renderPaired()
    }

    private fun renderDiscovered() {
        val list = view<LinearLayout>(R.id.foundList)
        list.removeAllViews()
        val paired = store.hosts().map { it.hostId }.toSet()
        for (h in discovered) {
            list.addView(row(h.name, if (h.hostId in paired) "Emparejado · ${h.address}" else "Sin emparejar · ${h.address}", "Conectar") {
                val p = store.host(h.hostId)
                if (p != null) connect(Target.Paired(p)) else askCode(h)
            })
        }
        view<TextView>(R.id.foundEmpty).apply {
            visibility = if (discovered.isEmpty()) View.VISIBLE else View.GONE
            if (!scanning) text = "No se encontraron PCs en esta red."
        }
    }

    private fun renderPaired() {
        val list = view<LinearLayout>(R.id.pairedList)
        list.removeAllViews()
        val hosts = store.hosts()
        for (h in hosts) list.addView(pairedRow(h))
        view<View>(R.id.pairedEmpty).visibility = if (hosts.isEmpty()) View.VISIBLE else View.GONE
    }

    private fun pairedRow(h: PairedHost): View = row(h.name, if (h.signalingUrl != null) "Red local y remota" else "Red local", "Olvidar") {
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setMessage("¿Olvidar ${h.name}? Tendrás que volver a emparejar.")
            .setPositiveButton("Olvidar") { _, _ -> store.forget(h.hostId); render() }
            .setNegativeButton("Cancelar", null).show()
    }.also { v -> v.setOnClickListener { connect(Target.Paired(h)) } }

    private fun row(title: String, subtitle: String, action: String, onAction: () -> Unit): View {
        val d = resources.displayMetrics.density
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setBackgroundResource(R.drawable.card)
            setPadding((16 * d).toInt(), (12 * d).toInt(), (12 * d).toInt(), (12 * d).toInt())
            layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = (8 * d).toInt() }
            addView(LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL
                addView(TextView(context).apply { text = title; setTextColor(getColor(R.color.text)); textSize = 16f })
                addView(TextView(context).apply { text = subtitle; setTextColor(getColor(R.color.text2)); textSize = 12f })
            }, LinearLayout.LayoutParams(0, -2, 1f))
            addView(Button(context, null, 0, R.style.Button_Tonal).apply {
                text = action
                minHeight = (40 * d).toInt()
                setPadding((18 * d).toInt(), 0, (18 * d).toInt(), 0)
                setOnClickListener { onAction() }
            })
        }
    }
}
