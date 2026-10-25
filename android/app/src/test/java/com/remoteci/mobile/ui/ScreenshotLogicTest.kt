package com.remoteci.mobile.ui

import com.remoteci.mobile.data.Protocol
import com.remoteci.mobile.data.ScreenshotInfo
import com.remoteci.mobile.data.protocolJson
import java.time.ZoneId
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ScreenshotLogicTest {
    private val shanghai = ZoneId.of("Asia/Shanghai")

    @Test
    fun `server screenshot json decodes and formats caption`() {
        val info = protocolJson.decodeFromString(
            ScreenshotInfo.serializer(),
            """{"id":"0123456789abcdef0123456789abcdef","classId":"c1","deviceName":"一班电脑","requestedBy":"王老师",
               "width":1920,"height":1080,"size":1572864,"capturedAt":"2026-10-25T02:03:04+00:00",
               "expiresAt":"2026-10-26T02:03:04+00:00","url":"/api/screenshots/0123456789abcdef0123456789abcdef"}""",
        )
        assertEquals("/api/screenshots/0123456789abcdef0123456789abcdef", info.url)
        assertEquals("10月25日 10:03:04 · 一班电脑 · 1920×1080 · 1.5 MB · 王老师", screenshotCaption(info, shanghai))
        assertEquals("screenshot-20261025-100304.png", screenshotFileName(info, shanghai))
    }

    @Test
    fun `caption skips missing parts and file name falls back to id`() {
        val info = ScreenshotInfo(id = "abcdef0123456789", size = 300, capturedAt = "bad")
        assertEquals("1 KB", screenshotCaption(info, shanghai))
        assertEquals("screenshot-abcdef01.png", screenshotFileName(info, shanghai))
    }

    @Test
    fun `screen capture matches server permission bit and capability`() {
        assertEquals(1 shl 15, Protocol.PERMISSION_CAPTURE_SCREEN)
        assertTrue(Protocol.CAP_SCREEN_CAPTURE in Protocol.CURRENT_CAPABILITIES)
    }
}
