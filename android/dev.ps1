# RemoteCI Wear OS 开发辅助脚本
# 用法:
#   .\dev.ps1 emulator   - 启动 Wear OS 模拟器（已运行时跳过）
#   .\dev.ps1 run        - 构建 APK -> 安装 -> 启动应用
# 说明: 路径为本机开发环境配置，换机器时按 docs/platform-notes.md 调整。
param([string]$Action = "run")

$ErrorActionPreference = "Stop"
$Sdk = "C:\Users\YangTianming\AppData\Local\Android\Sdk"
$Jbr = "E:\Android Studio\jbr"
$Avd = "Pixel_8_API35"

$env:JAVA_HOME = $Jbr
$env:ANDROID_HOME = $Sdk
$adb = "$Sdk\platform-tools\adb.exe"
$emulator = "$Sdk\emulator\emulator.exe"

switch ($Action) {
    "emulator" {
        $running = & $adb devices | Select-String "emulator-\d+\s+device"
        if (-not $running) {
            Start-Process -FilePath $emulator -ArgumentList "-avd", $Avd, "-no-snapshot", "-no-boot-anim", "-gpu", "auto" -WindowStyle Hidden
            Write-Host "模拟器启动中: $Avd （首次冷启动约 2-3 分钟，可用 adb devices 查看状态）"
        } else {
            Write-Host "模拟器已在运行: $($running.Line.Trim())"
        }
    }
    "run" {
        Write-Host "==> 构建 APK"
        & .\gradlew.bat assembleDebug
        if ($LASTEXITCODE -ne 0) { throw "构建失败，请查看上方日志" }

        # 与 build.gradle.kts 使用相同的本机构建目录优先级，避免云盘项目中的重解析文件导致 Gradle 快照失败。
        $externalBuildRoot = $env:REMOTECI_ANDROID_BUILD_DIR
        if (-not $externalBuildRoot -and (Test-Path ".\local.properties")) {
            $buildDirEntry = Get-Content ".\local.properties" |
                Where-Object { $_ -match '^remoteci\.buildDir=' } |
                Select-Object -Last 1
            if ($buildDirEntry) {
                $externalBuildRoot = ($buildDirEntry -split '=', 2)[1].Trim().Replace('\:', ':')
            }
        }
        if ($externalBuildRoot -and -not [IO.Path]::IsPathRooted($externalBuildRoot)) {
            $externalBuildRoot = Join-Path $PSScriptRoot $externalBuildRoot
        }
        $apk = if ($externalBuildRoot) {
            Join-Path $externalBuildRoot "app\outputs\apk\debug\app-debug.apk"
        } else {
            "app\build\outputs\apk\debug\app-debug.apk"
        }
        Write-Host "==> 安装 $apk"
        & $adb install -r $apk
        if ($LASTEXITCODE -ne 0) { throw "安装失败（模拟器是否已启动？）" }

        Write-Host "==> 启动应用"
        & $adb shell am start -n com.remoteci.mobile/.MainActivity
        Write-Host "完成。"
    }
    default {
        Write-Host "用法: .\dev.ps1 emulator|run"
    }
}
