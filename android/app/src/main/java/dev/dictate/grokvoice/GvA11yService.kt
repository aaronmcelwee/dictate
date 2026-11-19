package dev.dictate.grokvoice

import android.accessibilityservice.AccessibilityService
import android.content.ClipData
import android.content.ClipboardManager
import android.graphics.Color
import android.graphics.PixelFormat
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.Gravity
import android.view.KeyEvent
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import android.view.accessibility.AccessibilityWindowInfo
import android.widget.TextView

/**
 * Watches which field has focus and whether a keyboard window is showing, types text into the
 * focused field, and draws the small pill. Also logs volume keys (future stop / hold-to-talk key).
 */
class GvA11yService : AccessibilityService() {

    companion object {
        @Volatile
        var instance: GvA11yService? = null
    }

    data class Snapshot(
        val time: Long,
        val imeVisible: Boolean,
        val editableFocused: Boolean,
        val pkg: String?,
        val cls: String?,
    ) {
        val typing get() = imeVisible && editableFocused
        fun describe(): String {
            val age = SystemClock.uptimeMillis() - time
            return "keyboard=${if (imeVisible) "UP" else "down"}, field=${if (editableFocused) "editable" else "none"}, app=${pkg ?: "?"} ${cls?.substringAfterLast('.') ?: ""} (${age} ms ago)"
        }
    }

    data class Decision(val typing: Boolean, val live: Snapshot, val before: Snapshot?)

    val main = Handler(Looper.getMainLooper())
    private val history = ArrayDeque<Snapshot>()
    private var pill: TextView? = null
    private val keyDownAt = HashMap<Int, Long>()

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        EventLog.init(this)
        EventLog.add("Accessibility service connected")
        record()
    }

    override fun onUnbind(intent: android.content.Intent?): Boolean {
        instance = null
        EventLog.add("Accessibility service disconnected")
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        instance = null
        super.onDestroy()
    }

    override fun onInterrupt() {}

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        record()
    }

    override fun onKeyEvent(event: KeyEvent): Boolean {
        val name = KeyEvent.keyCodeToString(event.keyCode)
        when (event.action) {
            KeyEvent.ACTION_DOWN -> if (event.repeatCount == 0) {
                keyDownAt[event.keyCode] = event.eventTime
                if (event.keyCode == KeyEvent.KEYCODE_POWER || event.keyCode.isVolume()) EventLog.add("key down $name")
            }
            KeyEvent.ACTION_UP -> {
                val held = keyDownAt.remove(event.keyCode)?.let { event.eventTime - it }
                if (event.keyCode == KeyEvent.KEYCODE_POWER || event.keyCode.isVolume()) EventLog.add("key up $name (held ${held ?: "?"} ms)")
            }
        }
        return false // test build never swallows keys
    }

    private fun Int.isVolume() = this == KeyEvent.KEYCODE_VOLUME_DOWN || this == KeyEvent.KEYCODE_VOLUME_UP

    fun snapshot(): Snapshot {
        val ws = try {
            windows
        } catch (_: Exception) {
            emptyList<AccessibilityWindowInfo>()
        }
        val ime = ws.any { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD }
        val focus = try {
            findFocus(AccessibilityNodeInfo.FOCUS_INPUT)
        } catch (_: Exception) {
            null
        }
        return Snapshot(
            SystemClock.uptimeMillis(),
            ime,
            focus?.isEditable == true,
            focus?.packageName?.toString(),
            focus?.className?.toString(),
        )
    }

    private fun record() {
        val s = snapshot()
        synchronized(history) {
            val last = history.lastOrNull()
            if (last != null && last.imeVisible == s.imeVisible && last.editableFocused == s.editableFocused && last.pkg == s.pkg) return
            history.addLast(s)
            while (history.size > 40) history.removeFirst()
        }
    }

    /**
     * Keyboard state as it was right before the long press. A long press holds still for at least
     * ~400 ms, so the last change older than that is the screen the person was looking at.
     */
    fun decide(triggerTime: Long): Decision {
        val live = snapshot()
        val before = synchronized(history) { history.lastOrNull { it.time < triggerTime - 300 } }
        val typing = live.typing || (before?.typing == true)
        return Decision(typing, live, before)
    }

    fun insertTextSoon(text: String) {
        main.postDelayed({ insertText(text, 0) }, 300)
    }

    private fun insertText(text: String, attempt: Int) {
        val node = try {
            findFocus(AccessibilityNodeInfo.FOCUS_INPUT)
        } catch (_: Exception) {
            null
        }
        if (node == null || !node.isEditable) {
            if (attempt < 8) {
                main.postDelayed({ insertText(text, attempt + 1) }, 150)
            } else {
                EventLog.add("   INSERT FAILED: no editable field had focus after the assistant closed")
                showPill("Couldn't find the text box")
            }
            return
        }
        val where = "${node.packageName} ${node.className?.toString()?.substringAfterLast('.')}"
        val after = "(attempt ${attempt + 1}, ${(attempt * 150) + 300} ms after trigger)"
        val imeNow = try {
            windows.any { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD }
        } catch (_: Exception) {
            false
        }
        EventLog.add("   field found in $where $after, keyboard now ${if (imeNow) "UP" else "down"}")

        // 1) Paste at the cursor: keeps the app's own undo and formatting.
        val cm = getSystemService(ClipboardManager::class.java)
        val oldClip = try {
            cm.primaryClip
        } catch (_: Exception) {
            null
        }
        try {
            cm.setPrimaryClip(ClipData.newPlainText("Grok Voice", text))
            if (node.performAction(AccessibilityNodeInfo.ACTION_PASTE)) {
                EventLog.add("   INSERTED by paste")
                showPill("Typed (paste)")
                main.postDelayed({
                    try {
                        if (oldClip != null) cm.setPrimaryClip(oldClip) else cm.clearPrimaryClip()
                    } catch (_: Exception) {
                    }
                }, 600)
                return
            }
        } catch (e: Exception) {
            EventLog.add("   paste threw ${e.javaClass.simpleName}")
        }

        // 2) Set the whole text with our line spliced in at the cursor.
        val current = if (node.isShowingHintText) "" else (node.text?.toString() ?: "")
        var start = node.textSelectionStart
        var end = node.textSelectionEnd
        if (start < 0 || start > current.length) start = current.length
        if (end < start || end > current.length) end = start
        val updated = current.substring(0, start) + text + current.substring(end)
        val args = Bundle().apply {
            putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, updated)
        }
        if (node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)) {
            val caret = start + text.length
            node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, Bundle().apply {
                putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, caret)
                putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, caret)
            })
            EventLog.add("   INSERTED by set-text (paste was refused)")
            showPill("Typed (set text)")
        } else {
            EventLog.add("   INSERT FAILED: field refused both paste and set-text")
            showPill("This text box refused the text")
        }
    }

    fun showPill(text: String, durationMs: Long = 2500) {
        main.post {
            val wm = getSystemService(WindowManager::class.java)
            pill?.let {
                try {
                    wm.removeView(it)
                } catch (_: Exception) {
                }
            }
            val density = resources.displayMetrics.density
            val tv = TextView(this).apply {
                this.text = text
                setTextColor(Color.WHITE)
                textSize = 15f
                val h = (18 * density).toInt()
                val v = (9 * density).toInt()
                setPadding(h, v, h, v)
                background = getDrawable(R.drawable.pill_bg)
            }
            val lp = WindowManager.LayoutParams(
                WindowManager.LayoutParams.WRAP_CONTENT,
                WindowManager.LayoutParams.WRAP_CONTENT,
                WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                    WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                    WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
                PixelFormat.TRANSLUCENT,
            ).apply {
                gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
                y = (56 * density).toInt()
            }
            try {
                wm.addView(tv, lp)
                pill = tv
            } catch (e: Exception) {
                EventLog.add("   pill failed: ${e.javaClass.simpleName}")
                return@post
            }
            main.postDelayed({
                if (pill === tv) {
                    try {
                        wm.removeView(tv)
                    } catch (_: Exception) {
                    }
                    pill = null
                }
            }, durationMs)
        }
    }
}
