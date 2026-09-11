[CmdletBinding()]
param(
    [ValidateSet("x86", "x64", "all")]
    [string] $Architecture = "all",
    [switch] $Rebuild
)

& (Join-Path $PSScriptRoot "native\Scry.Injector.Native\build.ps1") `
    -Architecture $Architecture `
    -Rebuild:$Rebuild
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
