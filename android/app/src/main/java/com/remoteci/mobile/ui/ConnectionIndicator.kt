package com.remoteci.mobile.ui

import com.remoteci.mobile.data.ConnectionManager

/** “今天”页标题旁云朵图标的状态：每种连接状态对应一个图标和一句说明。 */
internal enum class ConnectionIndicator(val label: String) {
    Cloud("已连接 RemoteCI 服务器（云端）"),
    Lan("已连接 RemoteCI 服务器（局域网）"),
    Connecting("正在连接…"),
    Error("连接失败"),
    Offline("未连接"),
}

internal fun connectionIndicator(state: ConnectionManager.State): ConnectionIndicator = when (state) {
    ConnectionManager.State.CloudConnected -> ConnectionIndicator.Cloud
    ConnectionManager.State.LanConnected -> ConnectionIndicator.Lan
    ConnectionManager.State.Connecting -> ConnectionIndicator.Connecting
    is ConnectionManager.State.Error -> ConnectionIndicator.Error
    ConnectionManager.State.Idle -> ConnectionIndicator.Offline
}
