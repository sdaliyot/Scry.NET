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

    if (-not $Quick) {
        # The net462 legs above cover x86 on .NET Framework; this covers it on modern .NET. Only the
        # in-process hook tests run here: Harmony patches native code, so bitness is what can differ,
        # whereas the attach tests are x64-only by design. The net8.0 test assembly is AnyCPU, so the
        # build above is reused and only the test host's platform changes.
        $x86Runtime = Join-Path ${env:ProgramFiles(x86)} "dotnet\shared\Microsoft.NETCore.App"
        if (-not (Test-Path (Join-Path $x86Runtime "8.*"))) {
            if ($env:GITHUB_ACTIONS -eq "true") {
                # CI runners ship x64 only; install the matching x86 runtime where the x86 test
                # host looks for it. Not done on a developer machine, which would be a surprise.
                Invoke-Step "Install the x86 .NET 8 runtime" {
                    $installer = Join-Path ([System.IO.Path]::GetTempPath()) "dotnet-install.ps1"
                    Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer
                    & $installer -Runtime dotnet -Channel 8.0 -Architecture x86 `
                        -InstallDir (Join-Path ${env:ProgramFiles(x86)} "dotnet")
                }
            }
            else {
                Write-Warning "Skipping 'dotnet test (net8.0, x86, hooks)': no x86 .NET 8 runtime under '$x86Runtime'."
            }
        }

        if (Test-Path (Join-Path $x86Runtime "8.*")) {
            Invoke-Step "dotnet test (net8.0, x86, hooks)" {
                # Point the x86 test host at the x86 install explicitly. GitHub's runners export
                # DOTNET_ROOT as the x64 install, and the x86 host lookup then fails with "Could not
                # find 'dotnet.exe' host for the 'X86' architecture" even though the x86 install is
                # present at its default location (confirmed on a runner: with these two variables
                # set, or DOTNET_ROOT cleared, the same command passes; without, it aborts).
                $x86Root = Join-Path ${env:ProgramFiles(x86)} "dotnet"
                $env:SCRY_EXPECT_BITNESS = "x86"
                $env:DOTNET_ROOT_X86 = $x86Root
                [Environment]::SetEnvironmentVariable("DOTNET_ROOT(x86)", $x86Root)
                try {
                    dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net8.0 --no-build --nologo `
                        --filter "FullyQualifiedName~HookOperationTests" `
                        -- RunConfiguration.TargetPlatform=x86
                }
                finally {
                    Remove-Item Env:SCRY_EXPECT_BITNESS -ErrorAction SilentlyContinue
                    Remove-Item Env:DOTNET_ROOT_X86 -ErrorAction SilentlyContinue
                    [Environment]::SetEnvironmentVariable("DOTNET_ROOT(x86)", $null)
                }
            }
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
