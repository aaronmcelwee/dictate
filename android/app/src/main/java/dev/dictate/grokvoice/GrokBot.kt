package dev.dictate.grokvoice

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.SystemClock
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.UUID

data class Bot(val id: String, val name: String)

/**
 * Grok Bot the way its desktop app talks to it (same undocumented protocol as the Windows app):
 * Cursor browser sign-in, then GrokBotService calls on api2.cursor.sh; listing bots goes through
 * the bot gateway.
 */
object GrokBot {
    private const val BACKEND = "https://api2.cursor.sh"
    private const val CLIENT_ID = "KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB"
    private const val VERSION = "1.2.3"

    val signedIn get() = Store.botAccess != null

    /** Opens the sign-in page in the browser and waits up to 10 minutes. Blocking: call off the main thread. */
    fun signIn(context: Context) {
        val verifier = b64url(ByteArray(32).also { SecureRandom().nextBytes(it) })
        val challenge = b64url(MessageDigest.getInstance("SHA-256").digest(verifier.toByteArray(Charsets.US_ASCII)))
        val uuid = UUID.randomUUID().toString()
        val url = "https://cursor.com/loginDeepControl?challenge=$challenge&uuid=$uuid&mode=login&redirectTarget=sand&supportsSelectedTeamLogin=true"
        context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        val deadline = SystemClock.uptimeMillis() + 10 * 60_000
        while (SystemClock.uptimeMillis() < deadline) {
            Thread.sleep(2000)
            val conn = URL("$BACKEND/auth/poll?uuid=$uuid&verifier=$verifier").openConnection() as HttpURLConnection
            conn.connectTimeout = 10_000
            conn.readTimeout = 15_000
            val (status, body) = try {
                Grok.send(conn, null)
            } catch (_: Exception) {
                continue
            }
            if (status == 404) continue
            if (status !in 200..299) throw GrokException("Grok Bot sign-in failed (HTTP $status)")
            val json = JSONObject(body)
            Store.botAccess = json.getString("accessToken")
            Store.botRefresh = json.getString("refreshToken")
            return
        }
        throw GrokException("Sign-in didn't finish within 10 minutes.")
    }

    fun signOut() {
        Store.botAccess = null
        Store.botRefresh = null
    }

    fun listBots(): List<Bot> {
        val sandbox = backend("EnsureSandBox", JSONObject(), mutation = false)
        val gateway = sandbox.getString("gatewayUrl").trimEnd('/')
        if (!gateway.startsWith("https://")) throw GrokException("Grok Bot's gateway isn't a secure address.")
        val conn = (URL("$gateway/api/listAgents").openConnection() as HttpURLConnection).apply {
            requestMethod = "POST"
            doOutput = true
            connectTimeout = 10_000
            readTimeout = 20_000
            setRequestProperty("Authorization", "Bearer ${sandbox.getString("gatewayToken")}")
            setRequestProperty("x-anyrun-port-token", sandbox.getString("gatewayPortToken"))
            setRequestProperty("Content-Type", "application/json")
        }
        val (status, text) = Grok.send(conn, "{}".toByteArray())
        if (status !in 200..299) throw GrokException("Couldn't list your bots (HTTP $status)")
        val arr = JSONArray(text)
        return (0 until arr.length()).map { arr.getJSONObject(it) }.map { Bot(it.getString("id"), it.getString("name")) }
    }

    /** The chosen bot, or Jeeves (or the only bot) when none is chosen yet. */
    fun mainBot(): Bot {
        if (Store.botId.isNotEmpty()) return Bot(Store.botId, Store.botName)
        val bots = listBots()
        val bot = (if (bots.size == 1) bots[0] else bots.firstOrNull { it.name.startsWith("Jeeves", true) })
            ?: throw GrokException(if (bots.isEmpty()) "Your Grok Bot account has no bots." else "Pick your bot in Grok Voice first.")
        Store.botId = bot.id
        Store.botName = bot.name
        return bot
    }

    /** Sends a prompt; returns the message id for [replies]. Never retried, since it may have landed. */
    fun send(botId: String, text: String): String {
        val messageId = UUID.randomUUID().toString()
        val body = JSONObject()
            .put("agentId", botId)
            .put("messageId", messageId)
            .put("text", text)
            .put("sentAtMs", System.currentTimeMillis().toString())
            .put("isFork", false)
        val result = backend("SendGrokBotUserMessage", body, mutation = true)
        if (result.has("refusal")) throw GrokException("Grok Bot refused the instruction: ${result.opt("refusal")}")
        return messageId
    }

    /**
     * The bot's text replies to a message, polling its transcript: our message appears with its id
     * as clientNonce, and the replies are send-message entries with the same requestId.
     */
    fun replies(botId: String, messageId: String, seen: MutableSet<String>): List<String> {
        val value = backend("ListGrokBotTranscriptEntries", JSONObject().put("agentId", botId).put("limit", 12).put("sessionId", ""), mutation = false)
        val raw = value.optJSONArray("entries") ?: return emptyList()
        val entries = (0 until raw.length()).mapNotNull { i ->
            val e = raw.getJSONObject(i)
            try {
                JSONObject(String(Base64.decode(e.getString("body"), Base64.DEFAULT))).put("seq", e.optString("seq").toLongOrNull() ?: 0L)
            } catch (_: Exception) {
                null
            }
        }.sortedBy { it.optLong("seq") }
        val requestId = entries.firstOrNull { it.optString("clientNonce") == messageId }?.optString("requestId") ?: return emptyList()
        val out = mutableListOf<String>()
        for (e in entries) {
            if (e.optString("kind") != "send-message" || e.optString("requestId") != requestId) continue
            if (!seen.add(e.optString("id"))) continue
            val m = e.optJSONObject("message") ?: continue
            if (m.optString("type") == "text" && m.optString("content").isNotEmpty()) out += m.optString("content")
        }
        return out
    }

    private fun backend(method: String, body: JSONObject, mutation: Boolean): JSONObject {
        var access = Store.botAccess ?: throw GrokException("Sign in to Grok Bot in Grok Voice first.")
        for (attempt in 0..1) {
            val conn = (URL("$BACKEND/aiserver.v1.GrokBotService/$method").openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                doOutput = true
                connectTimeout = 10_000
                readTimeout = 30_000
                setRequestProperty("Authorization", "Bearer $access")
                setRequestProperty("Content-Type", "application/json")
                setRequestProperty("x-cursor-client-type", "sand")
                setRequestProperty("x-cursor-client-source", "sand-desktop")
                setRequestProperty("x-cursor-client-version", VERSION)
                setRequestProperty("x-sand-box-namespace", "prod")
                setRequestProperty("Connect-Protocol-Version", "1")
            }
            val (status, text) = try {
                Grok.send(conn, body.toString().toByteArray())
            } catch (e: Exception) {
                throw GrokException(if (mutation) "Couldn't tell whether ${Store.botLabel} got it. Check Grok Bot before sending again." else "Couldn't reach Grok Bot.")
            }
            // An expired sign-in is refused before anything happens, so renewing and retrying is safe.
            if (status == 401 && attempt == 0 && !text.contains("no longer supported", true)) {
                access = refresh()
                continue
            }
            if (status !in 200..299) throw GrokException(
                when {
                    text.contains("no longer supported", true) -> "Grok Bot's server wants a newer app version (Grok Voice sends $VERSION)."
                    mutation && status >= 500 -> "Couldn't tell whether ${Store.botLabel} got it. Check Grok Bot before sending again."
                    status == 401 || status == 403 -> "Grok Bot refused the request. Sign in again in Grok Voice."
                    else -> "Grok Bot returned HTTP $status."
                }
            )
            return if (text.isBlank()) JSONObject() else JSONObject(text)
        }
        throw GrokException("Grok Bot refused the sign-in.")
    }

    private fun refresh(): String {
        val refresh = Store.botRefresh ?: throw GrokException("Sign in to Grok Bot again in Grok Voice.")
        val conn = (URL("$BACKEND/oauth/token").openConnection() as HttpURLConnection).apply {
            requestMethod = "POST"
            doOutput = true
            setRequestProperty("Content-Type", "application/json")
        }
        val body = JSONObject().put("client_id", CLIENT_ID).put("grant_type", "refresh_token").put("refresh_token", refresh)
        val (status, text) = Grok.send(conn, body.toString().toByteArray())
        if (status !in 200..299) throw GrokException("Your Grok Bot sign-in expired. Sign in again in Grok Voice.")
        val json = JSONObject(text)
        val access = json.getString("access_token")
        Store.botAccess = access
        json.optString("refresh_token").takeIf { it.isNotEmpty() }?.let { Store.botRefresh = it }
        return access
    }

    private fun b64url(bytes: ByteArray) = Base64.encodeToString(bytes, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
}
