[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$ChecksumPath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($ExpectedVersion -notmatch '^3\.[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "ClassIsland 市场版本必须是四段纯数字 V3 版本，实际为：$ExpectedVersion"
}

$resolvedPackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$resolvedChecksumPath = (Resolve-Path -LiteralPath $ChecksumPath).Path

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackagePath)
try {
    $requiredEntries = @('manifest.yml', 'README.md', 'icon.png')
    foreach ($requiredEntry in $requiredEntries) {
        if (-not ($archive.Entries | Where-Object FullName -eq $requiredEntry)) {
            throw "ClassIsland 市场包缺少必需文件：$requiredEntry"
        }
    }

    $manifestEntry = $archive.Entries | Where-Object FullName -eq 'manifest.yml'
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try {
        $manifest = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}
finally {
    $archive.Dispose()
}

$requiredManifestValues = [ordered]@{
    id               = 'remoteci.plugin'
    entranceAssembly = 'RemoteCI.Plugin.dll'
    version          = $ExpectedVersion
    apiVersion       = '2.0.0.0'
    author           = 'Edge-HH'
    repoOwner        = 'Edge-HH'
    repoName         = 'RemoteCI'
    assetsRoot       = 'main/plugin/RemoteCI.Plugin'
    artifactName     = 'RemoteCI.Plugin.cipx'
    tagPattern       = '3.*.*.*'
}

foreach ($entry in $requiredManifestValues.GetEnumerator()) {
    $escapedValue = [regex]::Escape($entry.Value)
    $pattern = '(?m)^{0}:\s*["'']?{1}["'']?\s*$' -f [regex]::Escape($entry.Key), $escapedValue
    if ($manifest -notmatch $pattern) {
        throw "插件清单字段 $($entry.Key) 不符合 ClassIsland 市场要求。"
    }
}

if ($manifest -notmatch '(?m)^supportedOSPlatforms:\s*$' -or
    $manifest -notmatch '(?m)^\s*-\s*Windows\s*$') {
    throw '插件清单必须明确声明仅支持 Windows。'
}

$checksumText = Get-Content -LiteralPath $resolvedChecksumPath -Raw -Encoding UTF8
$marker = [regex]::Match(
    $checksumText,
    '<!--\s*CLASSISLAND_PKG_MD5\s+(?<json>\{.*?\})\s*-->',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $marker.Success) {
    throw 'checksums.md 缺少 CLASSISLAND_PKG_MD5 标记。'
}

$hashes = $marker.Groups['json'].Value | ConvertFrom-Json -AsHashtable
$artifactName = 'RemoteCI.Plugin.cipx'
if (-not $hashes.ContainsKey($artifactName)) {
    throw "MD5 标记没有使用市场工件名：$artifactName"
}

$actualHash = (Get-FileHash -LiteralPath $resolvedPackagePath -Algorithm MD5).Hash.ToUpperInvariant()
$declaredHash = ([string]$hashes[$artifactName]).ToUpperInvariant()
if ($actualHash -ne $declaredHash) {
    throw "市场包 MD5 不匹配：声明 $declaredHash，实际 $actualHash"
}

Write-Host "ClassIsland 市场包验证通过：$artifactName $ExpectedVersion ($actualHash)"
