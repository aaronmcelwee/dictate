package dev.dictate.grokvoice

import android.app.Activity
import android.app.assist.AssistStructure
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.os.VibrationEffect
import android.os.VibratorManager
import android.service.voice.VoiceInteractionService
import android.service.voice.VoiceInteractionSession
import android.service.voice.VoiceInteractionSessionService
import android.speech.RecognitionService
import android.speech.SpeechRecognizer
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.widget.FrameLayout

/** Registers Grok Voice as the phone's digital assistant, so the side key's long press reaches us. */
class GvAssistService : VoiceInteractionService() {
    override fun onReady() {
        super.onReady()
        EventLog.init(this)
        EventLog.add("Grok Voice is the active digital assistant")
    }
}

class GvSessionService : VoiceInteractionSessionService() {
    override fun onNewSession(args: Bundle?): VoiceInteractionSession = GvSession(this)
}

/** Shared by the assistant session and the fallback activity. */
object Overlay {
    /** A transparent full-screen layer with the pill near the bottom; tapping outside the pill closes. */
    fun build(context: Context, panel: PillPanel, onOutside: () -> Unit): View {
        val dp = context.resources.displayMetrics.density
        return FrameLayout(context).apply {
            setBackgroundColor(Color.TRANSPARENT)
            isClickable = true
            setOnClickListener { onOutside() }
            addView(panel, FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL,
            ).apply {
                val side = (16 * dp).toInt()
                setMargins(side, 0, side, (110 * dp).toInt())
            })
        }
    }

    /**
     * Dictated text goes into the field: typed by the accessibility service when it is on,
     * otherwise put on the clipboard to paste.
     */
    fun deliver(context: Context, panel: PillPanel, text: String, close: (Long) -> Unit) {
        val a11y = GvA11yService.instance
        if (a11y != null) {
            EventLog.add("   handing to accessibility to type")
            close(0)
            a11y.insertTextSoon(text)
        } else {
            context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Grok Voice", text))
            EventLog.add("   copied to clipboard (accessibility is off)")
            panel.message("Copied. Long-press the text box > Paste")
            close(2200)
        }
    }

    fun buzz(context: Context) {
        try {
            context.getSystemService(VibratorManager::class.java)?.defaultVibrator
                ?.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_CLICK))
        } catch (_: Exception) {
        }
    }

    /** Keyboard state from the accessibility service, when it's on. Null when it's off. */
    fun a11yMode(triggerTime: Long): Run.Mode? {
        val a11y = GvA11yService.instance ?: return null
        val d = a11y.decide(triggerTime)
        EventLog.add("   accessibility: now ${d.live.describe()}")
        EventLog.add("   accessibility: before ${d.before?.describe() ?: "no earlier reading"}")
        return if (d.typing) Run.Mode.DICTATE else Run.Mode.BOT
    }
}

/**
 * The window the side key opens: the pill, recording straight away. Whether the text goes into
 * the field or to the bot is decided from the screen the person was on (assist data, or the
 * accessibility service when it's on).
 */
class GvSession(context: Context) : VoiceInteractionSession(context) {
    private val main = Handler(Looper.getMainLooper())
    private val panel = PillPanel(context)
    private var run: Run? = null
    private var a11yMode: Run.Mode? = null

    override fun onCreateContentView(): View = Overlay.build(context, panel) { closeNow() }

    override fun onShow(args: Bundle?, showFlags: Int) {
        super.onShow(args, showFlags)
        Store.init(context)
        EventLog.init(context)
        val t = SystemClock.uptimeMillis()
        EventLog.add("SIDE KEY: assistant opened (showFlags=$showFlags)")
        Overlay.buzz(context)
        panel.reset()
        val r = Run(context, panel, object : Run.Host {
            override fun deliverText(text: String) = Overlay.deliver(context, panel, text) { closeAfter(it) }
            override fun close(afterMs: Long) = closeAfter(afterMs)
        })
        panel.onPillTap { if (r.isRecording) r.stop() else closeNow() }
        run = r
        a11yMode = Overlay.a11yMode(t)
        a11yMode?.let { r.mode = it }
        r.start()
    }

    override fun onHandleAssist(state: AssistState) {
        super.onHandleAssist(state)
        val structure = state.assistStructure
        if (structure == null) {
            EventLog.add("   assist data: none (turn on \"Use text from screen\" for Grok Voice)")
            return
        }
        var found: String? = null
        for (i in 0 until structure.windowNodeCount) {
            found = findFocusedField(structure.getWindowNodeAt(i).rootViewNode)
            if (found != null) break
        }
        val app = structure.activityComponent?.packageName ?: "?"
        EventLog.add("   assist data: $app, focused text box: ${found ?: "none"}")
        val r = run ?: return
        // A focused text box wins; otherwise keep what accessibility saw, or go to the bot.
        r.mode = when {
            found != null -> Run.Mode.DICTATE
            a11yMode != null -> a11yMode!!
            else -> Run.Mode.BOT
        }
        EventLog.add("   mode: ${r.mode}")
    }

    private fun findFocusedField(node: AssistStructure.ViewNode?): String? {
        if (node == null) return null
        if (node.isFocused && (node.inputType != 0 || node.className?.contains("EditText") == true)) {
            return node.className?.substringAfterLast('.') ?: "field"
        }
        for (i in 0 until node.childCount) {
            findFocusedField(node.getChildAt(i))?.let { return it }
        }
        return null
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if ((keyCode == KeyEvent.KEYCODE_VOLUME_DOWN || keyCode == KeyEvent.KEYCODE_VOLUME_UP) && run?.isRecording == true) {
            EventLog.add("   volume key: stop")
            run?.stop()
            return true
        }
        return super.onKeyDown(keyCode, event)
    }

    override fun onBackPressed() = closeNow()

    override fun onHide() {
        run?.cancel()
        run = null
        main.removeCallbacksAndMessages(null)
        super.onHide()
    }

    private fun closeNow() {
        run?.cancel()
        hide()
    }

    private fun closeAfter(ms: Long) {
        if (ms <= 0) hide() else main.postDelayed({ hide() }, ms)
    }
}

/** Required for the assistant role. Grok Voice doesn't offer system speech recognition. */
class GvRecognitionService : RecognitionService() {
    override fun onStartListening(recognizerIntent: Intent?, listener: Callback?) {
        try {
            listener?.error(SpeechRecognizer.ERROR_CLIENT)
        } catch (_: Exception) {
        }
    }

    override fun onCancel(listener: Callback?) {}
    override fun onStopListening(listener: Callback?) {}
}

/** Fallback if One UI launches the assistant as an activity instead of a session: same pill. */
class AssistActivity : Activity() {
    private lateinit var panel: PillPanel
    private var run: Run? = null
    private val main = Handler(Looper.getMainLooper())

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Store.init(this)
        EventLog.init(this)
        val t = SystemClock.uptimeMillis()
        EventLog.add("SIDE KEY: assist activity opened")
        Overlay.buzz(this)
        panel = PillPanel(this)
        setContentView(Overlay.build(this, panel) { closeNow() })
        val r = Run(this, panel, object : Run.Host {
            override fun deliverText(text: String) = Overlay.deliver(this@AssistActivity, panel, text) { closeAfter(it) }
            override fun close(afterMs: Long) = closeAfter(afterMs)
        })
        panel.onPillTap { if (r.isRecording) r.stop() else closeNow() }
        run = r
        r.mode = Overlay.a11yMode(t) ?: Run.Mode.UNKNOWN
        r.start()
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if ((keyCode == KeyEvent.KEYCODE_VOLUME_DOWN || keyCode == KeyEvent.KEYCODE_VOLUME_UP) && run?.isRecording == true) {
            run?.stop()
            return true
        }
        return super.onKeyDown(keyCode, event)
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() = closeNow()

    override fun onDestroy() {
        run?.cancel()
        main.removeCallbacksAndMessages(null)
        super.onDestroy()
    }

    private fun closeNow() {
        run?.cancel()
        finish()
    }

    private fun closeAfter(ms: Long) {
        if (ms <= 0) finish() else main.postDelayed({ finish() }, ms)
    }
}
