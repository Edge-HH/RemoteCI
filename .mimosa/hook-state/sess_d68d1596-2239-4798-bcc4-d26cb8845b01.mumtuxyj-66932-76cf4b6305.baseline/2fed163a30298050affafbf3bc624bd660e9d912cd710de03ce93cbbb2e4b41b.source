package com.remoteci.watch.notif

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.remoteci.watch.R

enum class InstallStatusAction {
    RequestUserConfirmation,
    Success,
    Failure,
}

/**
 * 接收 PackageInstaller 的安装结果并提示用户。
 * Manifest 中声明为不导出的显式结果接收器，回调只能通过本应用创建的 PendingIntent 到达。
 */
class UpdateReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
        when (actionForStatus(status)) {
            InstallStatusAction.RequestUserConfirmation -> {
                val confirmation = userConfirmationIntent(intent)
                if (confirmation != null && runCatching {
                        context.startActivity(confirmation.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    }.isSuccess
                ) return
                notify(context, false, "无法打开系统安装确认界面，请重新尝试。")
            }
            InstallStatusAction.Success -> notify(context, true, message)
            InstallStatusAction.Failure -> notify(context, false, message)
        }
    }

    private fun notify(context: Context, success: Boolean, message: String?) {
        // 进程可能只为接收安装结果而启动，App 尚未创建通知渠道；这里幂等补建，避免 API 26+ 静默丢通知。
        NotificationHelper.ensureChannel(context)
        val title = if (success) "更新完成" else "更新失败"
        val text = when {
            success -> "RemoteCI 已更新到新版本，请重新打开应用。"
            !message.isNullOrBlank() -> message
            else -> "安装未完成，请稍后重试。"
        }
        val notification = NotificationCompat.Builder(context, NotificationHelper.CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(title)
            .setContentText(text)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .build()
        try {
            NotificationManagerCompat.from(context).notify(UPDATE_NOTIFICATION_ID, notification)
        } catch (_: SecurityException) {
            // 未授予通知权限时静默失败，安装结果仍以系统安装页为准。
        }
    }

    companion object {
        fun actionForStatus(status: Int): InstallStatusAction = when (status) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> InstallStatusAction.RequestUserConfirmation
            PackageInstaller.STATUS_SUCCESS -> InstallStatusAction.Success
            else -> InstallStatusAction.Failure
        }

        @Suppress("DEPRECATION")
        private fun userConfirmationIntent(result: Intent): Intent? =
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                result.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
            } else {
                result.getParcelableExtra(Intent.EXTRA_INTENT)
            }

        const val UPDATE_NOTIFICATION_ID = 2026
    }
}
