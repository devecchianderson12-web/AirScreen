package com.dualscreen.app.net

import android.net.Uri

/** Parsed "dualscreen://pair?..." QR payload. The fingerprint is pinned, so QR pairing resists MITM. */
data class PairInfo(
    val hostId: String,
    val name: String,
    val addresses: List<String>,
    val port: Int,
    val fingerprint: String,
    val code: String,
    val signalingUrl: String?,
) {
    companion object {
        fun parse(text: String): PairInfo? = runCatching {
            val u = Uri.parse(text.trim())
            if (u.scheme != "dualscreen" || u.host != "pair" || u.getQueryParameter("v") != "1") return null
            val fp = u.getQueryParameter("f")!!.lowercase()
            val code = u.getQueryParameter("c")!!
            require(fp.length == 64 && code.length == 6 && code.all { it.isDigit() })
            PairInfo(
                hostId = u.getQueryParameter("h")!!,
                name = u.getQueryParameter("n") ?: "PC",
                addresses = u.getQueryParameter("a")?.split(',')?.filter { it.isNotBlank() } ?: emptyList(),
                port = u.getQueryParameter("p")?.toIntOrNull() ?: Protocol.LAN_PORT,
                fingerprint = fp,
                code = code,
                signalingUrl = u.getQueryParameter("s")?.takeIf { it.startsWith("wss://") || it.startsWith("ws://") },
            )
        }.getOrNull()
    }
}
