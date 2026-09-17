package com.remoteci.mobile.ui

import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.Announcement
import androidx.compose.material.icons.automirrored.rounded.Assignment
import androidx.compose.material.icons.automirrored.rounded.EventNote
import androidx.compose.material.icons.automirrored.rounded.Grading
import androidx.compose.material.icons.automirrored.rounded.Help
import androidx.compose.material.icons.automirrored.rounded.LibraryBooks
import androidx.compose.material.icons.automirrored.rounded.Login
import androidx.compose.material.icons.automirrored.rounded.Logout
import androidx.compose.material.icons.automirrored.rounded.MenuBook
import androidx.compose.material.icons.automirrored.rounded.ScreenShare
import androidx.compose.material.icons.automirrored.rounded.VolumeDown
import androidx.compose.material.icons.automirrored.rounded.VolumeMute
import androidx.compose.material.icons.automirrored.rounded.VolumeOff
import androidx.compose.material.icons.automirrored.rounded.VolumeUp
import androidx.compose.material.icons.rounded.AccountCircle
import androidx.compose.material.icons.rounded.AcUnit
import androidx.compose.material.icons.rounded.AdminPanelSettings
import androidx.compose.material.icons.rounded.Air
import androidx.compose.material.icons.rounded.Alarm
import androidx.compose.material.icons.rounded.Autorenew
import androidx.compose.material.icons.rounded.BatterySaver
import androidx.compose.material.icons.rounded.Bluetooth
import androidx.compose.material.icons.rounded.Bolt
import androidx.compose.material.icons.rounded.BroadcastOnHome
import androidx.compose.material.icons.rounded.Cached
import androidx.compose.material.icons.rounded.CalendarMonth
import androidx.compose.material.icons.rounded.Campaign
import androidx.compose.material.icons.rounded.Cancel
import androidx.compose.material.icons.rounded.Casino
import androidx.compose.material.icons.rounded.Cast
import androidx.compose.material.icons.rounded.CastConnected
import androidx.compose.material.icons.rounded.CastForEducation
import androidx.compose.material.icons.rounded.Celebration
import androidx.compose.material.icons.rounded.Chat
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.Class
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.CloudDownload
import androidx.compose.material.icons.rounded.CloudUpload
import androidx.compose.material.icons.rounded.Code
import androidx.compose.material.icons.rounded.DoNotDisturb
import androidx.compose.material.icons.rounded.Download
import androidx.compose.material.icons.rounded.EditNotifications
import androidx.compose.material.icons.rounded.Email
import androidx.compose.material.icons.rounded.Emergency
import androidx.compose.material.icons.rounded.Error
import androidx.compose.material.icons.rounded.FitnessCenter
import androidx.compose.material.icons.rounded.Fullscreen
import androidx.compose.material.icons.rounded.Group
import androidx.compose.material.icons.rounded.Groups
import androidx.compose.material.icons.rounded.Headphones
import androidx.compose.material.icons.rounded.Lightbulb
import androidx.compose.material.icons.rounded.Link
import androidx.compose.material.icons.rounded.LocalHospital
import androidx.compose.material.icons.rounded.Lock
import androidx.compose.material.icons.rounded.LockOpen
import androidx.compose.material.icons.rounded.Mic
import androidx.compose.material.icons.rounded.MicOff
import androidx.compose.material.icons.rounded.Monitor
import androidx.compose.material.icons.rounded.Nightlight
import androidx.compose.material.icons.rounded.NotificationImportant
import androidx.compose.material.icons.rounded.NotificationsActive
import androidx.compose.material.icons.rounded.NotificationsOff
import androidx.compose.material.icons.rounded.NotificationsPaused
import androidx.compose.material.icons.rounded.PauseCircle
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.PlayCircle
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.PresentToAll
import androidx.compose.material.icons.rounded.Quiz
import androidx.compose.material.icons.rounded.RestartAlt
import androidx.compose.material.icons.rounded.Restaurant
import androidx.compose.material.icons.rounded.RingVolume
import androidx.compose.material.icons.rounded.Router
import androidx.compose.material.icons.rounded.Save
import androidx.compose.material.icons.rounded.Schedule
import androidx.compose.material.icons.rounded.School
import androidx.compose.material.icons.rounded.Science
import androidx.compose.material.icons.rounded.Security
import androidx.compose.material.icons.rounded.SensorDoor
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.SettingsPower
import androidx.compose.material.icons.rounded.Shield
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.material.icons.rounded.SmartDisplay
import androidx.compose.material.icons.rounded.Sms
import androidx.compose.material.icons.rounded.Speaker
import androidx.compose.material.icons.rounded.SwapHoriz
import androidx.compose.material.icons.rounded.Sync
import androidx.compose.material.icons.rounded.SystemUpdate
import androidx.compose.material.icons.rounded.Terminal
import androidx.compose.material.icons.rounded.Thermostat
import androidx.compose.material.icons.rounded.Timer
import androidx.compose.material.icons.rounded.Today
import androidx.compose.material.icons.rounded.Translate
import androidx.compose.material.icons.rounded.Tune
import androidx.compose.material.icons.rounded.Tv
import androidx.compose.material.icons.rounded.Upload
import androidx.compose.material.icons.rounded.VerifiedUser
import androidx.compose.material.icons.rounded.Visibility
import androidx.compose.material.icons.rounded.VisibilityOff
import androidx.compose.material.icons.rounded.Warning
import androidx.compose.material.icons.rounded.Wifi
import androidx.compose.material.icons.rounded.WifiOff
import androidx.compose.ui.graphics.vector.ImageVector

/**
 * 扩展功能图标的归一化解析。
 *
 * Material 图标在 Compose 中是编译期常量，无法按字符串动态查表，因此这里维护显式白名单。
 * 归一化规则：转小写、取最后一个 `.` 之后的部分（兼容 `Icons.Rounded.Foo` 写法）、
 * 去掉所有非字母数字字符。
 */
internal fun normalizeExtensionIconName(icon: String?): String? = icon
    ?.trim()
    ?.lowercase()
    ?.substringAfterLast('.')
    ?.filter { it.isLetterOrDigit() }
    ?.takeIf { it.isNotEmpty() }

/**
 * 解析扩展图标；未知或缺失时返回 null，手表界面回退为纯文字按钮。
 *
 * 历史别名优先于图标本体，保证 3.2 之前已在使用这些名字的插件外观不变。
 */
internal fun extensionIcon(icon: String?): ImageVector? {
    val name = normalizeExtensionIconName(icon) ?: return null
    return LEGACY_EXTENSION_ICONS[name] ?: EXTENSION_ICONS[name]
}

/**
 * 历史别名：这些名字在扩展图标白名单只有 13 项时已经被使用，为避免旧插件失效而保留。
 * 其中 `message`、`notifications`、`power`、`clear` 等与 Material 同名图标语义不同，
 * 这里统一保留历史含义，新插件应优先使用 `[EXTENSION_ICONS]` 中的图标名。
 */
private val LEGACY_EXTENSION_ICONS: Map<String, ImageVector> = mapOf(
    "notification" to Icons.Rounded.EditNotifications,
    "notifications" to Icons.Rounded.EditNotifications,
    "message" to Icons.Rounded.EditNotifications,
    "volume" to Icons.AutoMirrored.Rounded.VolumeUp,
    "power" to Icons.Rounded.PowerSettingsNew,
    "poweroff" to Icons.Rounded.PowerSettingsNew,
    "gear" to Icons.Rounded.Settings,
    "update" to Icons.Rounded.SystemUpdate,
    "restart" to Icons.Rounded.RestartAlt,
    "reboot" to Icons.Rounded.RestartAlt,
    "swap" to Icons.Rounded.SwapHoriz,
    "exchange" to Icons.Rounded.SwapHoriz,
    "connect" to Icons.Rounded.Wifi,
    "show" to Icons.Rounded.Visibility,
    "hide" to Icons.Rounded.VisibilityOff,
    "hidden" to Icons.Rounded.VisibilityOff,
    "clear" to Icons.Rounded.NotificationsOff,
    "clearnotifications" to Icons.Rounded.NotificationsOff,
)

/**
 * 扩展图标白名单；键为 [normalizeExtensionIconName] 归一化后的 Material 图标名。
 * 新增图标时需同步更新 `WatchScreensTest` 与文档站 extensions 说明。
 */
private val EXTENSION_ICONS: Map<String, ImageVector> = mapOf(
    // 教学与班级
    "school" to Icons.Rounded.School,
    "class" to Icons.Rounded.Class,
    "assignment" to Icons.AutoMirrored.Rounded.Assignment,
    "grading" to Icons.AutoMirrored.Rounded.Grading,
    "quiz" to Icons.Rounded.Quiz,
    "menubook" to Icons.AutoMirrored.Rounded.MenuBook,
    "librarybooks" to Icons.AutoMirrored.Rounded.LibraryBooks,
    "castforeducation" to Icons.Rounded.CastForEducation,
    "science" to Icons.Rounded.Science,
    "translate" to Icons.Rounded.Translate,

    // 通知与消息
    "editnotifications" to Icons.Rounded.EditNotifications,
    "notificationsactive" to Icons.Rounded.NotificationsActive,
    "notificationsoff" to Icons.Rounded.NotificationsOff,
    "notificationspaused" to Icons.Rounded.NotificationsPaused,
    "notificationimportant" to Icons.Rounded.NotificationImportant,
    "campaign" to Icons.Rounded.Campaign,
    "announcement" to Icons.AutoMirrored.Rounded.Announcement,
    "chat" to Icons.Rounded.Chat,
    "sms" to Icons.Rounded.Sms,
    "email" to Icons.Rounded.Email,
    "ringvolume" to Icons.Rounded.RingVolume,

    // 显示与投屏
    "cast" to Icons.Rounded.Cast,
    "castconnected" to Icons.Rounded.CastConnected,
    "presenttoall" to Icons.Rounded.PresentToAll,
    "broadcastonhome" to Icons.Rounded.BroadcastOnHome,
    "tv" to Icons.Rounded.Tv,
    "monitor" to Icons.Rounded.Monitor,
    "smartdisplay" to Icons.Rounded.SmartDisplay,
    "fullscreen" to Icons.Rounded.Fullscreen,
    "screenshare" to Icons.AutoMirrored.Rounded.ScreenShare,

    // 设备与环境
    "powersettingsnew" to Icons.Rounded.PowerSettingsNew,
    "settingspower" to Icons.Rounded.SettingsPower,
    "bolt" to Icons.Rounded.Bolt,
    "lightbulb" to Icons.Rounded.Lightbulb,
    "nightlight" to Icons.Rounded.Nightlight,
    "thermostat" to Icons.Rounded.Thermostat,
    "acunit" to Icons.Rounded.AcUnit,
    "sensordoor" to Icons.Rounded.SensorDoor,
    "air" to Icons.Rounded.Air,
    "batterysaver" to Icons.Rounded.BatterySaver,
    "router" to Icons.Rounded.Router,

    // 音视频与媒体
    "volumeup" to Icons.AutoMirrored.Rounded.VolumeUp,
    "volumedown" to Icons.AutoMirrored.Rounded.VolumeDown,
    "volumeoff" to Icons.AutoMirrored.Rounded.VolumeOff,
    "volumemute" to Icons.AutoMirrored.Rounded.VolumeMute,
    "mic" to Icons.Rounded.Mic,
    "micoff" to Icons.Rounded.MicOff,
    "headphones" to Icons.Rounded.Headphones,
    "speaker" to Icons.Rounded.Speaker,
    "playcircle" to Icons.Rounded.PlayCircle,
    "pausecircle" to Icons.Rounded.PauseCircle,

    // 系统与运维
    "settings" to Icons.Rounded.Settings,
    "tune" to Icons.Rounded.Tune,
    "systemupdate" to Icons.Rounded.SystemUpdate,
    "download" to Icons.Rounded.Download,
    "upload" to Icons.Rounded.Upload,
    "cloudupload" to Icons.Rounded.CloudUpload,
    "clouddownload" to Icons.Rounded.CloudDownload,
    "sync" to Icons.Rounded.Sync,
    "restartalt" to Icons.Rounded.RestartAlt,
    "swaphoriz" to Icons.Rounded.SwapHoriz,
    "terminal" to Icons.Rounded.Terminal,
    "code" to Icons.Rounded.Code,
    "save" to Icons.Rounded.Save,

    // 网络与连接
    "wifi" to Icons.Rounded.Wifi,
    "wifioff" to Icons.Rounded.WifiOff,
    "bluetooth" to Icons.Rounded.Bluetooth,
    "link" to Icons.Rounded.Link,

    // 账号与权限
    "person" to Icons.Rounded.Person,
    "group" to Icons.Rounded.Group,
    "groups" to Icons.Rounded.Groups,
    "accountcircle" to Icons.Rounded.AccountCircle,
    "adminpanelsettings" to Icons.Rounded.AdminPanelSettings,
    "security" to Icons.Rounded.Security,
    "shield" to Icons.Rounded.Shield,
    "verifieduser" to Icons.Rounded.VerifiedUser,
    "login" to Icons.AutoMirrored.Rounded.Login,
    "logout" to Icons.AutoMirrored.Rounded.Logout,

    // 状态与提示
    "visibility" to Icons.Rounded.Visibility,
    "visibilityoff" to Icons.Rounded.VisibilityOff,
    "lock" to Icons.Rounded.Lock,
    "lockopen" to Icons.Rounded.LockOpen,
    "checkcircle" to Icons.Rounded.CheckCircle,
    "cancel" to Icons.Rounded.Cancel,
    "close" to Icons.Rounded.Close,
    "warning" to Icons.Rounded.Warning,
    "error" to Icons.Rounded.Error,
    "help" to Icons.AutoMirrored.Rounded.Help,
    "celebration" to Icons.Rounded.Celebration,

    // 时间与课表
    "schedule" to Icons.Rounded.Schedule,
    "calendarmonth" to Icons.Rounded.CalendarMonth,
    "today" to Icons.Rounded.Today,
    "eventnote" to Icons.AutoMirrored.Rounded.EventNote,
    "alarm" to Icons.Rounded.Alarm,
    "timer" to Icons.Rounded.Timer,

    // 场景与其他
    "localhospital" to Icons.Rounded.LocalHospital,
    "emergency" to Icons.Rounded.Emergency,
    "fitnesscenter" to Icons.Rounded.FitnessCenter,
    "restaurant" to Icons.Rounded.Restaurant,
    "donotdisturb" to Icons.Rounded.DoNotDisturb,

    // 随机与刷新
    "shuffle" to Icons.Rounded.Shuffle,
    "casino" to Icons.Rounded.Casino,
    "autorenew" to Icons.Rounded.Autorenew,
    "cached" to Icons.Rounded.Cached,
)
