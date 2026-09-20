# Runs the exact validation matrix documented in docs/development.md's "Validation" section.
#
# This script is the primary definition of "validated", not the CI workflow. The repository has
# no remote yet, so .github/workflows/ci.yml cannot actually run anywhere - this script gives the
# same guarantee locally today, and the workflow is written to invoke this same script rather than
# duplicate its steps, so there is exactly one place the matrix is defined.
#
# Each step is run in sequence and the script stops at the first failure, reporting which step
# failed rather than leaving that to be inferred from a wall of build output.

[CmdletBinding()]
param(
    # Skips native-DLL and x86-leg steps - useful for a quick inner-loop check. CI and a real
    # release validation should always run the full matrix (the default).
    [switch] $Quick,
    # Forwarded as -p:Version to every dotnet build/test invocation below. Omit for a plain local
    # run (falls back to Directory.Build.props' VersionPrefix, as before). The release workflow
    # MUST pass the tag-derived version here: this script's own dotnet build/test calls otherwise
    # rebuild Scry.Contracts/Scry.Client/Scry.Injector (and, via BuildInjectionPayloads, the
    # payload/adapter assemblies) without Version, silently resetting them back to the 0.1.0
    # default after release.yml's earlier versioned build already produced correct binaries.
    [string] $Version
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

# NOTE: must be a plain statement, not "$versionArgs = if (...) { @(...) } else { @() }" -
# assigning from an if/else *expression* runs the single-element array literal through
# PowerShell's pipeline output capture, which unwraps a 1-item array down to a bare string.
# Splatting (@versionArgs) a bare string then enumerates it character-by-character (strings are
# IEnumerable<char>), passing "-p:Version=X" to dotnet/MSBuild as one argument per character.
$versionArgs = @()
if ($Version) {
    $versionArgs = @("-p:Version=$Version")
}

function Invoke-Step {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][scriptblock] $Action
    )

    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Name (exit code $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Push-Location $repoRoot
try {
    if (-not $Quick) {
        Invoke-Step "Build native bootstrap DLL" {
            & (Join-Path $repoRoot "build-native.ps1") -Version $Version
        }
    }

    Invoke-Step "dotnet build Scry.sln -c Release" {
        # RequireNativeInjector=true so a missing native helper fails the build here rather than
        # producing a CLI that silently cannot attach - the whole reason that switch exists.
        # @versionArgs must be forwarded here: this is the first build this script runs, and
        # without it every project would rebuild against Directory.Build.props' VersionPrefix
        # default, clobbering whatever version the caller (e.g. release.yml) already produced.
        dotnet build Scry.sln -c Release -p:RequireNativeInjector=true @versionArgs --nologo
    }

    Invoke-Step "dotnet test (net8.0)" {
        dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net8.0 --no-build --nologo
    }

    Invoke-Step "dotnet test (net462, x64)" {
        # No --no-build here (RunConfiguration.TargetPlatform=x64 needs its own build), so
        # @versionArgs must be forwarded or this silently rebuilds net462 without Version.
        dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net462 `
            --artifacts-path artifacts\net462-x64 -p:PlatformTarget=x64 @versionArgs --nologo `
            -- RunConfiguration.TargetPlatform=x64
    }

    if (-not $Quick) {
        Invoke-Step "dotnet test (net462, x86)" {
            dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net462 `
                --artifacts-path artifacts\net462-x86 -p:PlatformTarget=x86 @versionArgs --nologo `
                -- RunConfiguration.TargetPlatform=x86
        }
    }

    Invoke-Step "dotnet test Scry.Wpf.Tests (net462)" {
        dotnet test tests\Scry.Wpf.Tests\Scry.Wpf.Tests.csproj -c Release -f net462 @versionArgs --nologo
    }

    Invoke-Step "dotnet test Scry.WinForms.Tests (net462)" {
        dotnet test tests\Scry.WinForms.Tests\Scry.WinForms.Tests.csproj -c Release -f net462 @versionArgs --nologo
    }

    Invoke-Step "dotnet format --verify-no-changes" {
        dotnet format Scry.sln --verify-no-changes --no-restore
    }

    Write-Host "All validation steps passed." -ForegroundColor Green
}
finally {
    Pop-Location
}
