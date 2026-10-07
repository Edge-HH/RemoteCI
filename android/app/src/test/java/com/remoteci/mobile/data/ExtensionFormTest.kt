package com.remoteci.mobile.data

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ExtensionFormTest {
    private val parameters = listOf(
        ExtensionParameter("count", "次数", Protocol.EXT_PARAM_NUMBER, min = 1.0, max = 5.0),
        ExtensionParameter("loud", "大声", Protocol.EXT_PARAM_SWITCH),
        ExtensionParameter(
            "voice", "音色", Protocol.EXT_PARAM_SELECT,
            required = true, options = listOf("standard", "soft"), optionLabels = listOf("标准", "柔和"),
        ),
    )

    @Test
    fun `switch defaults to false and select shows display labels`() {
        val args = initialExtensionArgs(parameters)

        assertEquals("false", args["loud"])
        assertEquals("柔和", parameters[2].optionLabel(1))
        assertEquals("standard", parameters[2].copy(optionLabels = listOf("只有一个")).optionLabel(0))
    }

    @Test
    fun `validation mirrors server rules`() {
        val base = initialExtensionArgs(parameters)

        assertEquals("请填写“音色”", validateExtensionArgs(parameters, base))
        assertEquals("“次数”超出允许范围", validateExtensionArgs(parameters, base + ("voice" to "soft") + ("count" to "9")))
        assertEquals("“音色”不是有效选项", validateExtensionArgs(parameters, base + ("voice" to "柔和")))
        assertNull(validateExtensionArgs(parameters, base + ("voice" to "soft") + ("count" to "3")))
    }
}
