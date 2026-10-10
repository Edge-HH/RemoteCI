package com.remoteci.mobile.ui

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class WebQrLoginTest {
    @Test
    fun `same server ignores scheme default ports trailing slash and case`() {
        assertTrue(sameServer("https://CI.example.com/", "https://ci.example.com"))
        assertTrue(sameServer("https://ci.example.com:443", "https://ci.example.com"))
        assertTrue(sameServer("http://192.168.1.2:8080", "http://192.168.1.2:8080/"))
        assertFalse(sameServer("http://192.168.1.2:8080", "https://ci.example.com"))
        assertFalse(sameServer("not a url", "https://ci.example.com"))
    }
}
