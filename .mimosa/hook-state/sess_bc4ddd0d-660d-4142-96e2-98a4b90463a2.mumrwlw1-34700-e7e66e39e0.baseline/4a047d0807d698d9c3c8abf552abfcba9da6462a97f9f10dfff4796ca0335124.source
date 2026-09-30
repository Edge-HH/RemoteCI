package com.remoteci.watch

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.remoteci.watch.notif.NotificationHelper
import com.remoteci.watch.ui.RemoteCiApp

/** Wear OS 入口：申请通知权限后进入主界面。 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        NotificationHelper.ensureChannel(this)

        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            ActivityCompat.requestPermissions(
                this,
                arrayOf(Manifest.permission.POST_NOTIFICATIONS),
                REQUEST_NOTIFICATION_PERMISSION,
            )
        }

        // 主题由 RemoteCiApp 根据“设置 → 外观”选中的配色统一提供。
        setContent {
            RemoteCiApp(applicationContext)
        }
    }

    private companion object {
        const val REQUEST_NOTIFICATION_PERMISSION = 1001
    }
}
