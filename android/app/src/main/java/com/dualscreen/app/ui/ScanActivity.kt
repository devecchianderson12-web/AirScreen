package com.dualscreen.app.ui

import android.app.Activity
import android.content.Intent
import android.graphics.ImageFormat
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.ImageReader
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.util.Size
import android.view.Gravity
import android.view.Surface
import android.view.TextureView
import android.widget.FrameLayout
import android.widget.TextView
import com.dualscreen.app.R
import com.dualscreen.app.net.PairInfo
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer

/** Minimal QR scanner: Camera2 preview + ZXing decoding of the luminance plane. No Google Play services needed. */
class ScanActivity : Activity() {
    private lateinit var texture: TextureView
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reader: ImageReader? = null
    private val thread = HandlerThread("DualScreen.Scan").apply { start() }
    private val handler = Handler(thread.looper)
    private val zxing = MultiFormatReader().apply {
        setHints(mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE), DecodeHintType.TRY_HARDER to true))
    }
    @Volatile private var done = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val root = FrameLayout(this)
        texture = TextureView(this)
        root.addView(texture, FrameLayout.LayoutParams(-1, -1))
        root.addView(TextView(this).apply {
            text = "Apunta al código QR que muestra DualScreen en Windows"
            setTextColor(getColor(R.color.text)); textSize = 16f; gravity = Gravity.CENTER
            setBackgroundResource(R.drawable.overlay); setPadding(40, 30, 40, 30)
        }, FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply { bottomMargin = 120 })
        setContentView(root)
    }

    override fun onResume() {
        super.onResume()
        if (texture.isAvailable) open() else texture.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(s: SurfaceTexture, w: Int, h: Int) = open()
            override fun onSurfaceTextureSizeChanged(s: SurfaceTexture, w: Int, h: Int) {}
            override fun onSurfaceTextureDestroyed(s: SurfaceTexture) = true
            override fun onSurfaceTextureUpdated(s: SurfaceTexture) {}
        }
    }

    override fun onPause() {
        close()
        super.onPause()
    }

    override fun onDestroy() {
        thread.quitSafely()
        super.onDestroy()
    }

    private fun open() {
        val cm = getSystemService(CameraManager::class.java)!!
        val id = cm.cameraIdList.firstOrNull {
            cm.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_BACK
        } ?: cm.cameraIdList.firstOrNull() ?: return
        val size = Size(1280, 720)
        reader = ImageReader.newInstance(size.width, size.height, ImageFormat.YUV_420_888, 2).apply {
            setOnImageAvailableListener({ r -> decode(r) }, handler)
        }
        try {
            cm.openCamera(id, object : CameraDevice.StateCallback() {
                override fun onOpened(c: CameraDevice) {
                    camera = c
                    val st = texture.surfaceTexture ?: return
                    st.setDefaultBufferSize(size.width, size.height)
                    val preview = Surface(st)
                    val targets = listOf(preview, reader!!.surface)
                    @Suppress("DEPRECATION")
                    c.createCaptureSession(targets, object : CameraCaptureSession.StateCallback() {
                        override fun onConfigured(s: CameraCaptureSession) {
                            session = s
                            val req = c.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
                                targets.forEach { addTarget(it) }
                                set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE)
                            }
                            s.setRepeatingRequest(req.build(), null, handler)
                        }
                        override fun onConfigureFailed(s: CameraCaptureSession) = finish()
                    }, handler)
                }
                override fun onDisconnected(c: CameraDevice) = c.close()
                override fun onError(c: CameraDevice, error: Int) { c.close(); finish() }
            }, handler)
        } catch (e: SecurityException) {
            finish()
        }
    }

    private fun decode(r: ImageReader) {
        val img = r.acquireLatestImage() ?: return
        try {
            if (done) return
            val plane = img.planes[0]
            val buf = plane.buffer
            val data = ByteArray(buf.remaining()).also { buf.get(it) }
            val src = PlanarYUVLuminanceSource(data, plane.rowStride, img.height, 0, 0, img.width, img.height, false)
            val result = runCatching { zxing.decodeWithState(BinaryBitmap(HybridBinarizer(src))) }.getOrNull() ?: return
            if (PairInfo.parse(result.text) != null) {
                done = true
                setResult(RESULT_OK, Intent().putExtra(EXTRA_RESULT, result.text))
                runOnUiThread { finish() }
            }
        } finally {
            zxing.reset()
            img.close()
        }
    }

    private fun close() {
        runCatching { session?.close() }
        runCatching { camera?.close() }
        runCatching { reader?.close() }
        session = null; camera = null; reader = null
    }

    companion object {
        const val EXTRA_RESULT = "qr"
    }
}
