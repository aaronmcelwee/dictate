package dev.dictate.grokvoice

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import java.util.concurrent.Executors

/**
 * One press of the side key (or a Try-it button): record until the speaker pauses, transcribe with
 * Grok, clean up, then either hand the text to [Host.deliverText] or send it to the chosen bot and
 * show its reply.
 */
class Run(private val context: Context, private val panel: PillPanel, private val host: Host) {
    interface Host {
        fun deliverText(text: String)
        fun close(afterMs: Long)
    }

    enum class Mode { DICTATE, BOT, UNKNOWN }
    private enum class State { IDLE, RECORDING, WORKING, DONE, CANCELLED }

    companion object {
        private val worker = Executors.newSingleThreadExecutor()
    }

    private val main = Handler(Looper.getMainLooper())
    private var state = State.IDLE
    private var recorder: Recorder? = null

    var mode = Mode.UNKNOWN
        set(v) {
            field = v
            if (state == State.RECORDING) panel.listening(label())
        }

    val isRecording get() = state == State.RECORDING

    private fun label() = when (mode) {
        Mode.DICTATE -> "Dictating"
        Mode.BOT -> "To ${Store.botLabel}"
        Mode.UNKNOWN -> "Listening"
    }

    fun start() {
        Store.init(context)
        val key = Store.apiKey
        if (key == null) {
            fail("Add your xAI API key in Grok Voice first")
            return
        }
        if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            fail("Allow the microphone in Grok Voice first")
            return
        }
        val r = Recorder(
            onLevel = { lvl -> main.post { panel.level(lvl) } },
            onAutoStop = { why ->
                main.post {
                    EventLog.add("   recording stopped: $why")
                    stop()
                }
            },
        )
        if (!r.start()) {
            fail("The microphone is busy")
            return
        }
        recorder = r
        state = State.RECORDING
        panel.listening(label())
        EventLog.add("   recording")
    }

    /** Ends the recording and processes it (pill tap, volume key or a pause). */
    fun stop() {
        if (state != State.RECORDING) return
        state = State.WORKING
        val pcm = recorder?.stop() ?: ByteArray(0)
        val seconds = pcm.size / (Recorder.RATE * 2.0)
        val silent = pcm.isNotEmpty() && pcm.all { it.toInt() == 0 }
        EventLog.add("   %.1f s of audio%s".format(seconds, if (silent) " (ALL SILENT: mic blocked?)" else ""))
        if (silent) {
            fail("The microphone was blocked")
            return
        }
        panel.working("Transcribing…")
        val key = Store.apiKey ?: return
        worker.execute {
            try {
                val t0 = SystemClock.uptimeMillis()
                val heard = Grok.transcribe(pcm, key)
                EventLog.add("   heard (${SystemClock.uptimeMillis() - t0} ms): $heard")
                if (state == State.CANCELLED) return@execute
                if (heard.isBlank()) {
                    main.post { fail("Didn't catch anything", 1500) }
                    return@execute
                }
                val t1 = SystemClock.uptimeMillis()
                val text = Grok.cleanUp(heard, key)
                if (text != heard) EventLog.add("   cleaned (${SystemClock.uptimeMillis() - t1} ms): $text")
                if (state == State.CANCELLED) return@execute
                val finalMode = if (mode == Mode.UNKNOWN) Mode.DICTATE.also { EventLog.add("   mode still unknown: treating as dictation") } else mode
                if (finalMode == Mode.DICTATE) {
                    main.post {
                        if (state != State.CANCELLED) {
                            state = State.DONE
                            host.deliverText(text)
                        }
                    }
                } else {
                    sendToBot(text)
                }
            } catch (e: Exception) {
                EventLog.add("   ERROR ${e.javaClass.simpleName}: ${e.message}")
                main.post { fail(e.message ?: "Something went wrong") }
            }
        }
    }

    private fun sendToBot(text: String) {
        main.post { panel.working("Sending to ${Store.botLabel}…") }
        val bot = GrokBot.mainBot()
        val messageId = GrokBot.send(bot.id, text)
        EventLog.add("   sent to ${bot.name}")
        main.post { panel.message("Sent to ${Store.botLabel} ✓  waiting for a reply") }
        val seen = HashSet<String>()
        val replies = StringBuilder()
        val started = SystemClock.uptimeMillis()
        var lastReply = 0L
        while (state != State.CANCELLED) {
            Thread.sleep(2000)
            val now = SystemClock.uptimeMillis()
            val fresh = try {
                GrokBot.replies(bot.id, messageId, seen)
            } catch (e: Exception) {
                EventLog.add("   reply check failed: ${e.message}")
                emptyList()
            }
            if (fresh.isNotEmpty()) {
                lastReply = now
                fresh.forEach { if (replies.isNotEmpty()) replies.append("\n\n"); replies.append(it) }
                val shown = replies.toString()
                main.post {
                    if (state != State.CANCELLED) {
                        panel.reply(Store.botLabel, shown)
                        panel.message("Tap to close")
                    }
                }
            }
            if (lastReply > 0 && now - lastReply > 30_000) break
            if (lastReply == 0L && now - started > 120_000) {
                main.post {
                    if (state != State.CANCELLED) {
                        panel.message("Sent. No reply yet, check Grok Bot")
                        host.close(3000)
                    }
                }
                break
            }
        }
        if (state != State.CANCELLED) state = State.DONE
    }

    fun cancel() {
        if (state == State.RECORDING) recorder?.stop()
        if (state != State.DONE) EventLog.add("   cancelled")
        state = State.CANCELLED
    }

    private fun fail(message: String, closeAfter: Long = 3500) {
        state = State.DONE
        recorder?.stop()
        panel.message(message)
        host.close(closeAfter)
    }
}
