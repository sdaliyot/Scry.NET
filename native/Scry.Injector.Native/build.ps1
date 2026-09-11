[CmdletBinding()]
param(
    [ValidateSet("x86", "x64", "all")]
    [string] $Architecture = "all",
    [switch] $Rebuild
)

$ErrorActionPreference = "Stop"
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
