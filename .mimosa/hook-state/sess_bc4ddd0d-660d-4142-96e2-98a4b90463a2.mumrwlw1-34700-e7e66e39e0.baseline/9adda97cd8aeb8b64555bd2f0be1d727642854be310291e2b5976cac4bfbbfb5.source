package com.remoteci.watch.notif

import android.content.pm.PackageInstaller
import kotlin.test.Test
import kotlin.test.assertEquals

class UpdateReceiverTest {
    @Test
    fun `pending user action opens system installer confirmation`() {
        assertEquals(
            InstallStatusAction.RequestUserConfirmation,
            UpdateReceiver.actionForStatus(PackageInstaller.STATUS_PENDING_USER_ACTION),
        )
    }

    @Test
    fun `terminal package installer statuses become success or failure`() {
        assertEquals(
            InstallStatusAction.Success,
            UpdateReceiver.actionForStatus(PackageInstaller.STATUS_SUCCESS),
        )
        assertEquals(
            InstallStatusAction.Failure,
            UpdateReceiver.actionForStatus(PackageInstaller.STATUS_FAILURE_INVALID),
        )
    }
}
