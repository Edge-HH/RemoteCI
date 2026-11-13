# 第三方依赖声明

本项目遵循 GPLv3（见 LICENSE）。以下为直接引入或参考的第三方项目/依赖清单，使用前请确认对应许可证条款。**新增依赖时必须在表格中同步登记。**

| 项目 | 用途 | 许可证 | 版权/备注 |
| --- | --- | --- | --- |
| [ClassIsland](https://github.com/ClassIsland/ClassIsland) | 插件宿主与 SDK | GPLv3（本体）/ LGPLv3（PluginSdk、Core、Shared、Shared.Ipc） | Copyright (c) 2024 HelloWRC；插件按 GPLv3 发布以兼容上架要求 |
| ASP.NET Core | 服务端框架 | MIT | © .NET Foundation；NuGet 包自带许可信息 |
| AndroidX / Jetpack Compose for Wear OS / Tiles | 手表端 UI 与系统组件 | Apache-2.0 | © The Android Open Source Project |
| Kotlin 标准库 | 手表端语言运行时 | Apache-2.0 | © JetBrains |
| Gradle / Android Gradle Plugin | 构建工具 | Apache-2.0 | © Gradle Inc. / Google |
| xUnit / Microsoft.NET.Test.Sdk / Mvc.Testing | 服务端测试框架（开发依赖） | Apache-2.0 / MIT | © .NET Foundation 等；仅用于测试，不进入分发物 |
| [Fleck](https://github.com/statianzo/Fleck) | 插件端嵌入式 WebSocket 服务器（局域网直连） | MIT | Copyright (c) Jason Staten |
| [Harmony](https://github.com/pardeike/Harmony) | 旁路观察 ClassIsland 通知发送入口 | MIT | Copyright (c) Andreas Pardeike；使用单程序集 `Lib.Harmony` 分发包 |
| [NAudio](https://github.com/naudio/NAudio) | Windows 默认播放设备主音量与静音控制 | MIT | Copyright (c) Mark Heath 等贡献者 |
| OkHttp | 手表端 HTTP/WebSocket 客户端 | Apache-2.0 | © Square, Inc. |
| kotlinx.serialization | 手表端 JSON 序列化 | Apache-2.0 | © JetBrains |
| kotlinx.coroutines | 手表端协程 | Apache-2.0 | © JetBrains |
| [Material Symbols Rounded](https://fonts.google.com/icons) | WebUI 导航与操作图标（按使用到的图标子集化，与安卓端 Material Rounded 图标同源） | Apache-2.0 | Copyright Google LLC；许可证原文位于 `server/RemoteCI.Server/wwwroot/vendor/material-symbols/LICENSE` |
| [ClosedXML](https://github.com/ClosedXML/ClosedXML) | 服务端成员 Excel 导入、导出与覆盖导入 | MIT | Copyright (c) ClosedXML contributors；传递依赖包括 DocumentFormat.OpenXml（MIT）与 SixLabors.Fonts（Apache-2.0） |
| [Markdig](https://github.com/xoofx/markdig) | WebUI 系统更新页渲染 GitHub Release 更新说明 | BSD-2-Clause | Copyright (c) Alexandre Mutel |
| [QRCoder](https://github.com/codebude/QRCoder) | 服务端生成网页扫码登录、手机扫码登录、二步验证与电子班牌二维码 | MIT | NuGet 1.7.0；Copyright (c) QRCoder contributors |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | 插件编译期依赖：固定 ClassIsland SDK 经 Avalonia 传入的易受攻击版本 0.21.2（GHSA-xrw6-gwf8-vvr9），运行时由宿主提供、不随插件包分发 | MIT | NuGet 0.21.3；Copyright (c) Tom Deseyn |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCLRaw) | 服务端 SQLite 原生库捆绑（`bundle_e_sqlite3`，EF Core Sqlite 的底层提供程序）；显式引用以覆盖易受攻击的传递版本 2.1.11 | Apache-2.0 | NuGet 2.1.13（含同版本 core / provider.e_sqlite3 / lib.e_sqlite3）；Copyright (c) Eric Sink；SQLite 本身为 public domain |

## 合规约定

1. 引入任何第三方代码前，先确认其许可证与 GPLv3 兼容，并在上表登记。
2. 保留被引用项目的版权声明与许可证原文（随包分发的 NOTICE/LICENSE 不得删除）。
3. 插件分发物（.cipx）与源码包中必须包含 GPLv3 文本及本清单。
