# Publishes HetznerDrive as a self-contained win-x64 app (no .NET runtime prerequisite), then
# publishes the Windows service host into the same folder so the installer ships one payload.
# Output: src\HetznerDrive.App\bin\Release\net8.0-windows*\win-x64\publish
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root "src\HetznerDrive.App\HetznerDrive.App.csproj"
$service = Join-Path $root "src\HetznerDrive.Service\HetznerDrive.Service.csproj"

dotnet publish $app `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=false

# The TFM folder carries the Windows platform version (e.g. net8.0-windows10.0.19041.0),
# so resolve it by glob rather than hard-coding.
$publishDir = Get-ChildItem -Path (Join-Path $root "src\HetznerDrive.App\bin\Release") -Directory `
    -Filter "net8.0-windows*" | Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName "win-x64\publish" }
Write-Host "Published app to: $publishDir"

# The service lands in the same directory as the app rather than a subfolder: ServiceControl
# resolves it next to HetznerDrive.exe, and both then share the one copy of the runtime and of
# rclone.exe that is already there.
Write-Host "Publishing the Windows service host ..."
dotnet publish $service `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=false `
    -o $publishDir

foreach ($required in @("rclone.exe", "HetznerDrive.exe", "HetznerDrive.Service.exe")) {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        Write-Warning "$required is missing from the publish output."
    }
}
Write-Host "Publish complete: $publishDir"
