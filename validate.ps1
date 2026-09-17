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
    [switch] $Quick
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

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
            & (Join-Path $repoRoot "build-native.ps1")
        }
    }

    Invoke-Step "dotnet build Scry.sln -c Release" {
        # RequireNativeInjector=true so a missing native helper fails the build here rather than
        # producing a CLI that silently cannot attach - the whole reason that switch exists.
        dotnet build Scry.sln -c Release -p:RequireNativeInjector=true --nologo
    }

    Invoke-Step "dotnet test (net8.0)" {
        dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net8.0 --no-build --nologo
    }

    Invoke-Step "dotnet test (net462, x64)" {
        dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net462 `
            --artifacts-path artifacts\net462-x64 -p:PlatformTarget=x64 --nologo `
            -- RunConfiguration.TargetPlatform=x64
    }

    if (-not $Quick) {
        Invoke-Step "dotnet test (net462, x86)" {
            dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net462 `
                --artifacts-path artifacts\net462-x86 -p:PlatformTarget=x86 --nologo `
                -- RunConfiguration.TargetPlatform=x86
        }
    }

    Invoke-Step "dotnet test Scry.Wpf.Tests (net462)" {
        dotnet test tests\Scry.Wpf.Tests\Scry.Wpf.Tests.csproj -c Release -f net462 --nologo
    }

    Invoke-Step "dotnet test Scry.WinForms.Tests (net462)" {
        dotnet test tests\Scry.WinForms.Tests\Scry.WinForms.Tests.csproj -c Release -f net462 --nologo
    }

    Invoke-Step "dotnet format --verify-no-changes" {
        dotnet format Scry.sln --verify-no-changes --no-restore
    }

    Write-Host "All validation steps passed." -ForegroundColor Green
}
finally {
    Pop-Location
}
