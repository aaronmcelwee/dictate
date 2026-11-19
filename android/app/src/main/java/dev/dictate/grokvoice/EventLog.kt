package dev.dictate.grokvoice

import android.content.Context
import android.os.Handler
import android.os.Looper
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.CopyOnWriteArrayList

/** Small on-device log the test screen shows, so we can see what each side key press did. */
object EventLog {
    private const val MAX_LINES = 300
    private val lines = ArrayDeque<String>()
    private var file: File? = null
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private val main = Handler(Looper.getMainLooper())
    private val fmt = SimpleDateFormat("HH:mm:ss.SSS", Locale.US)

    @Synchronized
    fun init(context: Context) {
        if (file != null) return
        val f = File(context.applicationContext.filesDir, "events.log")
        file = f
        if (f.exists()) {
            f.readLines().takeLast(MAX_LINES).forEach { lines.addLast(it) }
        }
    }

    fun add(message: String) {
        val line = "${fmt.format(Date())}  $message"
        synchronized(this) {
            lines.addLast(line)
            while (lines.size > MAX_LINES) lines.removeFirst()
            try {
                file?.writeText(lines.joinToString("\n") + "\n")
            } catch (_: Exception) {
            }
        }
        android.util.Log.i("GrokVoice", message)
        main.post { listeners.forEach { it() } }
    }

    @Synchronized
    fun text(): String = lines.reversed().joinToString("\n")

    @Synchronized
    fun clear() {
        lines.clear()
        try {
            file?.writeText("")
        } catch (_: Exception) {
        }
        main.post { listeners.forEach { it() } }
    }

    fun addListener(l: () -> Unit) = listeners.add(l)
    fun removeListener(l: () -> Unit) = listeners.remove(l)
}
