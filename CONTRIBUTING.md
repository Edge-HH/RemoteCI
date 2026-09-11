# 贡献指南

感谢帮助改进 RemoteCI。本仓库包含服务端、Wear OS 手表端与 ClassIsland 插件；
用户文档位于独立的 [RemoteCI-Docs](https://github.com/Edge-HH/RemoteCI-Docs) 仓库。

## 开发环境

- .NET SDK 10（服务端与共享库）
- JDK 17 与 Android SDK（手表端，见 `wearos/dev.ps1`）
- PowerShell 7（插件 CIPX 打包需要）

## 构建与测试

```powershell
dotnet build RemoteCI.slnx -c Release
dotnet test RemoteCI.slnx -c Release --no-build

cd wearos
.\gradlew.bat testDebugUnitTest assembleDebug
```

插件打包：

```powershell
dotnet build plugin/RemoteCI.Plugin -c Release -p:CreateCipx=true
```

详细说明见 [README.md](README.md) 与 `docs/` 目录。

## 提交流程

1. 新建分支（建议 `feature/` 或 `fix/` 前缀），在分支上提交修改。
2. 只修改本任务相关的问题，不夹带无关改动；代码保留必要注释并采用项目现有主流方案。
3. 本地运行上述构建与测试命令，确保通过。
4. 功能或用户可见行为变化必须在 RemoteCI-Docs 仓库同步更新文档，并在 PR 中互相添加链接。
5. 提交 Pull Request 到 `main`，填写模板中的检查清单，等待 CI 通过并完成审核。
6. 发布稳定版本时由维护者推送四段纯数字标签（例如 `3.2.1.3`）；测试版本才使用 `v3.x.x-beta.y`。GitHub Actions 会自动构建并在同一个 Release 发布服务端、固定名插件包、手表 APK 与三种 fnOS FPK，其他标签会被拒绝。

## 安全漏洞

不要通过 Issue 或 PR 公开报告安全漏洞，请按 [SECURITY.md](SECURITY.md) 的私密流程提交。

## 行为准则

参与本项目即视为同意 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。
