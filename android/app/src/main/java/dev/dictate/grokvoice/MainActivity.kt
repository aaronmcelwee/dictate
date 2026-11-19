package dev.dictate.grokvoice

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.app.role.RoleManager
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import android.text.InputType
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import kotlin.concurrent.thread

/** Setup, a place to try the pill without the side key, and the event log. */
class MainActivity : Activity() {
    private val main = Handler(Looper.getMainLooper())
    private lateinit var status: TextView
    private lateinit var log: TextView
    private lateinit var keyStatus: TextView
    private lateinit var botButton: Button
    private lateinit var signInButton: Button
    private lateinit var sendButton: Button
    private lateinit var testBox: EditText
    private lateinit var panel: PillPanel
    private var run: Run? = null
    private val refresh: () -> Unit = { log.text = EventLog.text().ifEmpty { "Nothing yet." } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Store.init(this)
        EventLog.init(this)
        val dp = resources.displayMetrics.density
        val pad = (16 * dp).toInt()

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad * 2, pad, pad * 2)
        }
        fun label(t: String, size: Float = 15f, bold: Boolean = false) = TextView(this).apply {
            text = t
            textSize = size
            if (bold) setTypeface(typeface, Typeface.BOLD)
            setPadding(0, pad / 2, 0, pad / 4)
        }.also { root.addView(it) }
        fun heading(t: String) = label(t, 19f, true).apply { setPadding(0, pad * 3 / 2, 0, pad / 4) }
        fun button(t: String, onClick: () -> Unit) = Button(this).apply {
            text = t
            isAllCaps = false
            setOnClickListener { onClick() }
        }.also { root.addView(it) }

        label("Grok Voice", 24f, true)
        label("Test build ${packageManager.getPackageInfo(packageName, 0).versionName}", 13f)
        status = label("", 14f)

        heading("1. Basics")
        button("Allow the microphone") {
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 1)
        }
        label("xAI API key (same one as on your PC)", 14f)
        val keyBox = EditText(this).apply {
            hint = "xai-..."
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            isSingleLine = true
        }
        root.addView(keyBox)
        button("Save key") {
            val k = keyBox.text.toString().trim()
            if (k.isNotEmpty()) {
                Store.apiKey = k
                keyBox.setText("")
                updateStatus()
            }
        }
        keyStatus = label("", 13f)
        root.addView(CheckBox(this).apply {
            text = "Clean up punctuation and mis-heard words with Grok"
            isChecked = Store.cleanUp
            setOnCheckedChangeListener { _, on -> Store.cleanUp = on }
        })

        heading("2. Grok Bot")
        signInButton = button("Sign in to Grok Bot") { signIn() }
        botButton = button("Choose bot") { chooseBot() }

        heading("3. Try it here (no side key needed)")
        label("Dictate: tap the box, tap Dictate, speak, then pause. The text lands at the cursor.", 14f)
        testBox = EditText(this).apply {
            hint = "Test box"
            minLines = 2
        }
        root.addView(testBox)
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        row.addView(Button(this).apply {
            text = "Dictate"
            isAllCaps = false
            setOnClickListener { tryIt(Run.Mode.DICTATE) }
        })
        sendButton = Button(this).apply {
            isAllCaps = false
            setOnClickListener { tryIt(Run.Mode.BOT) }
        }
        row.addView(sendButton)
        root.addView(row)
        panel = PillPanel(this)
        panel.onPillTap { run?.let { if (it.isRecording) it.stop() else closePanel(0) } }
        root.addView(panel, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
            topMargin = pad
        })

        heading("4. Side key")
        button("Make Grok Voice the digital assistant app") {
            if (!open(Intent(Settings.ACTION_VOICE_INPUT_SETTINGS))) open(Intent(Settings.ACTION_MANAGE_DEFAULT_APPS_SETTINGS))
        }
        label("On that screen, also keep \"Use text from screen\" on: it's how Grok Voice tells whether a text box is open.", 13f)
        label("Then Settings > Advanced features > Side button > Press and hold > Digital assistant app.", 14f)
        label("Keyboard open: the pill says Dictating and the text is copied for you to paste. No keyboard: it goes to your bot. Tap the pill or press volume to stop early.", 13f)

        heading("Optional: type automatically")
        label("Without this, dictation is copied to the clipboard. With it, Grok Voice types it into the text box for you.", 13f)
        button("Allow restricted settings (App info > top-right menu)") {
            open(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
        }
        button("Turn on the Grok Voice accessibility service") {
            open(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }
        button("Let it run in the background (battery)") {
            open(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
        }

        heading("Log (newest first)")
        val logRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        logRow.addView(Button(this).apply {
            text = "Copy"
            isAllCaps = false
            setOnClickListener {
                getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Grok Voice log", statusText() + "\n\n" + EventLog.text()))
                Toast.makeText(this@MainActivity, "Log copied", Toast.LENGTH_SHORT).show()
            }
        })
        logRow.addView(Button(this).apply {
            text = "Share"
            isAllCaps = false
            setOnClickListener {
                val send = Intent(Intent.ACTION_SEND).apply {
                    type = "text/plain"
                    putExtra(Intent.EXTRA_TEXT, statusText() + "\n\n" + EventLog.text())
                }
                startActivity(Intent.createChooser(send, "Share Grok Voice log"))
            }
        })
        logRow.addView(Button(this).apply {
            text = "Clear"
            isAllCaps = false
            setOnClickListener { EventLog.clear() }
        })
        root.addView(logRow)
        log = TextView(this).apply {
            typeface = Typeface.MONOSPACE
            textSize = 11f
            setTextIsSelectable(true)
        }
        root.addView(log)

        setContentView(ScrollView(this).apply {
            addView(root)
            isFillViewport = true
        })
    }

    override fun onResume() {
        super.onResume()
        updateStatus()
        EventLog.addListener(refresh)
        refresh()
    }

    override fun onPause() {
        EventLog.removeListener(refresh)
        run?.cancel()
        super.onPause()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        updateStatus()
    }

    private fun tryIt(mode: Run.Mode) {
        run?.cancel()
        panel.reset()
        EventLog.add("TRY IT: ${if (mode == Run.Mode.DICTATE) "dictate" else "send to ${Store.botLabel}"}")
        val r = Run(this, panel, object : Run.Host {
            override fun deliverText(text: String) {
                val start = testBox.selectionStart.coerceAtLeast(0)
                val end = testBox.selectionEnd.coerceAtLeast(start)
                testBox.text.replace(start, end, text)
                panel.message("Typed ✓")
                closePanel(1500)
            }

            override fun close(afterMs: Long) = closePanel(afterMs)
        })
        r.mode = mode
        run = r
        r.start()
    }

    private fun closePanel(afterMs: Long) {
        main.postDelayed({
            run?.cancel()
            panel.reset()
        }, afterMs)
    }

    private fun signIn() {
        signInButton.isEnabled = false
        signInButton.text = "Finish signing in in the browser…"
        thread {
            val result = try {
                GrokBot.signIn(this)
                Store.botId = ""
                val bot = GrokBot.mainBot()
                "Signed in. Bot: ${bot.name}"
            } catch (e: Exception) {
                "Sign-in failed: ${e.message}"
            }
            EventLog.add(result)
            main.post {
                signInButton.isEnabled = true
                Toast.makeText(this, result, Toast.LENGTH_LONG).show()
                updateStatus()
            }
        }
    }

    private fun chooseBot() {
        if (!GrokBot.signedIn) {
            Toast.makeText(this, "Sign in to Grok Bot first", Toast.LENGTH_SHORT).show()
            return
        }
        thread {
            try {
                val bots = GrokBot.listBots().sortedBy { it.name.lowercase() }
                main.post {
                    AlertDialog.Builder(this)
                        .setTitle("Send side key instructions to")
                        .setSingleChoiceItems(bots.map { it.name }.toTypedArray(), bots.indexOfFirst { it.id == Store.botId }) { d, i ->
                            Store.botId = bots[i].id
                            Store.botName = bots[i].name
                            d.dismiss()
                            updateStatus()
                        }
                        .show()
                }
            } catch (e: Exception) {
                main.post { Toast.makeText(this, "Couldn't list bots: ${e.message}", Toast.LENGTH_LONG).show() }
            }
        }
    }

    private fun updateStatus() {
        status.text = statusText()
        val key = Store.apiKey
        keyStatus.text = if (key == null) "No key saved yet" else "Key saved (ends ${key.takeLast(4)})"
        signInButton.text = if (GrokBot.signedIn) "Signed in to Grok Bot (sign in again)" else "Sign in to Grok Bot"
        botButton.text = "Choose bot (now: ${Store.botName.ifEmpty { "Jeeves, once signed in" }})"
        sendButton.text = "Send to ${Store.botLabel}"
    }

    private fun statusText(): String {
        fun mark(ok: Boolean) = if (ok) "✓" else "✗"
        val mic = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
        val assistant = try {
            getSystemService(RoleManager::class.java).isRoleHeld(RoleManager.ROLE_ASSISTANT)
        } catch (_: Exception) {
            false
        }
        val a11y = GvA11yService.instance != null
        val battery = getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName)
        return listOf(
            "${mark(mic)} Microphone",
            "${mark(Store.apiKey != null)} xAI API key",
            "${mark(GrokBot.signedIn)} Grok Bot sign-in",
            "${mark(assistant)} Digital assistant app is Grok Voice",
            "${if (a11y) "✓" else "–"} Auto-type (optional accessibility)",
            "${if (battery) "✓" else "–"} Unrestricted battery (optional)",
            "Phone: ${android.os.Build.MODEL}, Android ${android.os.Build.VERSION.RELEASE}",
        ).joinToString("\n")
    }

    private fun open(intent: Intent): Boolean = try {
        startActivity(intent)
        true
    } catch (_: Exception) {
        false
    }
}
