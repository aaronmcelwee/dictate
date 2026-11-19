package dev.dictate.grokvoice

import android.app.Activity
import android.app.assist.AssistStructure
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.service.voice.VoiceInteractionService
import android.service.voice.VoiceInteractionSession
import android.service.voice.VoiceInteractionSessionService
import android.speech.RecognitionService
import android.speech.SpeechRecognizer

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

/**
 * The session the side key opens. It shows no UI of its own (so the app underneath should keep
 * its keyboard), decides the mode, then closes straight away.
 */
class GvSession(context: Context) : VoiceInteractionSession(context) {
    private val main = Handler(Looper.getMainLooper())

    override fun onCreate() {
        super.onCreate()
        setUiEnabled(false)
    }

    override fun onShow(args: Bundle?, showFlags: Int) {
        super.onShow(args, showFlags)
        EventLog.init(context)
        EventLog.add("Assistant session opened (showFlags=$showFlags)")
        Trigger.fire(context, "side key / assistant")
        main.post { hide() }
    }

    override fun onHandleAssist(state: AssistState) {
        super.onHandleAssist(state)
        val structure = state.assistStructure ?: run {
            EventLog.add("   assist data: none (\"Use text from screen\" may be off)")
            return
        }
        var found: String? = null
        for (i in 0 until structure.windowNodeCount) {
            found = findFocusedField(structure.getWindowNodeAt(i).rootViewNode)
            if (found != null) break
        }
        EventLog.add("   assist data: ${structure.activityComponent?.flattenToShortString()} focused field=${found ?: "none"}")
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
}

/** Required for the assistant role. Grok Voice doesn't offer system speech recognition yet. */
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

/** Fallback if One UI launches the assistant as an activity instead of a session. */
class AssistActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Trigger.fire(applicationContext, "assist activity")
        finish()
        overridePendingTransition(0, 0)
    }
}
