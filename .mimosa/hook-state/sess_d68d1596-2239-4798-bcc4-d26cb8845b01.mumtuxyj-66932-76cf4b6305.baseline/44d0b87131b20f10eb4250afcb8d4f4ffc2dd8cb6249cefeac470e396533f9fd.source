package com.remoteci.watch.ui

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import com.remoteci.watch.data.ClassStateSnapshot
import com.remoteci.watch.data.ConnectionManager
import com.remoteci.watch.data.Protocol
import com.remoteci.watch.data.UserProfile
import java.time.LocalTime
import java.time.format.DateTimeFormatter

/** Debug 专用视觉验收入口，不会进入 release APK。 */
class HomeVisualQaActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val snapshot = when (intent.getStringExtra(EXTRA_STATE)) {
            STATE_AFTER_SCHOOL -> ClassStateSnapshot(currentState = Protocol.STATE_AFTER_SCHOOL)
            else -> timedSnapshot()
        }
        setContent {
            AppTheme {
                HomeScreen(
                    connectionState = ConnectionManager.State.CloudConnected,
                    snapshot = snapshot,
                    user = UserProfile(role = Protocol.ROLE_ADMIN, permissions = 127),
                    onOpenScheduleOverview = {},
                    onOpenScheduleChange = {},
                    onQuickSwapCourse = null,
                    onQuickSwapNextCourse = null,
                    onOpenNotification = {},
                    onOpenSettings = {},
                    onRetryConnection = {},
                )
            }
        }
    }

    private fun timedSnapshot(): ClassStateSnapshot {
        val now = LocalTime.now()
        // 避免跨过午夜后仅按时分解析导致进度区间倒置。
        val start = if (now.isBefore(LocalTime.of(0, 15))) LocalTime.MIN else now.minusMinutes(15)
        val end = if (now.isAfter(LocalTime.of(23, 14))) LocalTime.of(23, 59) else now.plusMinutes(45)
        val formatter = DateTimeFormatter.ofPattern("HH:mm")
        return ClassStateSnapshot(
            currentSubject = "数学",
            nextClassSubject = "物理",
            currentState = Protocol.STATE_CLASS,
            currentTimeLayoutItem = "${start.format(formatter)}-${end.format(formatter)} 数学",
            nextClassTimeLayoutItem = "14:00-15:00 物理",
            isClassPlanEnabled = true,
            isClassPlanLoaded = true,
            lessonConfirmed = true,
        )
    }

    private companion object {
        const val EXTRA_STATE = "state"
        const val STATE_AFTER_SCHOOL = "after_school"
    }
}
