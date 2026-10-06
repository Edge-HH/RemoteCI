package com.remoteci.mobile.data

import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class NotificationRequestTest {
    @Test
    fun `rolling is suggested only for long body while rolling is off`() {
        val thirty = "字".repeat(NotificationRequest.ROLLING_SUGGESTION_THRESHOLD)

        assertFalse(NotificationRequest.shouldSuggestRolling(thirty, isRollingEnabled = false))
        // 首尾空白不计入字数，与发送时 trim 后的正文一致。
        assertFalse(NotificationRequest.shouldSuggestRolling("  $thirty  ", isRollingEnabled = false))
        assertTrue(NotificationRequest.shouldSuggestRolling(thirty + "多", isRollingEnabled = false))
        assertFalse(NotificationRequest.shouldSuggestRolling(thirty + "多", isRollingEnabled = true))
    }

    @Test
    fun `full notification options use the server field names`() {
        val request = NotificationRequest(
            title = "标题",
            message = "正文",
            isNotificationTopmostEnabled = true,
            durationSeconds = 8,
            repeatCounts = 3,
            isRollingEnabled = true,
        )

        val encoded = protocolJson.encodeToJsonElement(NotificationRequest.serializer(), request).jsonObject

        assertTrue(encoded.getValue("isNotificationTopmostEnabled").jsonPrimitive.boolean)
        assertEquals(8, encoded.getValue("durationSeconds").jsonPrimitive.int)
        assertEquals(3, encoded.getValue("repeatCounts").jsonPrimitive.int)
        assertTrue(encoded.getValue("isRollingEnabled").jsonPrimitive.boolean)
    }
}
