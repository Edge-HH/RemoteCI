package com.remoteci.watch.data

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import com.remoteci.watch.BuildConfig
import com.remoteci.watch.notif.UpdateReceiver

/** GitHub release 响应中与本应用相关的字段。 */
@Serializable
data class GitHubRelease(
    @SerialName("tag_name") val tagName: String = "",
    @SerialName("name") val name: String? = null,
    @SerialName("body") val body: String? = null,
    @SerialName("prerelease") val prerelease: Boolean = false,
    @SerialName("draft") val draft: Boolean = false,
    @SerialName("assets") val assets: List<GitHubAsset> = emptyList(),
)

@Serializable
data class GitHubAsset(
    @SerialName("name") val name: String = "",
    @SerialName("browser_download_url") val browserDownloadUrl: String = "",
    @SerialName("size") val size: Long = 0,
)

data class CompatibleUpdate(
    val release: GitHubRelease,
    val asset: GitHubAsset,
)

enum class UpdateChannel
{
    STABLE,
    BETA,
}

/**
 * 手表端更新入口：从 GitHub 仓库发布列表选择 APK 并安装。
 *
 * 安装沿用 [PackageInstaller] 官方机制，要求发布 APK 与当前安装包签名一致，
 * 首次安装正式签名版后后续更新即可在同一签名下自动覆盖。
 */
object UpdateManager {
    private const val REPO = "Edge-HH/RemoteCI"
    private const val API_RELEASES = "https://api.github.com/repos/$REPO/releases?per_page=20"
    private val USER_AGENT = "RemoteCI-Watch/${BuildConfig.VERSION_NAME}"
    private val STABLE_RELEASE_TAG = Regex("""^3\.[0-9]+\.[0-9]+\.[0-9]+$""")
    private val BETA_RELEASE_TAG = Regex("""^v3\.[0-9]+\.[0-9]+-beta\.[1-9][0-9]*$""")
    const val INSTALL_RESULT_ACTION = "com.remoteci.watch.UPDATE_INSTALL_RESULT"

    private val json = Json { ignoreUnknownKeys = true }
    private val okHttp = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .build()

    /** 拉取最近发布列表，选择当前协议代内最高兼容手表版本。 */
    suspend fun fetchReleases(): List<GitHubRelease> = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url(API_RELEASES)
            .header("User-Agent", USER_AGENT)
            .build()
        okHttp.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IOException("检查更新失败（HTTP ${response.code}）")
            json.decodeFromString<List<GitHubRelease>>(response.body.string())
        }
    }

    /** 在 release 附件中寻找与该 tag 完全对应的手表 APK。 */
    fun findApkAsset(release: GitHubRelease): GitHubAsset? {
        val expectedName = "RemoteCI.Watch-${versionFromTag(release.tagName)}.apk"
        return release.assets.firstOrNull { it.name == expectedName }
    }

    /** 按渠道选择同一 V3 协议代内可升级或可强制覆盖的最高版本。 */
    fun selectCompatibleUpdate(
        releases: List<GitHubRelease>,
        currentVersion: String,
        channel: UpdateChannel = UpdateChannel.STABLE,
        force: Boolean = false,
    ): CompatibleUpdate? = releases
        .filterNot { it.draft }
        .filter { release ->
            val stable = STABLE_RELEASE_TAG.matches(release.tagName) && !release.prerelease
            val beta = BETA_RELEASE_TAG.matches(release.tagName) && release.prerelease
            if (channel == UpdateChannel.STABLE) stable else stable || beta
        }
        .mapNotNull { release -> findApkAsset(release)?.let { CompatibleUpdate(release, it) } }
        .filter { candidate ->
            val version = versionFromTag(candidate.release.tagName)
            val currentComparison = compareVersions(version, currentVersion)
            (currentComparison > 0 || force && currentComparison == 0) &&
                version.substringBefore("-").substringBefore(".").toIntOrNull() == Protocol.VERSION
        }
        .maxWithOrNull { left, right ->
            compareVersions(left.release.tagName, right.release.tagName)
        }

    /** 去掉 tag 前缀 v，得到可比较的版本号。 */
    fun versionFromTag(tag: String): String = tag.removePrefix("v").trim()

    /** latest > current 时返回 true。 */
    fun isNewer(latest: String, current: String): Boolean = compareVersions(latest, current) > 0

    /** 语义版本比较，正式版在相同核心版本的预发布版之后。 */
    fun compareVersions(a: String, b: String): Int {
        val left = parseVersion(a)
        val right = parseVersion(b)
        val size = maxOf(left.core.size, right.core.size)
        for (i in 0 until size) {
            val comparison = left.core.getOrElse(i) { 0 }.compareTo(right.core.getOrElse(i) { 0 })
            if (comparison != 0) return comparison
        }

        if (left.prerelease.isEmpty()) return if (right.prerelease.isEmpty()) 0 else 1
        if (right.prerelease.isEmpty()) return -1
        for (i in 0 until maxOf(left.prerelease.size, right.prerelease.size)) {
            if (i >= left.prerelease.size) return -1
            if (i >= right.prerelease.size) return 1
            val leftPart = left.prerelease[i]
            val rightPart = right.prerelease[i]
            val leftNumber = leftPart.toIntOrNull()
            val rightNumber = rightPart.toIntOrNull()
            val comparison = when {
                leftNumber != null && rightNumber != null -> leftNumber.compareTo(rightNumber)
                leftNumber != null -> -1
                rightNumber != null -> 1
                else -> leftPart.compareTo(rightPart)
            }
            if (comparison != 0) return comparison
        }
        return 0
    }

    private data class ParsedVersion(val core: List<Int>, val prerelease: List<String>)

    private fun parseVersion(value: String): ParsedVersion {
        val withoutBuild = value.removePrefix("v").substringBefore("+")
        val core = withoutBuild.substringBefore("-").split(".").map { it.toIntOrNull() ?: 0 }
        val prerelease = withoutBuild.substringAfter("-", "")
            .split(".")
            .filter { it.isNotEmpty() }
        return ParsedVersion(core, prerelease)
    }

    /** 下载 APK 到缓存目录，校验长度后返回本地文件。 */
    suspend fun downloadApk(context: Context, asset: GitHubAsset): File = withContext(Dispatchers.IO) {
        val target = File(context.cacheDir, asset.name)
        if (target.exists()) target.delete()
        val request = Request.Builder()
            .url(asset.browserDownloadUrl)
            .header("User-Agent", USER_AGENT)
            .build()
        okHttp.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IOException("下载失败（HTTP ${response.code}）")
            val expected = asset.size
            val announced = response.body.contentLength()
            if (expected > 0 && announced >= 0 && announced != expected)
                throw IOException("下载长度不匹配：发布清单 $expected 字节，实际 $announced 字节")
            val body = response.body
            body.byteStream().use { input ->
                target.outputStream().use { output -> input.copyTo(output) }
            }
        }
        // 传输中断但连接“正常”关闭时落盘文件会缺尾，落盘后再校验一次；安装器只负责验签不负责验全。
        if (asset.size > 0 && target.length() != asset.size) {
            target.delete()
            throw IOException("APK 下载不完整：预期 ${asset.size} 字节，实际 ${target.length()} 字节")
        }
        target
    }

    /** 通过系统 PackageInstaller 安装，结果由 UpdateReceiver 接收并提示；失败时回收会话。 */
    suspend fun installApk(context: Context, apk: File): Unit = withContext(Dispatchers.IO) {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        params.setAppPackageName(context.packageName)
        val sessionId = installer.createSession(params)
        try {
            val session = installer.openSession(sessionId)
            try {
                // openWrite + copyTo 会同步写入几十 MB，绝不能在主线程执行（ANR 风险）。
                session.openWrite(apk.name, 0, apk.length()).use { output ->
                    apk.inputStream().use { input -> input.copyTo(output) }
                    session.fsync(output)
                }
                val pending = createInstallResultPendingIntent(context, sessionId)
                session.commit(pending.intentSender)
            } finally {
                session.close()
            }
        } catch (error: Exception) {
            // 写入失败或 commit 前异常时释放会话，避免 PackageInstaller 留下僵尸会话占空间。
            runCatching { installer.abandonSession(sessionId) }
            throw error
        }
    }

    /** PackageInstaller 会补充状态字段，因此回调必须可变；显式组件避免可变隐式 Intent 风险。 */
    fun createInstallResultIntent(context: Context): Intent =
        Intent(context, UpdateReceiver::class.java).setAction(INSTALL_RESULT_ACTION)

    fun createInstallResultPendingIntent(context: Context, sessionId: Int): PendingIntent =
        PendingIntent.getBroadcast(
            context,
            sessionId,
            createInstallResultIntent(context),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE,
        )
}
