package dev.dictate.grokvoice

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/** The dark capsule with a white waveform and a label, plus a reply card above it for bot answers. */
class PillPanel(context: Context) : LinearLayout(context) {
    private val dp = context.resources.displayMetrics.density
    private val wave = WaveView(context)
    private val label = TextView(context)
    private val pill = LinearLayout(context)
    private val replyText = TextView(context)
    private val replyScroll = ScrollView(context)

    init {
        orientation = VERTICAL
        gravity = Gravity.CENTER_HORIZONTAL

        replyText.setTextColor(Color.WHITE)
        replyText.textSize = 15f
        replyText.setLineSpacing(0f, 1.15f)
        val pad = (16 * dp).toInt()
        replyText.setPadding(pad, pad, pad, pad)
        replyScroll.addView(replyText)
        replyScroll.background = GradientDrawable().apply {
            setColor(0xF0141414.toInt())
            cornerRadius = 20 * dp
        }
        replyScroll.visibility = GONE
        val maxH = (context.resources.displayMetrics.heightPixels * 0.45).toInt()
        addView(replyScroll, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply {
            bottomMargin = (10 * dp).toInt()
        })
        replyScroll.viewTreeObserver.addOnGlobalLayoutListener {
            if (replyScroll.height > maxH) replyScroll.layoutParams = replyScroll.layoutParams.apply { height = maxH }
        }

        pill.orientation = HORIZONTAL
        pill.gravity = Gravity.CENTER_VERTICAL
        pill.background = GradientDrawable().apply {
            setColor(0xF0141414.toInt())
            cornerRadius = 999 * dp
        }
        val h = (16 * dp).toInt()
        val v = (10 * dp).toInt()
        pill.setPadding(h, v, (20 * dp).toInt(), v)
        pill.addView(wave, LayoutParams((44 * dp).toInt(), (22 * dp).toInt()).apply { marginEnd = (10 * dp).toInt() })
        label.setTextColor(Color.WHITE)
        label.textSize = 15f
        pill.addView(label)
        addView(pill, LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT))
        visibility = GONE
    }

    fun onPillTap(action: () -> Unit) = pill.setOnClickListener { action() }

    fun listening(text: String) {
        visibility = VISIBLE
        wave.active = true
        label.text = text
    }

    fun level(value: Float) = wave.push(value)

    fun working(text: String) {
        visibility = VISIBLE
        wave.active = false
        label.text = text
    }

    fun message(text: String) = working(text)

    fun reply(from: String, text: String) {
        visibility = VISIBLE
        replyText.text = "$from\n\n$text"
        replyScroll.visibility = VISIBLE
    }

    fun reset() {
        replyScroll.visibility = GONE
        replyText.text = ""
        visibility = GONE
    }

    private class WaveView(context: Context) : View(context) {
        private val bars = FloatArray(9)
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE }
        private val rect = RectF()
        var active = false
            set(v) {
                field = v
                if (!v) bars.fill(0f)
                invalidate()
            }

        fun push(value: Float) {
            if (!active) return
            System.arraycopy(bars, 1, bars, 0, bars.size - 1)
            bars[bars.size - 1] = value
            invalidate()
        }

        override fun onDraw(canvas: Canvas) {
            val w = width.toFloat()
            val h = height.toFloat()
            val step = w / bars.size
            val bw = step * 0.55f
            paint.alpha = if (active) 255 else 120
            for (i in bars.indices) {
                val bh = (h * (0.15f + 0.85f * bars[i])).coerceAtMost(h)
                val x = i * step + (step - bw) / 2
                rect.set(x, (h - bh) / 2, x + bw, (h + bh) / 2)
                canvas.drawRoundRect(rect, bw / 2, bw / 2, paint)
            }
        }
    }
}
