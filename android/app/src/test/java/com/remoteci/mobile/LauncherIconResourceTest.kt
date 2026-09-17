package com.remoteci.mobile

import java.io.File
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertTrue

class LauncherIconResourceTest {
    private val mainSourceDirectory =
        sequenceOf(File("app/src/main"), File("src/main"))
            .first { it.isDirectory }

    @Test
    fun launcherIconKeepsTransparentPixels() {
        val manifest = File(mainSourceDirectory, "AndroidManifest.xml").readText()
        assertTrue(
            "android:icon=\"@mipmap/ic_launcher_foreground\"" in manifest,
            "应用图标必须直接使用透明 PNG，不能再通过带不透明背景的 adaptive icon 合成。",
        )

        val resourceDirectory = File(mainSourceDirectory, "res")
        val icons =
            resourceDirectory
                .listFiles()
                .orEmpty()
                .filter { it.name.startsWith("mipmap-") }
                .map { File(it, "ic_launcher_foreground.png") }
                .filter { it.isFile }

        assertTrue(icons.isNotEmpty(), "没有找到 Android 启动器图标资源。")
        icons.forEach { icon ->
            val header = icon.inputStream().use { it.readNBytes(26) }
            assertTrue(header.size == 26, "PNG 文件头不完整：${icon.path}")
            assertContentEquals(
                byteArrayOf(-119, 80, 78, 71, 13, 10, 26, 10),
                header.copyOfRange(0, 8),
                "不是有效的 PNG 文件：${icon.path}",
            )
            // PNG 的 IHDR color type 位于第 25 字节；4 和 6 分别表示灰度/RGB 带 Alpha。
            assertTrue(
                header[25].toInt() in setOf(4, 6),
                "图标没有 Alpha 通道：${icon.path}",
            )
        }
    }
}
