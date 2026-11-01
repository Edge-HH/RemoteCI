package com.remoteci.mobile.data

import android.content.Context
import android.content.SharedPreferences
import com.remoteci.mobile.ui.SubjectColors
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.Json

/**
 * 学科颜色：按账号保存在服务端（与 WebUI 同步），本机按“服务器 + 账号”缓存一份，离线时仍按上次的配色显示。
 * 进入课表页、下拉刷新与 App 回到前台时调用 [refresh]；改色需要在线。
 */
object SubjectColorStore {
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }
    private var prefs: SharedPreferences? = null
    private var loadedKey: String? = null
    private val _state = MutableStateFlow(SubjectColorsResponse())
    val state: StateFlow<SubjectColorsResponse> = _state

    fun initialize(context: Context) {
        if (prefs == null) prefs = context.applicationContext.getSharedPreferences("remoteci-subject-colors", Context.MODE_PRIVATE)
        loadCached()
    }

    /** 当前账号改过的颜色，键为去首尾空白的学科名。 */
    fun overrides(response: SubjectColorsResponse): Map<String, String> =
        response.items.filter { it.custom }.associate { SubjectColors.key(it.name) to it.color }

    /** 学科的生效基准色：改过的颜色，否则为默认颜色（与服务端算法一致，未同步过也能显示）。 */
    fun colorOf(overrides: Map<String, String>, subject: String?): String =
        overrides[SubjectColors.key(subject)] ?: SubjectColors.default(subject)

    /** 切换账号或服务器后读取对应的缓存；没有缓存时清空，回到默认配色。 */
    fun loadCached() {
        val key = cacheKey()
        if (key == loadedKey) return
        loadedKey = key
        _state.value = key?.let { prefs?.getString(it, null) }
            ?.let { runCatching { json.decodeFromString(SubjectColorsResponse.serializer(), it) }.getOrNull() }
            ?: SubjectColorsResponse()
    }

    /** 从服务端拉取；失败时保留缓存并返回 false。 */
    suspend fun refresh(): Boolean {
        loadCached()
        val response = runCatching { AdminApi.subjectColors() }.getOrNull() ?: return false
        store(response)
        return true
    }

    suspend fun set(name: String, color: String) {
        AdminApi.setSubjectColor(name, color)
        update(name, color, custom = true)
    }

    suspend fun reset(name: String) {
        AdminApi.resetSubjectColor(name)
        update(name, SubjectColors.default(name), custom = false)
    }

    private fun update(name: String, color: String, custom: Boolean) {
        val key = SubjectColors.key(name)
        val current = _state.value
        val items = if (current.items.any { it.name == key }) current.items.map { if (it.name == key) it.copy(color = color, custom = custom) else it }
        else current.items + SubjectColorItem(key, color, custom)
        store(current.copy(items = items))
    }

    private fun store(response: SubjectColorsResponse) {
        _state.value = response
        cacheKey()?.let { prefs?.edit()?.putString(it, json.encodeToString(SubjectColorsResponse.serializer(), response))?.apply() }
    }

    private fun cacheKey(): String? {
        val server = ConnectionManager.restBaseUrl() ?: return null
        val user = ConnectionManager.currentUser.value?.id ?: return null
        return "$server|$user"
    }
}
