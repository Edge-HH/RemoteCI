package com.remoteci.mobile.ui

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals

class RemoteCiAppTest {
    @Test
    fun `home tabs share transition key so bottom navigation stays fixed`() {
        val keys = HomeTab.entries.map { screenTransitionKey(Screen.Home(it)) }.toSet()

        assertEquals(setOf("home"), keys)
        assertNotEquals(screenTransitionKey(Screen.Account), screenTransitionKey(Screen.Home()))
    }
}
