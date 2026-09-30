import java.util.Properties

plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.android) apply false
    alias(libs.plugins.kotlin.compose) apply false
    alias(libs.plugins.kotlin.serialization) apply false
}

// 云盘、NAS 按需文件系统会把 build 中的普通文件转换为重解析占位文件，
// Gradle 9 无法对这类输出做增量快照；允许本机把生成目录放到普通本地磁盘。
val localProperties = Properties().apply {
    val file = rootProject.file("local.properties")
    if (file.isFile) file.inputStream().use { load(it) }
}
val externalBuildRootPath = providers.gradleProperty("remoteci.buildDir").orNull
    ?: System.getenv("REMOTECI_WEAROS_BUILD_DIR")
    ?: localProperties.getProperty("remoteci.buildDir")
externalBuildRootPath?.trim()?.takeIf(String::isNotEmpty)?.let { configuredPath ->
    val externalBuildRoot = rootProject.file(configuredPath)
    layout.buildDirectory.set(externalBuildRoot.resolve("root"))
    subprojects {
        val projectDirectoryName = path.removePrefix(":").replace(':', '/')
        layout.buildDirectory.set(externalBuildRoot.resolve(projectDirectoryName))
    }
}
