package com.dualscreen.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import com.dualscreen.app.ui.DisplayActivity

/**
 * Foreground service that keeps the connection alive while the phone is used as a monitor
 * (prevents Android from suspending the process, keeps Wi-Fi in low-latency mode).
 */
class StreamService : Service() {
    private var wifiLock: WifiManager.WifiLock? = null
    private var cpuLock: PowerManager.WakeLock? = null
    private val listener: () -> Unit = { updateNotification() }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(NotificationManager::class.java)!!
        nm.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.notif_channel), NotificationManager.IMPORTANCE_LOW))
        if (Build.VERSION.SDK_INT >= 29)
            startForeground(ID, build(), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        else
            startForeground(ID, build())

        val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        @Suppress("DEPRECATION")
        val mode = if (Build.VERSION.SDK_INT >= 29) WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF
        wifiLock = wifi.createWifiLock(mode, "DualScreen:wifi").apply { setReferenceCounted(false); acquire() }
        cpuLock = getSystemService(PowerManager::class.java)!!
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "DualScreen:stream").apply { setReferenceCounted(false); acquire(12 * 60 * 60 * 1000L) }
        StreamHub.addListener(listener)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_NOT_STICKY

    private fun build(): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, DisplayActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val text = when (StreamHub.state) {
            LinkState.CONNECTED -> getString(R.string.notif_streaming, StreamHub.hostName)
            else -> StreamHub.message
        }
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.mipmap.ic_launcher_foreground)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }

    private fun updateNotification() {
        getSystemService(NotificationManager::class.java)!!.notify(ID, build())
    }

    /** Swiping DualScreen away from Recents ends the stream (nothing is shown anymore). */
    override fun onTaskRemoved(rootIntent: Intent?) {
        StreamHub.disconnect()
        stopSelf()
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        StreamHub.removeListener(listener)
        runCatching { wifiLock?.release() }
        runCatching { cpuLock?.release() }
        super.onDestroy()
    }

    companion object {
        private const val CHANNEL = "stream"
        private const val ID = 1
    }
}
