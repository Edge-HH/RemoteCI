package com.remoteci.mobile

import android.Manifest
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import android.content.pm.PackageManager
import com.remoteci.mobile.data.ConnectionManager
import com.remoteci.mobile.data.hasLocalNetworkPermission
import com.remoteci.mobile.notif.NotificationHelper
import com.remoteci.mobile.ui.RemoteCiApp
import kotlinx.coroutines.flow.MutableStateFlow

class MainActivity : ComponentActivity() {
    companion object {
        /** 通知点击请求打开的页面（如换课页），界面层消费后置空。 */
        val openRequests = MutableStateFlow<String?>(null)
    }

    // 先完成本地网络授权再创建应用界面，防止自动续登在系统授权框出现前就发出请求。
    private val localNetworkPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) {
        showApp()
    }
    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        NotificationHelper.ensureChannel(this)
        ConnectionManager.initialize(applicationContext)
        intent?.getStringExtra(NotificationHelper.EXTRA_OPEN)?.let { openRequests.value = it }
        if (!hasLocalNetworkPermission(this)) {
            localNetworkPermission.launch(Manifest.permission.ACCESS_LOCAL_NETWORK)
        } else {
            showApp()
        }
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        intent.getStringExtra(NotificationHelper.EXTRA_OPEN)?.let { openRequests.value = it }
    }

    private fun showApp() {
        // 即使用户拒绝本地网络权限，仍允许进入应用使用公网云端连接。
        setContent { RemoteCiApp(applicationContext) }
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }
}
