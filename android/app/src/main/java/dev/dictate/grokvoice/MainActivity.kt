package dev.dictate.grokvoice

import android.app.Activity
import android.app.role.RoleManager
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.graphics.Typeface
import android.net.Uri
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast

/** Setup checklist, a test text box and the event log. */
class MainActivity : Activity() {
    private lateinit var status: TextView
    private lateinit var log: TextView
    private val refresh: () -> Unit = { log.text = EventLog.text().ifEmpty { "Nothing yet. Long-press the side key." } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        EventLog.init(this)
        val pad = (16 * resources.displayMetrics.density).toInt()

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad * 2, pad, pad)
        }
        fun label(t: String, size: Float = 15f, bold: Boolean = false) = TextView(this).apply {
            text = t
            textSize = size
            if (bold) setTypeface(typeface, Typeface.BOLD)
            setPadding(0, pad / 2, 0, pad / 4)
        }.also { root.addView(it) }
        fun button(t: String, onClick: () -> Unit) = Button(this).apply {
            text = t
            isAllCaps = false
            setOnClickListener { onClick() }
        }.also { root.addView(it) }

        label("Grok Voice (side key test)", 22f, true)
        label("Version ${packageManager.getPackageInfo(packageName, 0).versionName}", 13f)
        status = label("", 15f)

        label("Setup", 18f, true)
        button("1. Allow restricted settings (App info, top-right menu)") {
            open(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
        }
        button("2. Turn on the Grok Voice accessibility service") {
            open(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }
        button("3. Make Grok Voice the digital assistant app") {
            if (!open(Intent(Settings.ACTION_VOICE_INPUT_SETTINGS))) open(Intent(Settings.ACTION_MANAGE_DEFAULT_APPS_SETTINGS))
        }
        button("4. Let it run in the background (battery)") {
            open(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
        }
        label("5. In Settings > Advanced features > Side button, set Press and hold to Digital assistant app (Grok Voice).", 14f)

        label("Try it", 18f, true)
        label("Tap the box so the keyboard is up, then long-press the side key: a test line should appear. Then try in Messages, Chrome, etc. With no keyboard up, the pill should say \"To Jeeves\".", 14f)
        root.addView(EditText(this).apply { hint = "Test box" })

        label("Log (newest first)", 18f, true)
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        row.addView(Button(this).apply {
            text = "Copy log"
            isAllCaps = false
            setOnClickListener {
                getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Grok Voice log", statusText() + "\n\n" + EventLog.text()))
                Toast.makeText(this@MainActivity, "Log copied", Toast.LENGTH_SHORT).show()
            }
        })
        row.addView(Button(this).apply {
            text = "Share log"
            isAllCaps = false
            setOnClickListener {
                val send = Intent(Intent.ACTION_SEND).apply {
                    type = "text/plain"
                    putExtra(Intent.EXTRA_TEXT, statusText() + "\n\n" + EventLog.text())
                }
                startActivity(Intent.createChooser(send, "Share Grok Voice log"))
            }
        })
        row.addView(Button(this).apply {
            text = "Clear"
            isAllCaps = false
            setOnClickListener { EventLog.clear() }
        })
        root.addView(row)
        log = TextView(this).apply {
            typeface = Typeface.MONOSPACE
            textSize = 11f
            setTextIsSelectable(true)
        }
        root.addView(log)

        setContentView(ScrollView(this).apply { addView(root) ; isFillViewport = true })
    }

    override fun onResume() {
        super.onResume()
        status.text = statusText()
        EventLog.addListener(refresh)
        refresh()
    }

    override fun onPause() {
        EventLog.removeListener(refresh)
        super.onPause()
    }

    private fun statusText(): String {
        val assistant = try {
            getSystemService(RoleManager::class.java).isRoleHeld(RoleManager.ROLE_ASSISTANT)
        } catch (_: Exception) {
            false
        }
        val a11y = GvA11yService.instance != null
        val battery = getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName)
        fun mark(ok: Boolean) = if (ok) "✓" else "✗"
        return "${mark(a11y)} Accessibility service on\n${mark(assistant)} Digital assistant app is Grok Voice\n${mark(battery)} Unrestricted battery\n(The side button setting can't be checked from here.)\nPhone: ${android.os.Build.MODEL}, Android ${android.os.Build.VERSION.RELEASE}"
    }

    private fun open(intent: Intent): Boolean = try {
        startActivity(intent)
        true
    } catch (_: Exception) {
        false
    }
}
