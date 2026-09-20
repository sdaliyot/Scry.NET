[CmdletBinding()]
param(
    [ValidateSet("x86", "x64", "all")]
    [string] $Architecture = "all",
    [string] $Version,
    [switch] $Rebuild
)

$ErrorActionPreference = "Stop"

if ($Version) {
    $cleanVersion = $Version -replace '\+.*$', ''
    $semverCore = ($cleanVersion -split '-')[0]
    $parts = $semverCore.Split('.')
    $major = if ($parts.Length -ge 1) { [int]$parts[0] } else { 0 }
    $minor = if ($parts.Length -ge 2) { [int]$parts[1] } else { 0 }
    $patch = if ($parts.Length -ge 3) { [int]$parts[2] } else { 0 }
    $build = if ($parts.Length -ge 4) { [int]$parts[3] } else { 0 }
    $fileVersionComma = "$major,$minor,$patch,$build"
    $fileVersionStr = "$major.$minor.$patch.$build"
    $productVersionStr = $Version

    $headerContent = @"
#pragma once

#ifndef SCRY_FILEVERSION
#define SCRY_FILEVERSION $fileVersionComma
#endif

#ifndef SCRY_FILEVERSION_STR
#define SCRY_FILEVERSION_STR "$fileVersionStr"
#endif

#ifndef SCRY_PRODUCTVERSION_STR
#define SCRY_PRODUCTVERSION_STR "$productVersionStr"
#endif
"@
    Set-Content -Path (Join-Path $PSScriptRoot "Version.h") -Value $headerContent -Encoding ASCII
}

$project = Join-Path $PSScriptRoot "Scry.Injector.Native.vcxproj"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path $vswhere)) {
    throw "Visual Studio Installer's vswhere.exe was not found."
}

$installationPath = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $installationPath) {
    throw "Visual Studio with the C++ x86/x64 build tools was not found."
}

$msbuild = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    throw "MSBuild was not found at '$msbuild'."
}

$target = if ($Rebuild) { "Rebuild" } else { "Build" }
$platforms = switch ($Architecture) {
    "x86" { @("Win32") }
    "x64" { @("x64") }
    default { @("Win32", "x64") }
}

foreach ($platform in $platforms) {
    & $msbuild $project /nologo /m /t:$target `
        /p:Configuration=Release /p:Platform=$platform /verbosity:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Native $platform build failed with exit code $LASTEXITCODE."
    }
}
