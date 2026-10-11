# Builds the installer in one go: publishes the self-contained program to dist\WinCompositor, then compiles
# installer\WinCompositor.iss with Inno Setup into dist\installer\WinCompositor-Setup-<version>.exe.
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-SkipPublish]
param([switch]$SkipPublish)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# The version, from Directory.Build.props, so the program and its installer always agree.
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "Directory.Build.props has no <Version>." }

if (-not $SkipPublish) {
    if (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") {
        $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"
    & dotnet publish (Join-Path $root "src\Compositor.App") -p:PublishProfile=win-x64 -p:UseSharedCompilation=false -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
    & dotnet build-server shutdown | Out-Null
}
if (-not (Test-Path (Join-Path $root "dist\WinCompositor\WinCompositor.exe"))) { throw "dist\WinCompositor\WinCompositor.exe is missing; publish first." }

# Inno Setup's command-line compiler, wherever it was installed (per user or for everyone).
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php" }

& $iscc /Qp "/DAppVersion=$version" (Join-Path $root "installer\WinCompositor.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
Get-Item (Join-Path $root "dist\installer\WinCompositor-Setup-$version.exe") | Select-Object FullName, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB) } }
