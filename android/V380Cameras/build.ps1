<#
.SYNOPSIS
    Builds the V380 Cameras Android app with the portable toolchain in tools\.

.DESCRIPTION
    Everything needed to build lives inside this folder, so no machine-wide
    Android SDK or JDK installation is required:

      tools\dotnet            .NET 10 SDK with the android workload
      tools\jdk17             Microsoft OpenJDK 17
      tools\android-sdk       platform-tools, android-36, build-tools 36.0.0

    The web UI is not copied: the project packages ..\..\ui directly, so the
    browser build and the app always contain the same pages.

.PARAMETER Install
    After a successful build, install the APK on the connected device with adb.

.PARAMETER Configuration
    Release (default) or Debug.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Install
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $here)   # android\V380Cameras -> repo root
$tools = Join-Path $root 'tools'
$project = Join-Path $here 'V380Cameras.csproj'

function Find-Child([string]$parent, [string]$filter) {
    if (-not (Test-Path $parent)) { return $null }
    Get-ChildItem $parent -Directory -Filter $filter -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

$jdk = Find-Child (Join-Path $tools 'jdk17') 'jdk*'
if (-not $jdk -and $env:JAVA_HOME -and (Test-Path $env:JAVA_HOME)) {
    $jdk = $env:JAVA_HOME
}

$sdk = Join-Path $tools 'android-sdk'
if (-not (Test-Path $sdk)) {
    if ($env:ANDROID_HOME -and (Test-Path $env:ANDROID_HOME)) {
        $sdk = $env:ANDROID_HOME
    } elseif ($env:AndroidSdkDirectory -and (Test-Path $env:AndroidSdkDirectory)) {
        $sdk = $env:AndroidSdkDirectory
    } elseif ($env:LOCALAPPDATA -and (Test-Path (Join-Path $env:LOCALAPPDATA 'Android\Sdk'))) {
        $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    }
}

$dotnet = Join-Path $tools 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($systemDotnet) {
        $dotnet = $systemDotnet.Source
    }
}

if (-not $jdk) { throw "No JDK 17 found. Set JAVA_HOME or place OpenJDK under tools\jdk17." }
if (-not (Test-Path $sdk)) { throw "No Android SDK found. Set ANDROID_HOME or place SDK under tools\android-sdk." }
if (-not (Test-Path $dotnet)) { throw "No .NET SDK found. Ensure dotnet is installed on PATH or placed under tools\dotnet." }

$env:JAVA_HOME = $jdk
$env:AndroidSdkDirectory = $sdk
$env:ANDROID_HOME = $sdk
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host "JDK    $jdk"
Write-Host "SDK    $sdk"
Write-Host "dotnet $dotnet"

& $dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$apk = Get-ChildItem (Join-Path $here "bin\$Configuration\net10.0-android") -Filter '*-Signed.apk' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $apk) { throw 'Build reported success but no signed APK was produced.' }

Write-Host ''
Write-Host "APK    $($apk.FullName)"
Write-Host ("       {0:N1} MB" -f ($apk.Length / 1MB))

if ($Install) {
    $adb = Join-Path $sdk 'platform-tools\adb.exe'
    if (-not (Test-Path $adb)) { throw "adb not found at $adb" }

    $devices = & $adb devices | Select-String -Pattern '\sdevice$'
    if (-not $devices) {
        throw 'No device found. Enable USB debugging, connect the phone, and approve the prompt, then run .\build.ps1 -Install again.'
    }

    Write-Host ''
    Write-Host 'Installing...'
    & $adb install -r $apk.FullName
    if ($LASTEXITCODE -ne 0) { throw "adb install failed with exit code $LASTEXITCODE" }
    & $adb shell am start -n 'local.v380.cameras/.MainActivity'
}
