[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$OutputPath,

    [switch]$NoPrompt
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms

# 声明 DPI 感知，否则高分屏下系统会位图拉伸弹窗导致模糊。
Add-Type -Namespace RemoteCI -Name Dpi -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
'@
[void][RemoteCI.Dpi]::SetProcessDPIAware()
[System.Windows.Forms.Application]::EnableVisualStyles()

function Show-ResultMessage {
    param(
        [string]$Text,
        [string]$Caption,
        [System.Windows.Forms.MessageBoxIcon]$Icon
    )

    if (-not $NoPrompt) {
        [void][System.Windows.Forms.MessageBox]::Show(
            $Text,
            $Caption,
            [System.Windows.Forms.MessageBoxButtons]::OK,
            $Icon)
    }
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot "plugin\RemoteCI.Plugin\RemoteCI.Plugin.csproj"
$manifestPath = Join-Path $repoRoot "plugin\RemoteCI.Plugin\manifest.yml"
$generatedPackage = Join-Path $repoRoot "plugin\RemoteCI.Plugin\cipx\RemoteCI.Plugin.cipx"

try {
    if (-not (Test-Path -LiteralPath $projectPath) -or -not (Test-Path -LiteralPath $manifestPath)) {
        throw "未找到 RemoteCI 插件项目，请在仓库根目录运行此脚本。"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
    $versionMatch = [regex]::Match(
        $manifest,
        '(?m)^version:\s*(3\.[0-9]+\.[0-9]+\.[0-9]+(?:-beta\.[0-9]+)?)\s*$')
    if (-not $versionMatch.Success) {
        throw "无法从 manifest.yml 读取版本号。"
    }

    $displayVersion = $versionMatch.Groups[1].Value
    # 市场与稳定 Release 都使用固定资产名，避免四段版本再次被截断或产生第二个市场资产。
    $defaultFileName = "RemoteCI.Plugin.cipx"

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $dialog = New-Object System.Windows.Forms.SaveFileDialog
        $dialog.Title = "选择 RemoteCI CIPX 插件包保存位置"
        $dialog.Filter = "ClassIsland 插件包 (*.cipx)|*.cipx|所有文件 (*.*)|*.*"
        $dialog.FileName = $defaultFileName
        $dialog.DefaultExt = "cipx"
        $dialog.AddExtension = $true
        $dialog.OverwritePrompt = $true
        $dialog.InitialDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)

        if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            Write-Output "已取消构建。"
            exit 0
        }

        $OutputPath = $dialog.FileName
    }

    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
    if ([IO.Path]::GetExtension($OutputPath) -ne ".cipx") {
        $OutputPath += ".cipx"
    }

    $outputDirectory = Split-Path -Parent $OutputPath
    if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
        throw "输出路径无效。"
    }
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

    Push-Location $repoRoot
    try {
        & dotnet build $projectPath -c Release -t:Rebuild -p:Version="$displayVersion" -p:CreateCipx=true
        if ($LASTEXITCODE -ne 0) {
            throw "CIPX 构建失败：dotnet build 退出码为 $LASTEXITCODE。"
        }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path -LiteralPath $generatedPackage)) {
        throw "构建完成，但未找到生成的 CIPX 文件。"
    }

    # 当输出位置就是构建产物本身时，Copy-Item 会报“不能用自身覆盖自身”，此时跳过复制。
    $isSameFile = [string]::Equals(
        [IO.Path]::GetFullPath($OutputPath),
        [IO.Path]::GetFullPath($generatedPackage),
        [StringComparison]::OrdinalIgnoreCase)
    if (-not $isSameFile) {
        Copy-Item -LiteralPath $generatedPackage -Destination $OutputPath -Force
    }

    $hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash

    Show-ResultMessage `
        -Caption "RemoteCI CIPX 构建成功" `
        -Icon ([System.Windows.Forms.MessageBoxIcon]::Information) `
        -Text "CIPX 插件包已生成：`n$OutputPath`n`n版本：$displayVersion`nSHA-256：$hash"

    Write-Output $OutputPath
    Write-Output "SHA256=$hash"
    exit 0
}
catch {
    $message = $_.Exception.Message
    Show-ResultMessage `
        -Caption "RemoteCI CIPX 构建失败" `
        -Icon ([System.Windows.Forms.MessageBoxIcon]::Error) `
        -Text $message
    Write-Error $message
    exit 1
}
