package dev.dictate.grokvoice

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Settings, with secrets (API key, Grok Bot tokens) encrypted by a key held in the Android Keystore. */
object Store {
    private const val KEY_ALIAS = "grokvoice-secrets"
    private lateinit var prefs: SharedPreferences

    fun init(context: Context) {
        if (!::prefs.isInitialized) prefs = context.applicationContext.getSharedPreferences("grokvoice", Context.MODE_PRIVATE)
    }

    var apiKey: String?
        get() = secret("xai_key")
        set(v) = setSecret("xai_key", v)
    var botAccess: String?
        get() = secret("bot_access")
        set(v) = setSecret("bot_access", v)
    var botRefresh: String?
        get() = secret("bot_refresh")
        set(v) = setSecret("bot_refresh", v)
    var botId: String
        get() = prefs.getString("bot_id", "") ?: ""
        set(v) = prefs.edit().putString("bot_id", v).apply()
    var botName: String
        get() = prefs.getString("bot_name", "") ?: ""
        set(v) = prefs.edit().putString("bot_name", v).apply()
    var cleanUp: Boolean
        get() = prefs.getBoolean("clean_up", true)
        set(v) = prefs.edit().putBoolean("clean_up", v).apply()

    val botLabel get() = botName.ifEmpty { "Jeeves" }.substringBefore(" ·").trim()

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build()
        )
        return gen.generateKey()
    }

    private fun secret(name: String): String? {
        val stored = prefs.getString(name, null) ?: return null
        return try {
            val bytes = Base64.decode(stored, Base64.NO_WRAP)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes, 0, 12))
            String(cipher.doFinal(bytes, 12, bytes.size - 12), Charsets.UTF_8)
        } catch (_: Exception) {
            null
        }
    }

    private fun setSecret(name: String, value: String?) {
        if (value.isNullOrEmpty()) {
            prefs.edit().remove(name).apply()
            return
        }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val sealed = cipher.iv + cipher.doFinal(value.toByteArray(Charsets.UTF_8))
        prefs.edit().putString(name, Base64.encodeToString(sealed, Base64.NO_WRAP)).apply()
    }
}
