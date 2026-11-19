package dev.dictate.grokvoice

import android.content.Context
import android.os.SystemClock
import android.os.VibrationEffect
import android.os.VibratorManager
import android.widget.Toast

/** What a side key long press does. Test build: types a fixed line or shows where a bot message would go. */
object Trigger {
    const val TEST_LINE = "Grok Voice test line. "
    private const val DEFAULT_BOT = "Jeeves"

    fun fire(context: Context, source: String) {
        EventLog.init(context)
        val triggerTime = SystemClock.uptimeMillis()
        buzz(context)

        val a11y = GvA11yService.instance
        if (a11y == null) {
            EventLog.add("TRIGGER via $source: accessibility service is OFF, so I can't tell whether a keyboard is up.")
            Toast.makeText(context, "Grok Voice: turn on its accessibility service first", Toast.LENGTH_LONG).show()
            return
        }

        val d = a11y.decide(triggerTime)
        EventLog.add("TRIGGER via $source -> ${if (d.typing) "DICTATE (type into field)" else "BOT (send to $DEFAULT_BOT)"}")
        EventLog.add("   live now:      ${d.live.describe()}")
        EventLog.add("   just before:   ${d.before?.describe() ?: "no earlier reading"}")

        if (d.typing) {
            a11y.showPill("Dictating (test)")
            a11y.insertTextSoon(TEST_LINE)
        } else {
            a11y.showPill("To $DEFAULT_BOT (test, nothing sent)")
        }
    }

    private fun buzz(context: Context) {
        try {
            val vm = context.getSystemService(VibratorManager::class.java)
            vm?.defaultVibrator?.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_CLICK))
        } catch (_: Exception) {
        }
    }
}
