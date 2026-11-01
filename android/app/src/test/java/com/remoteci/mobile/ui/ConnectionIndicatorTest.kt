package com.remoteci.mobile.ui

import com.remoteci.mobile.data.ConnectionManager
import kotlin.test.Test
import kotlin.test.assertEquals

class ConnectionIndicatorTest {
    @Test
    fun `each connection state maps to its own cloud indicator`() {
        assertEquals(ConnectionIndicator.Cloud, connectionIndicator(ConnectionManager.State.CloudConnected))
        assertEquals(ConnectionIndicator.Lan, connectionIndicator(ConnectionManager.State.LanConnected))
        assertEquals(ConnectionIndicator.Connecting, connectionIndicator(ConnectionManager.State.Connecting))
        assertEquals(ConnectionIndicator.Error, connectionIndicator(ConnectionManager.State.Error("超时")))
        assertEquals(ConnectionIndicator.Offline, connectionIndicator(ConnectionManager.State.Idle))
    }
}
