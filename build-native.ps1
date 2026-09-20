# Builds the native bootstrap DLL, which is deliberately NOT a member of Scry.sln.
#
# Scry.Injector.Native is a vcxproj, and a vcxproj imports Microsoft.Cpp.props through
# $(VCTargetsPath) - which the .NET CLI does not ship. Adding it to the solution would break
# `dotnet build Scry.sln`, `dotnet test Scry.sln` and `dotnet format Scry.sln` at evaluation
# time, before anything compiled. It also declares only Release|Win32 and Release|x64, against
# a solution that offers Debug|Any CPU.
#
# So it is built here with full MSBuild instead, and the managed build picks up its output by
# path (see the NativeX64/NativeX86 items in src\Scry.Injector\Scry.Injector.csproj). Run this
# before the managed Release build, and pass -p:RequireNativeInjector=true to that build so a
# forgotten native build fails loudly rather than producing a CLI that cannot attach.

[CmdletBinding()]
param(
    [ValidateSet("x86", "x64", "all")]
    [string] $Architecture = "all",
    [string] $Version,
    [switch] $Rebuild
)

& (Join-Path $PSScriptRoot "native\Scry.Injector.Native\build.ps1") `
    -Architecture $Architecture `
    -Version $Version `
    -Rebuild:$Rebuild
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
