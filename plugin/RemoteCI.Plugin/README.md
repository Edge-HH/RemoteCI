# RemoteCI ClassIsland 插件

将 ClassIsland 与 RemoteCI 服务端、Wear OS 手表连接起来，让你可以在手表上查看课表并远程控制教室电脑。

## ✨ 核心功能

- 查看当前课程、下一节课和未来七日课表。
- 远程交换或替换课程。
- 发送通知，并接收 ClassIsland 自动化和其他插件的提醒。
- 控制 ClassIsland 主界面、电脑音量和电源。
- 支持局域网直连和云端中转。
- 支持其他 ClassIsland 插件扩展远程控制功能。

## 🚀 使用方法

1. 部署 RemoteCI 服务端，并安装 Wear OS 客户端。
2. 在服务端 WebUI 的“概览”页生成插件配对码。
3. 在 ClassIsland 的“RemoteCI 设置”中填写服务器地址和配对码。
4. 保存设置并重启 ClassIsland。

连接成功后，课表和状态会自动同步到服务端与手表。修改服务器地址或端口后，需要重启 ClassIsland 才会生效。

## 安全说明

插件不会保存学生密码；远程操作会按服务端配置的账号权限进行校验。

更多部署与使用说明请查看 [RemoteCI 项目文档](https://edge-hh.github.io/RemoteCI-Docs/)。
