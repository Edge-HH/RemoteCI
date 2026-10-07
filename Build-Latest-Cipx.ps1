[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$OutputPath,

    [switch]$NoPrompt
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms

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
        throw "??? RemoteCI ??????????????????"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
    $versionMatch = [regex]::Match(
        $manifest,
        '(?m)^version:\s*(3\.[0-9]+\.[0-9]+\.[0-9]+(?:-beta\.[0-9]+)?)\s*$')
    if (-not $versionMatch.Success) {
        throw "??? manifest.yml ???????"
    }

    $displayVersion = $versionMatch.Groups[1].Value
    # 市场与稳定 Release 都使用固定资产名，避免四段版本再次被截断或产生第二个市场资产。
    $defaultFileName = "RemoteCI.Plugin.cipx"

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $dialog = New-Object System.Windows.Forms.SaveFileDialog
        $dialog.Title = "???? RemoteCI CIPX ?????"
        $dialog.Filter = "ClassIsland ??? (*.cipx)|*.cipx|???? (*.*)|*.*"
        $dialog.FileName = $defaultFileName
        $dialog.DefaultExt = "cipx"
        $dialog.AddExtension = $true
        $dialog.OverwritePrompt = $true
        $dialog.InitialDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)

        if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            Write-Output "??????"
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
        throw "???????"
    }
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

    Push-Location $repoRoot
    try {
        & dotnet build $projectPath -c Release -t:Rebuild -p:Version="$displayVersion" -p:CreateCipx=true
        if ($LASTEXITCODE -ne 0) {
            throw "CIPX ?????dotnet build ????? $LASTEXITCODE?"
        }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path -LiteralPath $generatedPackage)) {
        throw "?????????????? CIPX ???"
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
        -Caption "RemoteCI CIPX ????" `
        -Icon ([System.Windows.Forms.MessageBoxIcon]::Information) `
        -Text "??????????`n$OutputPath`n`n???$displayVersion`nSHA-256?$hash"

    Write-Output $OutputPath
    Write-Output "SHA256=$hash"
    exit 0
}
catch {
    $message = $_.Exception.Message
    Show-ResultMessage `
        -Caption "RemoteCI CIPX ????" `
        -Icon ([System.Windows.Forms.MessageBoxIcon]::Error) `
        -Text $message
    Write-Error $message
    exit 1
}
