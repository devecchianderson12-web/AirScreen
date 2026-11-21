package com.dualscreen.app.data

import android.content.Context
import android.content.SharedPreferences
import android.os.Build
import android.provider.Settings
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** A PC this phone has paired with. The device token is stored encrypted with an Android Keystore key. */
data class PairedHost(
    val hostId: String,
    val name: String,
    val fingerprint: String,
    val addresses: List<String>,
    val port: Int,
    val signalingUrl: String?,
    val tokenEnc: String,
    val lastUsed: Long,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("hostId", hostId).put("name", name).put("fp", fingerprint)
        .put("addresses", JSONArray(addresses)).put("port", port)
        .put("signaling", signalingUrl ?: "").put("token", tokenEnc).put("lastUsed", lastUsed)

    companion object {
        fun fromJson(o: JSONObject) = PairedHost(
            hostId = o.getString("hostId"),
            name = o.optString("name"),
            fingerprint = o.getString("fp"),
            addresses = o.optJSONArray("addresses")?.let { a -> (0 until a.length()).map { a.getString(it) } } ?: emptyList(),
            port = o.optInt("port", 47800),
            signalingUrl = o.optString("signaling").ifEmpty { null },
            tokenEnc = o.getString("token"),
            lastUsed = o.optLong("lastUsed"),
        )
    }
}

class Store(context: Context) {
    private val prefs: SharedPreferences = context.getSharedPreferences("dualscreen", Context.MODE_PRIVATE)
    private val appContext = context.applicationContext

    val deviceId: String
        get() = prefs.getString("deviceId", null) ?: UUID.randomUUID().toString().also { prefs.edit().putString("deviceId", it).apply() }

    val deviceName: String
        get() = Settings.Global.getString(appContext.contentResolver, "device_name")?.takeIf { it.isNotBlank() }
            ?: "${Build.MANUFACTURER} ${Build.MODEL}"

    /** Play the PC's sound on the phone. */
    var audio: Boolean
        get() = prefs.getBoolean("audio", false)
        set(v) = prefs.edit().putBoolean("audio", v).apply()

    /** Small live stats in a corner of the stream. */
    var showStats: Boolean
        get() = prefs.getBoolean("showStats", false)
        set(v) = prefs.edit().putBoolean("showStats", v).apply()

    var quality: String
        get() = prefs.getString("quality", "auto") ?: "auto"
        set(v) = prefs.edit().putString("quality", v).apply()

    fun hosts(): List<PairedHost> {
        val arr = JSONArray(prefs.getString("hosts", "[]"))
        return (0 until arr.length()).mapNotNull { runCatching { PairedHost.fromJson(arr.getJSONObject(it)) }.getOrNull() }
            .sortedByDescending { it.lastUsed }
    }

    fun host(hostId: String) = hosts().firstOrNull { it.hostId == hostId }

    fun saveHost(h: PairedHost) {
        val list = hosts().filter { it.hostId != h.hostId } + h
        prefs.edit().putString("hosts", JSONArray(list.map { it.toJson() }).toString()).apply()
    }

    fun forget(hostId: String) {
        val list = hosts().filter { it.hostId != hostId }
        prefs.edit().putString("hosts", JSONArray(list.map { it.toJson() }).toString()).apply()
    }

    fun touch(hostId: String, addresses: List<String>? = null) {
        host(hostId)?.let { saveHost(it.copy(lastUsed = System.currentTimeMillis(), addresses = addresses ?: it.addresses)) }
    }

    // ---- token encryption (AES-256-GCM, non-exportable Keystore key) ----

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return gen.generateKey()
    }

    fun encrypt(plain: String): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key())
        val out = c.iv + c.doFinal(plain.toByteArray())
        return Base64.encodeToString(out, Base64.NO_WRAP)
    }

    fun decrypt(enc: String): String? = runCatching {
        val data = Base64.decode(enc, Base64.NO_WRAP)
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, data, 0, 12))
        String(c.doFinal(data, 12, data.size - 12))
    }.getOrNull()

    companion object {
        private const val KEY_ALIAS = "dualscreen.tokens.v1"
    }
}
