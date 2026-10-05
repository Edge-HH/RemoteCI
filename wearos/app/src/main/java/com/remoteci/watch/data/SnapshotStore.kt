package com.remoteci.watch.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.core.content.edit
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/** 持久化最后一次有效课表，让手表在断网时仍能展示最近课程。课表内容使用 Keystore 加密。 */
class SnapshotStore(context: Context) {
    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    private val json = Json { ignoreUnknownKeys = true }

    @Synchronized
    fun load(): ClassStateSnapshot? = decode(prefs.getString(KEY_SNAPSHOT, null))

    @Synchronized
    fun save(snapshot: ClassStateSnapshot) = prefs.edit { putString(KEY_SNAPSHOT, encode(json.encodeToString(snapshot))) }

    @Synchronized
    fun loadSchedule(): ScheduleBundle? = decode(prefs.getString(KEY_SCHEDULE, null))

    @Synchronized
    fun saveSchedule(schedule: ScheduleBundle) = prefs.edit { putString(KEY_SCHEDULE, encode(json.encodeToString(schedule))) }

    @Synchronized
    fun clear() = prefs.edit { clear() }

    private inline fun <reified T> decode(value: String?): T? {
        if (value.isNullOrBlank()) return null
        return runCatching {
            val parts = value.split('.', limit = 2)
            require(parts.size == 2)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(
                Cipher.DECRYPT_MODE,
                getOrCreateKey(),
                GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)),
            )
            cipher.updateAAD(AAD)
            json.decodeFromString<T>(cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)).decodeToString())
        }.getOrElse {
            clear()
            null
        }
    }

    private fun encode(value: String): String {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        cipher.updateAAD(AAD)
        return listOf(
            Base64.encodeToString(cipher.iv, Base64.NO_WRAP),
            Base64.encodeToString(cipher.doFinal(value.encodeToByteArray()), Base64.NO_WRAP),
        ).joinToString(".")
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
        const val PREFS_NAME = "remoteci_snapshot"
        const val KEY_SNAPSHOT = "latest"
        const val KEY_SCHEDULE = "schedule"
        const val KEY_ALIAS = "RemoteCI.Snapshot.v1"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        val AAD = "com.remoteci.watch:snapshot:v1".encodeToByteArray()
    }
}
