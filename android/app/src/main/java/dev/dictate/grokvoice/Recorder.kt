package dev.dictate.grokvoice

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.SystemClock
import java.io.ByteArrayOutputStream
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.sqrt

/**
 * 16 kHz mono microphone capture. Reports a level for the waveform and stops by itself once the
 * speaker has paused (or never started), so the side key needs no second press.
 */
class Recorder(
    private val onLevel: (Float) -> Unit,
    private val onAutoStop: (String) -> Unit,
) {
    companion object {
        const val RATE = 16000
        const val PAUSE_MS = 1600L
        const val NOTHING_SAID_MS = 8000L
        const val MAX_MS = 180_000L
    }

    @Volatile
    private var running = false
    private var record: AudioRecord? = null
    private var thread: Thread? = null
    private val pcm = ByteArrayOutputStream()

    @SuppressLint("MissingPermission")
    fun start(): Boolean {
        val min = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val r = try {
            AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, max(min, RATE))
        } catch (e: Exception) {
            EventLog.add("mic: ${e.javaClass.simpleName} ${e.message}")
            return false
        }
        if (r.state != AudioRecord.STATE_INITIALIZED) {
            r.release()
            return false
        }
        record = r
        r.startRecording()
        running = true
        thread = Thread({ loop(r) }, "grokvoice-mic").apply { start() }
        return true
    }

    private fun loop(r: AudioRecord) {
        val buf = ShortArray(RATE / 20) // 50 ms
        val bytes = ByteArray(buf.size * 2)
        val started = SystemClock.uptimeMillis()
        var floor = -50.0
        var speech = false
        var lastLoud = started
        var stoppedFor: String? = null
        while (running) {
            val n = r.read(buf, 0, buf.size)
            if (n <= 0) continue
            var sum = 0.0
            for (i in 0 until n) {
                val s = buf[i].toInt()
                sum += (s * s).toDouble()
                bytes[2 * i] = (s and 0xFF).toByte()
                bytes[2 * i + 1] = ((s shr 8) and 0xFF).toByte()
            }
            synchronized(pcm) { pcm.write(bytes, 0, n * 2) }
            val db = if (sum <= 0) -100.0 else 20 * log10(sqrt(sum / n) / 32768.0)
            val now = SystemClock.uptimeMillis()
            // Noise floor: follows quiet moments down at once and drifts up slowly.
            floor = (if (db < floor) db else floor + 0.02).coerceIn(-75.0, -30.0)
            val loud = db > max(-48.0, floor + 12)
            if (loud) {
                speech = true
                lastLoud = now
            }
            onLevel(((db + 60) / 50).coerceIn(0.0, 1.0).toFloat())
            stoppedFor = when {
                speech && now - lastLoud > PAUSE_MS -> "pause"
                !speech && now - started > NOTHING_SAID_MS -> "nothing said"
                now - started > MAX_MS -> "time limit"
                else -> null
            }
            if (stoppedFor != null) break
        }
        if (stoppedFor != null && running) onAutoStop(stoppedFor)
    }

    /** Stops and returns the audio as 16-bit PCM. */
    fun stop(): ByteArray {
        running = false
        try {
            thread?.join(500)
        } catch (_: InterruptedException) {
        }
        record?.let {
            try {
                it.stop()
            } catch (_: Exception) {
            }
            it.release()
        }
        record = null
        return synchronized(pcm) { pcm.toByteArray() }
    }
}
