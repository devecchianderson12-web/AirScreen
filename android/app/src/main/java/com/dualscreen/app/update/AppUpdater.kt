package com.dualscreen.app.update

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.util.Log
import com.dualscreen.app.BuildConfigCompat
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit

data class ApkRelease(val version: String, val apkUrl: String, val notes: String)

/**
 * In-app updates from GitHub Releases. The APK is installed through the system PackageInstaller: Android itself
 * verifies that the new APK is signed with the same key as the installed app, and always asks the user to confirm.
 */
object AppUpdater {
    const val REPO = "devecchianderson12-web/AirScreen"
    private const val APK_NAME = "DualScreen.apk"
    private const val TAG = "DualScreen.Update"
    private val http = OkHttpClient.Builder().readTimeout(60, TimeUnit.SECONDS).build()

    /** Returns the latest release if it is newer than the installed version, null if up to date. */
    @Throws(IOException::class)
    fun check(ctx: Context): ApkRelease? {
        val req = Request.Builder().url("https://api.github.com/repos/$REPO/releases/latest")
            .header("Accept", "application/vnd.github+json").build()
        http.newCall(req).execute().use { resp ->
            if (resp.code == 404) return null
            if (!resp.isSuccessful) throw IOException("GitHub respondió ${resp.code}")
            val o = JSONObject(resp.body!!.string())
            val version = o.getString("tag_name").trimStart('v', 'V')
            val assets = o.getJSONArray("assets")
            val apk = (0 until assets.length()).map { assets.getJSONObject(it) }
                .firstOrNull { it.getString("name").equals(APK_NAME, ignoreCase = true) } ?: return null
            val notes = o.optString("body").replace(Regex("<!--.*?-->", RegexOption.DOT_MATCHES_ALL), "").trim()
            if (!isNewer(version, BuildConfigCompat.versionName(ctx))) return null
            return ApkRelease(version, apk.getString("browser_download_url"), notes)
        }
    }

    fun isNewer(candidate: String, current: String): Boolean {
        fun parts(v: String) = v.split('.', '-').mapNotNull { it.toIntOrNull() } + listOf(0, 0, 0)
        val a = parts(candidate); val b = parts(current)
        for (i in 0 until 3) if (a[i] != b[i]) return a[i] > b[i]
        return false
    }

    /** Android 8+: the user must allow this app to install apps once. Returns false and opens Settings if needed. */
    fun ensureInstallPermission(ctx: Context): Boolean {
        if (ctx.packageManager.canRequestPackageInstalls()) return true
        ctx.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${ctx.packageName}"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        return false
    }

    /** Downloads the APK (progress 0..100) and hands it to the system installer. */
    @Throws(IOException::class)
    fun downloadAndInstall(ctx: Context, release: ApkRelease, progress: (Int) -> Unit) {
        val file = File(ctx.cacheDir, "DualScreen-${release.version}.apk")
        http.newCall(Request.Builder().url(release.apkUrl).build()).execute().use { resp ->
            if (!resp.isSuccessful) throw IOException("Descarga fallida (${resp.code})")
            val body = resp.body!!
            val total = body.contentLength()
            body.byteStream().use { input ->
                file.outputStream().use { out ->
                    val buf = ByteArray(64 * 1024)
                    var done = 0L
                    while (true) {
                        val n = input.read(buf)
                        if (n < 0) break
                        out.write(buf, 0, n)
                        done += n
                        if (total > 0) progress((done * 100 / total).toInt())
                    }
                }
            }
        }

        val installer = ctx.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        params.setAppPackageName(ctx.packageName)
        val sessionId = installer.createSession(params)
        installer.openSession(sessionId).use { session ->
            file.inputStream().use { input ->
                session.openWrite("base.apk", 0, file.length()).use { out ->
                    input.copyTo(out)
                    session.fsync(out)
                }
            }
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or
                (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
            val pi = PendingIntent.getBroadcast(ctx, sessionId, Intent(ctx, InstallResultReceiver::class.java), flags)
            session.commit(pi.intentSender)
        }
        file.delete()
    }
}

/** Receives PackageInstaller results; shows the system confirmation screen when Android asks for it. */
class InstallResultReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT) ?: return
                context.startActivity(confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
            PackageInstaller.STATUS_SUCCESS -> Log.i("DualScreen.Update", "Update installed")
            else -> {
                val msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
                Log.w("DualScreen.Update", "Install failed ($status): $msg")
                android.widget.Toast.makeText(context,
                    if (status == PackageInstaller.STATUS_FAILURE_CONFLICT)
                        "No se pudo actualizar: la versión instalada tiene otra firma. Desinstálala e instala la nueva."
                    else "No se pudo instalar la actualización: ${msg ?: status}",
                    android.widget.Toast.LENGTH_LONG).show()
            }
        }
    }
}
