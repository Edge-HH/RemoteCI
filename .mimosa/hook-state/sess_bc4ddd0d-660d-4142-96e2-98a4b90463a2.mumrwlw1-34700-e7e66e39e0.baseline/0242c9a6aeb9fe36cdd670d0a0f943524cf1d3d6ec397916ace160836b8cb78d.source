package com.remoteci.watch.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/** 设备会话持久化的最小接口：生产实现为 Android Keystore 加密的 [SecureSessionStore]，测试注入内存假实现。 */
interface SessionStorage {
    fun save(session: PersistedDeviceSession)
    fun load(): PersistedDeviceSession?
    fun clear()
}

/** 使用 Android Keystore AES-GCM 保护设备会话密钥，设置中永不出现明文密码。 */
class SecureSessionStore(context: Context) : SessionStorage {
    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    private val json = Json { ignoreUnknownKeys = true }

    @Synchronized
    override fun save(session: PersistedDeviceSession) {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        cipher.updateAAD(AAD)
        val encrypted = cipher.doFinal(json.encodeToString(session).encodeToByteArray())
        val value = listOf(
            Base64.encodeToString(cipher.iv, Base64.NO_WRAP),
            Base64.encodeToString(encrypted, Base64.NO_WRAP),
        ).joinToString(".")
        prefs.edit().putString(KEY_SESSION, value).apply()
    }

    @Synchronized
    override fun load(): PersistedDeviceSession? {
        val parts = prefs.getString(KEY_SESSION, null)?.split('.') ?: return null
        if (parts.size != 2) return null
        return runCatching {
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(
                Cipher.DECRYPT_MODE,
                getOrCreateKey(),
                GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)),
            )
            cipher.updateAAD(AAD)
            json.decodeFromString<PersistedDeviceSession>(
                cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)).decodeToString(),
            )
        }.getOrElse {
            clear()
            null
        }
    }

    override fun clear() {
        prefs.edit().remove(KEY_SESSION).apply()
    }

    private fun getOrCreateKey(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(
                KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                )
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .build(),
            )
            generateKey()
        }
    }

    private companion object {
        const val PREFS_NAME = "remoteci_secure_session"
        const val KEY_SESSION = "session"
        const val KEY_ALIAS = "RemoteCI.DeviceSession.v2"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        val AAD = "com.remoteci.watch:device-session:v2".encodeToByteArray()
    }
}
