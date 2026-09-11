import java.util.Properties

// 软件版本与协议版本独立，Release 工作流会用标签覆盖此默认值。
val releaseVersion = providers.gradleProperty("releaseVersion").orNull ?: "3.2.1.3"

// Android versionCode 必须是正整数且 Beta 版本也要严格排在同核心版本的稳定版之前。
// 版本槽位为 major*100000000 + minor*1000000 + patch*10000 + revision*1000，
// 稳定版使用槽位末尾 999，v3.x.x-beta.y 使用 y（1..998）。
val releaseVersionMatch = Regex(
    """^(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)(?:(?:\.(?<revision>[0-9]+))|(?:-beta\.(?<beta>[0-9]+)))$""",
).matchEntire(releaseVersion)
    ?: error("无效的 Wear OS 版本号：$releaseVersion")

fun versionPart(name: String): Int =
    releaseVersionMatch.groups[name]?.value?.toIntOrNull()
        ?: 0

val releaseVersionCode = run {
    val major = versionPart("major")
    val minor = versionPart("minor")
    val patch = versionPart("patch")
    val revision = versionPart("revision")
    val beta = releaseVersionMatch.groups["beta"]?.value?.toIntOrNull()
    require(minor in 0..999 && patch in 0..999 && revision in 0..999) {
        "版本号分段必须处于 0..999：$releaseVersion"
    }
    val suffix = if (beta != null) {
        require(beta in 1..998) { "Beta 序号必须处于 1..998：$releaseVersion" }
        beta
    } else {
        999
    }
    val code = major.toLong() * 100_000_000L +
        minor.toLong() * 1_000_000L +
        patch.toLong() * 10_000L +
        revision.toLong() * 1_000L +
        suffix
    require(code in 1L..2_100_000_000L) {
        "版本号推导出的 versionCode 超出 Android 范围：$code"
    }
    code.toInt()
}

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// 签名信息从 wearos/keystore.properties（本机）或 CI 环境变量读取；
// 文件与密钥均不入库，缺失时 release 构建产物不签名（仅用于本地调试）。
val keystoreProperties = Properties().apply {
    val file = rootProject.file("keystore.properties")
    if (file.exists()) file.inputStream().use { load(it) }
}
val keystoreFile = System.getenv("ANDROID_KEYSTORE_FILE")?.let { rootProject.file(it) }
    ?: keystoreProperties.getProperty("storeFile")?.let { rootProject.file(it) }
val keystorePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
    ?: keystoreProperties.getProperty("storePassword")
val keystoreKeyAlias = System.getenv("ANDROID_KEY_ALIAS") ?: keystoreProperties.getProperty("keyAlias")
val keystoreKeyPassword = System.getenv("ANDROID_KEY_PASSWORD")
    ?: keystoreProperties.getProperty("keyPassword")

android {
    namespace = "com.remoteci.watch"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.remoteci.watch"
        minSdk = 30
        targetSdk = 37
        versionCode = releaseVersionCode
        versionName = releaseVersion
        testInstrumentationRunner =
            "com.remoteci.watch.NetworkSecurityPolicyInstrumentation"
    }

    signingConfigs {
        create("release") {
            if (keystoreFile != null && keystoreFile.exists()) {
                storeFile = keystoreFile
                storePassword = keystorePassword
                keyAlias = keystoreKeyAlias
                keyPassword = keystoreKeyPassword
            }
        }
    }

    buildTypes {
        release {
            // release 启用 R8 压缩；kotlinx.serialization 保留规则见 proguard-rules.pro。
            isMinifyEnabled = true
            signingConfig = if (keystoreFile != null && keystoreFile.exists()) {
                signingConfigs.getByName("release")
            } else {
                null
            }
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        buildConfig = true
        compose = true
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.foundation)
    implementation(libs.compose.material.icons.extended)
    implementation(libs.compose.material3)
    implementation(libs.wear.compose.material)
    implementation(libs.wear.compose.navigation)
    implementation(libs.activity.compose)
    implementation(libs.lifecycle.viewmodel.compose)
    implementation(libs.lifecycle.runtime.ktx)
    implementation(libs.wear)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.okhttp)
    testImplementation(kotlin("test"))
    testImplementation(libs.mockwebserver3)
    debugImplementation(libs.compose.ui.tooling)
}
