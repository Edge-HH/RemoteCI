package com.remoteci.watch

import android.app.Activity
import android.app.Instrumentation
import android.os.Bundle
import android.os.Build
import android.security.NetworkSecurityPolicy
import com.remoteci.watch.data.UpdateManager
import com.remoteci.watch.notif.UpdateReceiver

/**
 * 在目标应用进程中读取 Android 网络安全策略，不依赖额外测试框架。
 * 命令输出的 cleartextPermitted 字段可作为局域网 ws:// 回归测试信号。
 */
class NetworkSecurityPolicyInstrumentation : Instrumentation() {
    override fun onCreate(arguments: Bundle?) {
        super.onCreate(arguments)
        start()
    }

    override fun onStart() {
        val permitted = NetworkSecurityPolicy.getInstance()
            .isCleartextTrafficPermitted(LAN_TEST_HOST)
        val installIntent = UpdateManager.createInstallResultIntent(targetContext)
        val installCallback = UpdateManager.createInstallResultPendingIntent(targetContext, 42)
        val callbackMutable = Build.VERSION.SDK_INT < Build.VERSION_CODES.S || !installCallback.isImmutable
        val callbackExplicit = installIntent.component?.className == UpdateReceiver::class.java.name
        val passed = permitted && callbackMutable && callbackExplicit
        finish(
            if (passed) Activity.RESULT_OK else Activity.RESULT_CANCELED,
            Bundle().apply {
                putString("cleartextPermitted", permitted.toString())
                putString("testedHost", LAN_TEST_HOST)
                putString("installCallbackMutable", callbackMutable.toString())
                putString("installCallbackExplicit", callbackExplicit.toString())
            },
        )
    }

    private companion object {
        const val LAN_TEST_HOST = "192.168.1.100"
    }
}
