<#
.SYNOPSIS
    Downloads the third-party binaries HetznerDrive bundles: rclone.exe (the mount engine) and the
    WinFsp installer (the user-mode filesystem drive-letter mounts need).

.DESCRIPTION
    These are deliberately not committed — they are large, and pinning a binary in git makes
    updating one a repository rewrite. Run this once after cloning, and again when you want to move
    to a newer rclone.

    The rclone version is recorded in third_party\rclone\VERSION.txt so a build is reproducible.

.EXAMPLE
    scripts\fetch-deps.ps1
    scripts\fetch-deps.ps1 -RcloneVersion 1.68.2 -Force
#>
[CmdletBinding()]
param(
    [string]$RcloneVersion = 'current',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$rcloneDir = Join-Path $root 'third_party\rclone'
$winfspDir = Join-Path $root 'third_party\winfsp'
New-Item -ItemType Directory -Force -Path $rcloneDir, $winfspDir | Out-Null

$rcloneExe = Join-Path $rcloneDir 'rclone.exe'
if ((Test-Path $rcloneExe) -and -not $Force) {
    Write-Host "rclone.exe already present (use -Force to re-download)."
}
else {
    $url = if ($RcloneVersion -eq 'current') {
        'https://downloads.rclone.org/rclone-current-windows-amd64.zip'
    } else {
        "https://downloads.rclone.org/v$RcloneVersion/rclone-v$RcloneVersion-windows-amd64.zip"
    }

    Write-Host "Downloading rclone from $url ..."
    $zip = Join-Path $env:TEMP "rclone-$([guid]::NewGuid()).zip"
    $extract = Join-Path $env:TEMP "rclone-$([guid]::NewGuid())"
    try {
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        Expand-Archive -Path $zip -DestinationPath $extract -Force

        $found = Get-ChildItem -Path $extract -Recurse -Filter 'rclone.exe' | Select-Object -First 1
        if (-not $found) { throw "rclone.exe was not found inside the downloaded archive." }
        Copy-Item $found.FullName $rcloneExe -Force

        # Record the version actually fetched, not the one requested: 'current' resolves to
        # whatever was newest at download time, and the build should be able to say which.
        $version = (& $rcloneExe version | Select-Object -First 1)
        Set-Content -LiteralPath (Join-Path $rcloneDir 'VERSION.txt') -Value @"
$version
Source: $url
Fetched: $(Get-Date -Format 'yyyy-MM-dd')
"@
        Write-Host "rclone ready: $version"
    }
    finally {
        Remove-Item $zip -ErrorAction SilentlyContinue
        Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$winfspMsi = Join-Path $winfspDir 'winfsp.msi'
if ((Test-Path $winfspMsi) -and -not $Force) {
    Write-Host "winfsp.msi already present (use -Force to re-download)."
    return
}

# WinFsp publishes its installer as a GitHub release asset; take the newest .msi from the latest tag.
Write-Host "Resolving the latest WinFsp release ..."
$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/winfsp/winfsp/releases/latest' `
    -Headers @{ 'User-Agent' = 'HetznerDrive-fetch-deps' }
$asset = $release.assets | Where-Object { $_.name -like '*.msi' } | Select-Object -First 1
if (-not $asset) { throw "No .msi asset found on the latest WinFsp release." }

Write-Host "Downloading $($asset.name) ..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $winfspMsi -UseBasicParsing
Set-Content -LiteralPath (Join-Path $winfspDir 'SOURCE.txt') -Value @"
$($asset.name)
Release: $($release.tag_name)
Source: $($asset.browser_download_url)
Fetched: $(Get-Date -Format 'yyyy-MM-dd')
"@
Write-Host "WinFsp ready: $($release.tag_name)"
