---
name: scry
description: Inspect, validate, and deliberately mutate a local Scry.NET-enabled .NET process through the scry JSON CLI.
---

# Scry.NET agent Skill

Use this Skill when an agent must inspect or control a running .NET application that has
opted into Scry.NET. The `scry` CLI is the only agent interface in this release. Do not
connect to named pipes directly and do not read capability tokens from descriptor files.

Scry.NET is Windows-only tooling for development and testing. Endpoints are local-only unless the
target deliberately opted into a TCP listener, which is off by default. It can inspect private
state, mutate objects, invoke methods, and execute C# inside the target process. Use it only on
applications the user deliberately started or enabled for development or testing.

"Development and testing" is a matter of authorization, not capability: attach works against
Release builds as readily as Debug ones. The rule is about what you are permitted to attach to,
never about how the target was compiled. Two things do differ in a Release build, and both change
what your expressions can rely on rather than whether attaching works: an obfuscated assembly
breaks anything that names a member, and `#if DEBUG` code is absent, so the application itself may
behave differently from a Debug build you tested against.

## Locating the `scry` CLI

Call `Resolve-ScryCli -Architecture x64` (or `x86`) below instead of asking the user to install
anything. It finds an existing `scry.exe` if one is already configured, and otherwise downloads,
extracts, and configures one automatically - no manual download or environment variable setup
required. It also keeps an existing install current: on an existing install, at most once per 24
hours (tracked next to `scry.exe`) it compares the installed `scry --version` against the
[latest GitHub release](https://github.com/sdaliyot/Scry.NET/releases/latest) and updates in place
if a newer one exists. Any failure to reach GitHub, or to replace a file still locked because
`scry.exe` is currently injected into a running target, is not fatal - it just means "nothing
changed this time"; the existing install (if any) is always what gets returned.

It installs under `%ProgramData%\Scry.NET` (not `%LOCALAPPDATA%`), locked so only administrators,
SYSTEM and the installing user can write. A machine-wide path is required because a packaged host -
Claude Desktop is an MSIX app - redirects `%LOCALAPPDATA%` into its own per-package `LocalCache`,
and a copy of scry there loads for the host but not into a separate target process; `%ProgramData%`
is not virtualized that way and is readable by every local identity, including a service-account
target. An existing install that resolved into such a package sandbox is discarded and replaced.

> [!IMPORTANT]
> **Check Process, User, and Machine scopes:**
> Environment variables defined or modified by the user or an installer after your agent session
> started will **not** appear in the current `Process` environment table (`$env:...`). Always query
> `Process`, `User`, and `Machine` scopes - `Resolve-ScryCli` does this internally before deciding
> whether an install or update is needed:
> ```powershell
> function Resolve-ScryCli {
>     param(
>         [Parameter(Mandatory)]
>         [ValidateSet("x64", "x86")]
>         [string]$Architecture
>     )
>
>     $envVar = if ($Architecture -eq "x86") { "SCRY_HOME_X86" } else { "SCRY_HOME_X64" }
>     $repo = "sdaliyot/Scry.NET"
>     Write-Host "Resolving scry.exe for architecture $Architecture..."
>
>     function Resolve-ScryExe([string]$dir) {
>         $path = if (Test-Path $dir -PathType Leaf) { $dir } else { Join-Path $dir "scry.exe" }
>         if (Test-Path $path) { return $path }
>         return $null
>     }
>
>     # 1. Check Process, User, Machine env vars, then PATH - same order as before.
>     $exePath = $null
>     foreach ($scope in 'Process', 'User', 'Machine') {
>         $dir = [Environment]::GetEnvironmentVariable($envVar, $scope)
>         if (![string]::IsNullOrWhiteSpace($dir)) {
>             Write-Host ("Checking {0} environment variable {1}: {2}" -f $scope, $envVar, $dir)
>             $exePath = Resolve-ScryExe $dir
>             if ($exePath) { break }
>         }
>     }
>     if (-not $exePath) {
>         Write-Host "scry.exe not found in environment variables; checking PATH..."
>         $cmd = Get-Command scry -ErrorAction SilentlyContinue
>         if ($cmd) { $exePath = $cmd.Source }
>         Write-Host "Resolved scry.exe path: $exePath"
>     }
>
>     # A packaged host (MSIX - Claude Desktop is one) redirects LocalApplicationData into its own
>     # per-package ...\Packages\<id>\LocalCache\Local. scry installed there loads for the host but
>     # not into a separate target process, so it must never be the install location. Discard an
>     # existing install that resolved into such a sandbox, and install under ProgramData instead:
>     # machine-wide, not virtualized by packaging, and readable by every local identity (including a
>     # service-account target). CommonApplicationData, not $env:ProgramData, for the same reason.
>     if ($exePath -and $exePath -match '\\Packages\\[^\\]+\\LocalCache\\') {
>         Write-Host "Ignoring sandboxed scry.exe at $exePath (packaged-app LocalCache)."
>         $exePath = $null
>     }
>     $installRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "Scry.NET"
>     $targetDir = if ($exePath) { Split-Path $exePath -Parent } else { Join-Path $installRoot $Architecture }
>     $stampPath = Join-Path $targetDir ".scry-last-checked"
>
>     # 2. Skip the network entirely if we already checked within the last 24h.
>     if ($exePath) {
>         $lastChecked = if (Test-Path $stampPath) { Get-Date (Get-Content $stampPath -Raw) } else { $null }
>         if ($lastChecked -and ((Get-Date) - $lastChecked) -lt (New-TimeSpan -Hours 24)) {
>             Write-Host "scry.exe was last checked at $lastChecked; skipping network check."
>             return $exePath
>         }
>     }
>
>     # 3. Ask GitHub for the latest release. Any failure here (offline, rate-limited) just means
>     #    "can't check right now" - keep whatever's already installed rather than fail the attach.
>     try {
>         Write-Host "Fetching latest release information from GitHub for $repo..."
>         $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
>     } catch {
>         Write-Host "Failed to fetch latest release information from GitHub: $_"
>         if ($exePath) {
>             Write-Host "Using existing scry.exe at $exePath despite GitHub fetch failure."
>             Set-Content $stampPath (Get-Date -Format "o")
>             return $exePath
>         }
>         throw "Could not reach GitHub to install scry.exe ($Architecture): $_"
>     }
>
>     $latest = $release.tag_name.TrimStart('v')
>     Write-Host "Latest release from GitHub: $latest"
>     if ($exePath) {
>         $installed = (& $exePath --version).Trim()
>         if ($installed -eq $latest) {
>             Write-Host "scry.exe is up-to-date ($installed)."
>             Set-Content $stampPath (Get-Date -Format "o")
>             return $exePath
>         }
>         Write-Host "scry $installed -> $latest available; updating $targetDir..."
>     } else {
>         Write-Host "scry.exe not currently installed; installing $latest to $targetDir..."
>     }
>
>     $asset = $release.assets | Where-Object { $_.name -eq "scry-win-$Architecture.zip" }
>     if (-not $asset) { throw "Release $($release.tag_name) has no scry-win-$Architecture.zip asset." }
>
>     # 4. Install/update. scry.exe or a staged payload/native DLL can still be locked if it's
>     #    currently injected into a running target - rename the old folder out of the way first so
>     #    a lock never corrupts a working install, and restore it if anything below fails.
>     $renamedOld = $null
>     if (Test-Path $targetDir) {
>         $renamedOld = "$targetDir-old"
>         Remove-Item $renamedOld -Recurse -Force -ErrorAction SilentlyContinue
>         try {
>             Rename-Item $targetDir $renamedOld -ErrorAction Stop
>         } catch {
>             Write-Host "scry update deferred: $targetDir is in use."
>             if ($exePath) {
>                 Set-Content $stampPath (Get-Date -Format "o")
>                 return $exePath
>             }
>             throw "$targetDir exists but is locked and could not be renamed, and no usable scry.exe was found. $_"
>         }
>     }
>
>     try {
>         $zipPath = Join-Path $env:TEMP "scry-win-$Architecture-$($release.tag_name).zip"
>         Write-Host "Downloading scry-win-$Architecture-$($release.tag_name).zip to $zipPath"
>         Invoke-WebRequest $asset.browser_download_url -OutFile $zipPath
>         Write-Host "Extracting $zipPath to $targetDir"
>         Expand-Archive $zipPath -DestinationPath $targetDir -Force
>         Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
>     } catch {
>         # Download or extract failed - undo the rename and keep the previous install working.
>         Remove-Item $targetDir -Recurse -Force -ErrorAction SilentlyContinue
>         if ($renamedOld -and (Test-Path $renamedOld)) { Rename-Item $renamedOld $targetDir }
>         if ($exePath) {
>             Set-Content $stampPath (Get-Date -Format "o")
>             Write-Host "scry update failed, keeping existing install: $_"
>             return $exePath
>         }
>         throw "Could not install scry.exe ($Architecture): $_"
>     }
>
>     if ($renamedOld -and (Test-Path $renamedOld)) {
>         Remove-Item $renamedOld -Recurse -Force -ErrorAction SilentlyContinue
>     }
>
>     # Harden the ProgramData install so no other local user can plant a DLL that an elevated
>     # scry.exe or the target would then load. Only when we installed under the ProgramData root we
>     # own - never a folder the user pointed SCRY_HOME at themselves.
>     if ($targetDir -like (Join-Path $installRoot '*')) { Set-ScryInstallRootAcl -Root $installRoot }
>
>     Set-Content $stampPath (Get-Date -Format "o")
>     [Environment]::SetEnvironmentVariable($envVar, $targetDir, 'User')
>     Set-Item "env:$envVar" $targetDir
>
>     $newExe = Join-Path $targetDir "scry.exe"
>     Write-Host "scry ready at $newExe ($($release.tag_name))"
>     return $newExe
> }
>
> function Set-ScryInstallRootAcl {
>     [CmdletBinding()]
>     param(
>         [Parameter(Mandatory)][string]$Root,
>         # Connect-ScryRemote installs as an administrator and wants a hard failure; a local install
>         # may be an ordinary user hardening their own folder, where a best-effort warning is better.
>         [switch]$ThrowOnFailure
>     )
>     New-Item -ItemType Directory -Force -Path $Root | Out-Null
>     # Well-known SIDs so it holds in any display language: SYSTEM and Administrators full, Users
>     # read+execute only. The installing user is granted full as well, so a non-administrator can
>     # still update their own install while no other non-administrator can write into (and plant a
>     # DLL in) it. Connect-ScryRemote applies the same rule to its remote ProgramData install.
>     $me = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
>     icacls $Root /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" `
>         "*S-1-5-32-545:(OI)(CI)RX" "*${me}:(OI)(CI)F" /Q | Out-Null
>     if ($LASTEXITCODE -ne 0) {
>         $message = "Could not restrict the ACL on $Root (icacls exit $LASTEXITCODE)."
>         if ($ThrowOnFailure) { throw $message }
>         Write-Host "$message The install still works; run once from an elevated prompt to harden it."
>     }
> }
> ```

### Architecture Matching for Attaching
When attaching to an existing process using `scry attach`:
- Injection requires the architecture of `scry.exe` to match the target process's bitness.
- An x64 `scry.exe` **cannot** attach to an x86 target (fails immediately with `architecture_mismatch`).
- An x86 `scry.exe` **cannot** attach to an x64 target.
- Determine the target process's architecture before attaching (for example, inspecting the process or its primary module).
- Call `Resolve-ScryCli -Architecture x64` or `-Architecture x86` to match - it installs or updates
  the CLI automatically and returns the path to use. `SCRY_HOME_X64`/`SCRY_HOME_X86` still work
  exactly as before if a human or CI pipeline has deliberately pinned them to a specific location or
  version; `Resolve-ScryCli` only takes over when nothing usable is already configured, or when the
  configured install is due for its periodic freshness check.

## Start safely

1. Call `Resolve-ScryCli -Architecture x64` or `-Architecture x86` (matching the target architecture if attaching) to get the `scry` CLI binary - it installs or updates it automatically.
2. Confirm the target is a local development or test process.
3. Run `scry discover`. If the target is running but has no endpoint yet, attach to it: `scry attach <pid-or-name> [--adapters wpf|winforms]`.
4. Select one exact target. Prefer its `targetId`; use an alias only when it is unambiguous.
5. Run `scry capabilities` and `scry roots`.
6. Prefer `inspect`, `get`, `set`, `invoke`, and `enumerate`.
7. Use `wpf.*` or `winforms.*` projection operations for UI traversal and synchronization.
8. Use `evaluate` or `execute` only when the structured operations cannot do the job. To observe a
   method *call* (arguments, return value, exception) rather than state, use `hooks` - see
   "Observing a method call".
9. Validate mutations with a fresh read, wait, or assertion.
10. Release leased handles when a persistent session is used.

Run `scry --help`, `scry help <command>`, or `scry schema` whenever the installed CLI
contract must be checked. `scry schema` is deterministic machine-readable JSON and is the
authority for command names, request fields, result shapes, and exit codes.

## Input and output contract

Send request payloads through files or standard input:

```powershell
scry inspect --target <target-id> --request inspect.json
Get-Content invoke.json | scry invoke --target <target-id>
scry evaluate --target <target-id> --source expression.csx
Get-Content statements.csx | scry execute --target <target-id>
scry scenario --input scenario.json
```

`--input <file|->` is an alias for `--request <file|->`. Redirected stdin is a JSON object
for ordinary commands and raw C# source for `evaluate`/`execute`. To provide imports,
references, or a timeout with C# source, put a complete execution request in a JSON file
and pass it with `--request`.

Do not place C# source, credentials, tokens, connection strings, personal data, or other
sensitive payloads in command-line arguments. `--json` exists only for compatibility with
non-execution, non-sensitive payloads. Capability tokens are intentionally not accepted as
CLI options.

Target operations return one JSON object on stdout:

```json
{
  "protocolVersion": 1,
  "requestId": "01J...",
  "success": true,
  "sessionId": "01J...",
  "result": {},
  "operationId": "01J...",
  "correlationId": "agent-probe-1"
}
```

On a target operation failure, `success` is `false`, `result` is absent, and `error` is
present. Always retain `operationId` and `correlationId` in agent notes because they tie a
request to target logs. Supply a stable correlation with `--correlation <id>`; otherwise
the target defaults it to the operation ID. CLI-local errors occur before a target can
issue an operation ID.

CLI-local failures also emit JSON on stdout:

```json
{
  "success": false,
  "error": {
    "code": "target_not_found",
    "message": "No live Scry target matches 'worker-a'."
  }
}
```

Treat exit codes as part of the API:

| Code | Meaning | Agent action |
|---:|---|---|
| `0` | Success | Parse stdout and continue. |
| `2` | Invalid arguments, input, or JSON | Fix the request; do not retry unchanged. |
| `3` | Target/descriptor resolution failure | Rediscover and select a live target. |
| `4` | Connection, authentication, or protocol failure | Rediscover, then retry once against the current descriptor. |
| `5` | Target operation failure | Inspect the structured target error and correct the operation. |
| `6` | Scenario partial failure | Inspect every ordered result; retry only safe failed items. |
| `70` | Unexpected CLI failure | Stop, preserve stderr/stdout, and report the failure. |

## Discover and establish context

```powershell
scry discover
```

Representative result:

```json
{
  "protocolVersion": 1,
  "targets": [
    {
      "protocolVersion": 1,
      "target": {
        "targetId": "01J...",
        "alias": "checkout-app",
        "processId": 18420,
        "processName": "Checkout.App",
        "runtimeVersion": "9.0.0",
        "frameworkDescription": ".NET 9.0.0",
        "architecture": "X64",
        "startedAt": "2026-09-10T10:00:00+00:00",
        "aliases": ["desktop-under-test"]
      },
      "descriptorPath": "C:\\Users\\me\\AppData\\Local\\Scry\\targets\\01J....json",
      "publishedAt": "2026-09-10T10:00:01+00:00"
    }
  ]
}
```

If an alias is ambiguous, do not guess. Select the intended `targetId` from discovery.
Use `--descriptor <path>` when a harness supplied an exact descriptor; otherwise prefer
`--target <target-id>`.

```powershell
scry capabilities --target <target-id> --correlation discovery-1
scry roots --target <target-id> --correlation discovery-2
```

Capabilities separate core `operations` from application/adaptor `registeredOperations`.
Only call a registered operation that appears there. Roots contain bounded `RemoteValue`
previews; previews are not full serialization.

## Structured object workflow

### Inspect and read

`inspect.json`:

```json
{
  "root": "app"
}
```

```powershell
scry inspect --target <target-id> --request inspect.json --correlation inspect-app
```

Representative `result`:

```json
{
  "type": "Checkout.AppState",
  "preview": "Checkout.AppState",
  "members": [
    {
      "name": "Count",
      "kind": "property",
      "type": "System.Int32",
      "canRead": true,
      "canWrite": true,
      "isPublic": true
    }
  ]
}
```

Use exactly one subject selector in object requests:

```json
{ "root": "app", "member": "Count" }
```

or:

```json
{
  "reference": {
    "targetId": "01J...",
    "sessionId": "01J...",
    "handleId": "01J...",
    "type": "Checkout.AppState",
    "preview": "Checkout.AppState",
    "leaseExpiresAt": "2026-09-10T10:05:00+00:00"
  },
  "member": "Count"
}
```

```powershell
scry get --target <target-id> --request get-count.json
```

Scalar `RemoteValue` shape:

```json
{
  "value": {
    "kind": "scalar",
    "type": "System.Int32",
    "preview": "7",
    "value": 7
  }
}
```

Reference values carry an `ExternalReference`. References are scoped to their target and
session. A new stateless CLI invocation normally creates an ephemeral session, so use
`--session <session-id>` for a multi-command reference workflow. Do not mix references
between targets or sessions.

### Mutate and invoke

`set-count.json`:

```json
{
  "root": "app",
  "member": "Count",
  "value": 8
}
```

```powershell
scry set --target <target-id> --request set-count.json --correlation set-count-8
scry get --target <target-id> --request get-count.json --correlation verify-count-8
```

Always verify a mutation with a fresh read or assertion.

Invoke a member with structured arguments:

```json
{
  "root": "app",
  "member": "Greet",
  "arguments": ["Ada"]
}
```

Invoke an application-registered operation:

```json
{
  "registeredOperation": "reset",
  "arguments": {
    "scope": "cart"
  }
}
```

```powershell
scry invoke --target <target-id> --request reset.json --correlation reset-cart
```

Do not use C# execution when a registered operation or member invocation expresses the
same intent.

### Enumerate and release

```json
{
  "root": "numbers",
  "offset": 0,
  "limit": 100
}
```

```powershell
scry enumerate --target <target-id> --request first-page.json
```

Honor `hasMore`; advance by the count actually returned, not by the requested limit.
Responses may end early to stay below the protocol frame budget.

Use `asReference` or `asReferences` only when a boxed value must be inspected or invoked
later. Release handles owned by persistent sessions:

```json
{
  "handleIds": ["01JHANDLE1", "01JHANDLE2"]
}
```

```powershell
scry release --target <target-id> --session <session-id> --request release.json
```

## Framework-neutral waits and assertions

`wait` and `assert` evaluate a C# expression and compare its result. Prefer them over polling by
hand, and over `wpf.wait`/`winforms.wait` whenever the thing you care about is application state
rather than a rendered element: the adapter conditions only see the bounded UI-tree projection,
while these see anything an expression can reach - view models, services, counters, a worker's
queue depth. They are the only wait/assert available in a console, service, or worker target.

Request fields: `source` (required), `operator` (`isTrue` by default, plus `equals`, `notEquals`,
`contains`, `isNull`, `isNotNull`), `expected` (a string, number, boolean, or null - required for
`equals`, `notEquals`, and `contains`), `timeoutMilliseconds` (default 5000, `wait` only),
`pollIntervalMilliseconds` (default 100, `wait` only), `imports`, `references`, and `marshal`.

Choose between them by what a miss should mean:

- `wait` returns `satisfied: false` on timeout as a **successful** response. Read
  `satisfied`; do not treat exit code 0 as the condition having held.
- `assert` fails the request with `assertion_failed` and a message describing the comparison. Use
  it for the check you want to surface as a failure.

```json
{
  "source": "((MyApp.MainViewModel)Context.Roots[\"mainViewModel\"]).IsLoaded",
  "timeoutMilliseconds": 10000,
  "pollIntervalMilliseconds": 100
}
```

```powershell
scry wait --target <target-id> --request wait-loaded.json --correlation load-gate
scry assert --target <target-id> --request assert-count.json
```

Results carry `satisfied`, `attempts`, `elapsedMilliseconds`, the projected `value`, and a
`description`. `attempts` is worth checking when a wait passes suspiciously fast - it tells you
whether the condition was already true on the first evaluation.

Expressions are compiled against the target's loaded assemblies, so cast `Context.Roots[...]`
(typed `object`) to a type the target actually has, or call members available on `object`. A
`compilation_failed` error here means the expression, not the condition, is wrong.

## Reaching UI-owned state: `marshal`

WPF and WinForms objects have thread affinity, and endpoint requests are served on a non-UI
thread. So `get`, `set`, `invoke`, `inspect`, `enumerate`, `evaluate`, `execute`, `wait`, and
`assert` all fail with `operation_failed` and "The calling thread cannot access this object because
a different thread owns it" when they touch a `DependencyObject` or a `Control`. That is the
framework's own rule, not an endpoint restriction.

Add `"marshal": "ui"` to run on the target's UI thread instead:

```json
{ "reference": { "...": "..." }, "member": "Title", "marshal": "ui" }
```

Check availability before relying on it: a `capabilities` response lists `ui-thread-marshalling`
only when the target registered a marshaller. It is registered by `UseWpf`/`UseWinForms` in an
embedded host, or by `scry attach --adapters wpf|winforms`. Without one the request is refused up
front with `marshal_target_unavailable` rather than failing later with a cross-thread exception, and
an unrecognised target gives `marshal_target_not_supported`.

Two things to keep in mind:

- A marshalled `evaluate`/`execute` **occupies the UI thread for the whole submission**, and
  `timeoutMilliseconds` cannot interrupt work already running there. Keep marshalled submissions
  short; never loop or sleep inside one.
- `wait` marshals each individual evaluation, not the polling loop, so a marshalled wait does not
  hold the UI thread between attempts. Long `timeoutMilliseconds` on a marshalled `wait` is fine;
  long-running work inside a marshalled `evaluate` is not.

For a bulk read of UI structure, prefer `wpf.snapshot`/`winforms.snapshot`: they marshal internally,
return a bounded projection in one request, and do not need `marshal`.

### Repeated submissions are cheap; the first one is not

Compiling a submission builds a metadata reference for every loaded assembly and then runs codegen,
which in a large application takes seconds. Repeating the *same* submission reuses the compiled
script, so `wait` polls at essentially the cost of its poll interval. `evaluate`/`execute` results
carry `compilationCached` so you can tell a reused script from a cold compile.

Practical consequences:

- Reuse one expression across polls rather than varying it. A condition that embeds a changing
  value in its source recompiles every time; put the varying part in `expected` instead.
- Budget for the first call. Against a large application expect several seconds for the first
  `evaluate` after attaching, and near-zero afterwards.
- A marshalled poll still waits for the target's UI thread. While the application is busy with its
  own startup, each attempt can cost roughly a second regardless of caching.

### Setting a value and acting on it need separate submissions

WPF updates bindings on a later dispatcher turn. A submission that sets a control's value and then
invokes the command that reads it will see the *old* value, because the binding has not run yet.
This is easy to get wrong and fails silently - the command executes with stale input.

Send two requests instead: one to set the value, one to invoke. Each submission is its own
dispatcher turn, so bindings have run in between. This matters most for `PasswordBox`, whose
`Password` is not a dependency property and is usually surfaced to a command through a
`CommandParameter` binding or a behaviour.

## WPF recipe

The target must advertise `wpf.snapshot`, `wpf.wait`, `wpf.assert`, and
`wpf.screenshot` in `registeredOperations`. The CLI exposes these as direct commands and
internally uses the structured registered-operation protocol.

Start with a bounded snapshot:

```json
{
  "root": "main",
  "tree": "visual"
}
```

```powershell
scry wpf.snapshot --target <target-id> --request wpf-snapshot.json --correlation ui-map
```

Use the visual tree for rendered controls, bounds, focus, template-generated visuals, and
screenshots. Use `"tree":"logical"` for logical content, data contexts, and elements that
are not represented by the visual hierarchy. If one view does not contain the element,
retry once with the other view. Never infer absence when `truncated` is `true`.

Wait for UI state instead of sleeping:

```json
{
  "root": "main",
  "tree": "visual",
  "automationId": "SubmitButton",
  "state": "enabled",
  "expected": "true",
  "timeoutMilliseconds": 10000,
  "pollIntervalMilliseconds": 100
}
```

```powershell
scry wpf.wait --target <target-id> --request wpf-wait.json --correlation submit-ready
```

Assert the resulting state:

```json
{
  "root": "main",
  "tree": "visual",
  "automationId": "StatusText",
  "state": "textEquals",
  "expected": "Completed"
}
```

```powershell
scry wpf.assert --target <target-id> --request wpf-assert.json
```

`WpfWaitResult` includes `satisfied`, `conclusive`, `elapsed`, `match`, and
`description`. A negative result with `conclusive:false` means the bounded projection was
truncated; increase target-side projection limits or narrow the root rather than treating
it as a definitive absence.

Capture only when a visual artifact is needed:

```json
{
  "root": "main",
  "path": "root:main/visual[0]",
  "tree": "visual"
}
```

```powershell
scry wpf.screenshot --target <target-id> --request wpf-shot.json
```

Screenshots return base64 PNG data inside JSON and are bounded. WPF projections and
screenshots can omit popups, separate HWNDs, `HwndHost`, WebView2, protected content,
owner-drawn content, and out-of-process surfaces.

## WinForms recipe

The target must advertise the four `winforms.*` registered operations. Snapshot the
managed control hierarchy:

```json
{
  "root": "main"
}
```

```powershell
scry winforms.snapshot --target <target-id> --request forms-snapshot.json
```

Nodes include control path, type, name, text, bounds, visibility, enabled/focus state,
bindings, owned forms, menu/tool-strip items, and children. Locate controls by stable
`name` first and retain the returned `path` for exact follow-up operations.

Wait without blocking the UI thread:

```json
{
  "root": "main",
  "name": "saveButton",
  "state": "enabled",
  "expected": "true",
  "timeoutMilliseconds": 10000,
  "pollIntervalMilliseconds": 100
}
```

```powershell
scry winforms.wait --target <target-id> --request forms-wait.json
```

Validate:

```json
{
  "root": "main",
  "name": "statusLabel",
  "state": "textEquals",
  "expected": "Saved"
}
```

```powershell
scry winforms.assert --target <target-id> --request forms-assert.json
```

Capture a registered root or projected child:

```json
{
  "root": "main",
  "path": "root:main/control[2]"
}
```

```powershell
scry winforms.screenshot --target <target-id> --request forms-shot.json
```

WinForms projection is a managed `Control` view. Owner-drawn pixels, native child HWNDs,
ActiveX, WebView2, protected content, and some `DrawToBitmap` surfaces may be absent.

## Non-UI and service recipe

Scry.NET does not require a desktop UI. For a service, worker, console app, or test host:

1. Discover the process and verify its process identity.
2. Read `capabilities` and `roots`.
3. Inspect the service/provider/state root.
4. Prefer a registered health, reset, drain, reindex, or diagnostic operation.
5. Enumerate bounded queues or collections page by page.
6. Gate on asynchronous work with `wait`, and verify the outcome with `assert` - these are the
   deterministic validation primitives available here, since there is no UI tree to query and the
   `wpf.*`/`winforms.*` conditions do not apply. No `marshal` is needed in a non-UI target.
7. Verify the operation through a state root or registered assertion operation.

Example health request:

```json
{
  "registeredOperation": "health.snapshot",
  "arguments": {
    "includeDependencies": true
  }
}
```

```powershell
scry invoke --target worker-a --request health.json --correlation health-worker-a
```

## C# evaluation and execution escape hatch

Use C# only when structured inspection/invocation cannot express the operation. Read-only
`evaluate` is preferable to mutating `execute`.

`expression.csx`:

```csharp
Context.GetRoot("app").GetType().FullName
```

```powershell
scry evaluate --target <target-id> --source expression.csx --correlation eval-type
```

For statements, `statements.csx` can use `await`, `Context.CancellationToken`, and an
explicit `return`:

```csharp
var service = Context.GetRoot("service");
Context.Log($"Inspecting {service.GetType().FullName}");
await Task.Delay(10, Context.CancellationToken);
return service.ToString();
```

```powershell
Get-Content statements.csx | scry execute --target <target-id> --correlation exec-probe
```

Execution settings request:

```json
{
  "source": "return Context.GetRoot(\"app\").GetType().FullName;",
  "imports": ["System"],
  "references": [],
  "timeoutMilliseconds": 5000
}
```

`references`, when non-empty, narrows compilation to only those named assemblies plus whatever
Scry.NET's own runtime needs (the core library and its own dependencies) - it is not an addition on
top of every loaded assembly. Naming the specific assembly a script actually needs compiles faster
and keeps the script cache's memory footprint down; omitting it compiles against every loaded
compatible assembly instead, which is fine occasionally but adds up across many distinct cached
scripts in a long session. On modern .NET, `evaluate`/`execute` bind by default only against the
default `AssemblyLoadContext` and the runtime's own. To reference a type from another context - for
example one loaded via `load-assembly --loadPolicy isolated` - add `loadContext` naming that
context (the same name `list-assemblies`/`find-types`/`describe-type` report) alongside its
assembly in `references`. `loadContext` only widens which load contexts are eligible (never
narrows it on its own); combined with a non-empty `references` list, the two together are what
actually narrows the final compiled set. `loadContext` is rejected with
`load_context_not_supported` on .NET Framework, which has no load contexts. It is unrelated to
`--appdomain`, which targets a .NET Framework AppDomain, not a load context.

**Prefer a fixed script body with `arguments` over string-substituting values into `source`.** A
building-block-style caller that repeats the same script shape with only a literal changing (a
workspace name, a section tag, an id) should send one fixed `source` and vary `arguments` instead:

```json
{
  "source": "return Context.GetRoot(\"app\").Navigate(Context.GetArgument<string>(\"section\"));",
  "arguments": { "section": "Alerts" }
}
```

`Context.GetArgument<T>(name)` reads a named value back out. This matters beyond convenience:
`arguments` is deliberately not part of the script cache key, so the same `source` called
repeatedly with different argument values is always a cache hit - a script whose `source` instead
embeds the varying literal directly is always a fresh compile, and each compile binds against the
full reference set above. A caller that instead splices arbitrary caller-supplied code into control
flow (a boolean predicate expression, free-text embedded in a condition) still varies by source
text regardless of `arguments` - a specific `references` list is what actually helps those cases.

Timeouts are cooperative, not process isolation. Synchronous target code that ignores
cancellation keeps running in the target - especially serious for a `"marshal": "ui"`
submission, which holds the target's UI thread until it returns. `scry`'s own `--timeout`
(default 60s) bounds how long the CLI itself waits for a reply, so a wedged submission fails
the command with `connection_failed` instead of hanging the agent's shell forever - but it
does not stop the target from still running the code. Compilation errors return
`compilation_failed` with structured diagnostics. Never retry unchanged source after a
compilation or validation failure.

Only `Context.Log(...)` calls are captured and returned in `logs`. `Console.WriteLine`,
`Trace.Write`, and similar target-process output are not intercepted and go wherever the
target's own console or trace listeners send them - typically nowhere, for a GUI process.
Use `Context.Log` for anything a script needs to report back.

## Long-running jobs

Use jobs when work must survive the initiating CLI connection, needs cooperative
cancellation, or emits progress logs.

`job-start.json`:

```json
{
  "operation": "invoke",
  "payload": {
    "registeredOperation": "reindex",
    "arguments": {
      "scope": "catalog"
    }
  },
  "correlationId": "catalog-reindex"
}
```

```powershell
scry jobs start --target worker-a --request job-start.json --correlation start-reindex
```

Persist the returned complete `job` object:

```json
{
  "targetId": "01J...",
  "sessionId": "01J...",
  "jobId": "01J..."
}
```

The handle carries target and session identity, so follow-up commands can infer the
session:

```powershell
scry jobs status --target worker-a --request job-query.json
scry jobs logs --target worker-a --request job-logs.json
scry jobs wait --target worker-a --request job-wait.json
scry jobs cancel --target worker-a --request job-query.json
```

`job-logs.json`:

```json
{
  "job": {
    "targetId": "01J...",
    "sessionId": "01J...",
    "jobId": "01J..."
  },
  "cursor": 0,
  "limit": 100
}
```

Advance to `nextCursor`. If `truncated` is true, logs before `oldestCursor` are gone.
`job.wait` returning `timedOut:true` is not a failed job and does not change its state.
Cancellation is cooperative. Terminal states are `succeeded`, `failed`, and `canceled`.

## Observing a method call (hooks)

`evaluate`, `get` and `wait` see state, not calls. When the question is "did this method run, what
was it given, what did it return, did it throw" - especially for a private, non-virtual method that
leaves no trace - hook it. A hook records calls without changing the method's behavior.

Check `capabilities`: `method-hooks` must be in `features`. Find the declaring type first
(`find-types`, `describe-type` with `includeNonPublic`), because `type` must be the exact full name.

`hook-add.json`:

```json
{
  "type": "Shop.CheckoutService",
  "method": "PlaceOrder",
  "parameterTypes": ["Order"]
}
```

```powershell
scry hooks add --target app --request hook-add.json
```

Read the result before relying on the hook:

- `warnings` - `inlining_risk_high` means a caller that inlined the method bypasses the hook, so a
  hook that never fires proves nothing; `foreign_harmony_loaded` and `method_patched_by_others`
  explain interference. `inlining.risk` of `none` means the method cannot be inlined.
- `created: false` means this session already hooked the method; the existing hook is returned.
- Failures are specific: `type_not_found`, `member_not_found`, `ambiguous_member` (pass
  `parameterTypes`; the message lists the overloads), `method_not_patchable`, `hooks_unavailable`,
  `method_patched_by_foreign_harmony`.

Persist the returned `result.hook` handle. It carries the session, so later commands resume it.
`hook-wait.json`:

```json
{
  "hook": { "targetId": "01J...", "sessionId": "01J...", "hookId": "01J..." },
  "predicate": "((Shop.Order)Args[0]).Lines.Any(line => line.Sku == \"SKU-100\") && (bool)ReturnValue",
  "timeoutMilliseconds": 60000
}
```

```powershell
scry hooks wait --target app --request hook-wait.json
```

The predicate is C# evaluated in the target against the real captured objects, with `Args`
(`object[]`), `ReturnValue`, `Exception`, `Instance` and `Call` in scope, and must produce a `bool`.
Omit it to take the first call. It runs in this request, never inside the patched method.

- `satisfied: true` - `call` has `arguments`, `returnValue` (a scalar or value type carries its
  `value`), `exception`, `outcome` (`returned` or `threw`), `threadId` and `sequence`.
- `timedOut: true` is a normal response, not an error. Read `diagnostics`: with no call ever
  recorded it says so and repeats the inlining risk. `evaluated`, `totalCalls` and `droppedCalls` tell
  you whether calls were seen and missed.
- To continue past a call, pass its `nextCursor` as `cursor` so the next wait sees only newer calls.
- A reference-type argument is reported by type (`kind: "preview"`). Use the predicate to test its
  contents, or set `"includeReferences": true` to get a handle you can `inspect`.

`scry hooks read` pages recorded calls by `cursor`/`limit` (`truncated` means older calls were
dropped; raise `capacity` on add); `scry hooks drain` reads then discards; `scry hooks list --session
<id>` shows a session's hooks. A hook does not survive its session, so use `--session`/the handle
rather than an ephemeral connection.

**Always remove the hook when done** - it patches the live process:

```powershell
scry hooks remove --target app --request hook-remove.json
```

A hook on a type in a non-default AppDomain (.NET Framework) must be added through a sibling endpoint
started for that AppDomain with `appdomain.start`; the default-domain endpoint answers
`type_not_found`. Do not hook a method on a hot path casually: each call pays for the capture, and a
buffered call keeps its arguments alive.

## Multi-target scenario and batch

Use a scenario for an explicit multi-process flow. Each command must select exactly one
`target` or `descriptor`; do not rely on global target state. Concurrent results are still
returned in input order.

```json
{
  "mode": "concurrent",
  "commands": [
    {
      "id": "api-health",
      "target": "api-worker",
      "operation": "invoke",
      "payload": {
        "registeredOperation": "health.snapshot",
        "arguments": {}
      },
      "correlationId": "deploy-42-api"
    },
    {
      "id": "desktop-ready",
      "target": "desktop-app",
      "operation": "wpf.wait",
      "payload": {
        "root": "main",
        "automationId": "ReadyIndicator",
        "state": "textEquals",
        "expected": "Ready",
        "timeoutMilliseconds": 15000
      },
      "correlationId": "deploy-42-ui"
    }
  ]
}
```

```powershell
scry scenario --input deployment-check.json
```

Representative result:

```json
{
  "protocolVersion": 1,
  "mode": "concurrent",
  "success": false,
  "results": [
    {
      "id": "api-health",
      "index": 0,
      "operation": "invoke",
      "targetSelector": "api-worker",
      "target": {},
      "response": {
        "protocolVersion": 1,
        "requestId": "01J...",
        "success": true,
        "sessionId": "01J...",
        "result": {},
        "operationId": "01J...",
        "correlationId": "deploy-42-api"
      },
      "success": true
    },
    {
      "id": "desktop-ready",
      "index": 1,
      "operation": "wpf.wait",
      "targetSelector": "desktop-app",
      "error": {
        "code": "target_not_found",
        "message": "No live Scry target matches 'desktop-app'."
      },
      "success": false
    }
  ]
}
```

Exit code `6` means at least one item failed. Inspect all results. Retry only failed,
idempotent items after rediscovery; do not repeat successful mutations.

## Reaching a target on another machine

`scry discover` and `--target <alias>` only ever look at the local
`%USERPROFILE%\.scry\targets` directory - they can never surface a target that lives on another
machine. A remote target is always addressed with `--descriptor <path>` (the copied descriptor
file, kept outside the local targets directory) plus `--address <host:port|port|auto>`, never with
`--target`.

Remote execution (`Invoke-Command` or equivalent) is needed only for the **one-time setup**: attach
on the remote machine with `scry attach <pid> --tcp-port 0`, copy the resulting descriptor to this
machine, and stand up a port forward to the bound port. Once the forward is up, every subsequent
command runs **locally** and simply dials the forwarded port - do not shell out to the remote
machine per request; that defeats the reason this exists (a fresh ephemeral session per remote
invocation would drop every leased handle).

```powershell
scry evaluate --descriptor .\remote-target.json --address 127.0.0.1:9000 --source "1 + 1"
```

A `scenario` command can mix a local selector (`target` or a local `descriptor`) with a remote one
(`descriptor` plus `address`) in the same flow - this is the intended way to drive a local desktop
client and assert on a remote server component as one call. See
[`README.md`, "Reaching an endpoint on another machine"](../../README.md#reaching-an-endpoint-on-another-machine)
for the full setup recipe and the trust tradeoff (a loopback TCP listener has no OS peer check; the
capability token in the descriptor is the only gate). Treat a remote descriptor with the same care
as a local one - never copy it into logs, chat, or source control - and delete it once the task is
done, since it is a bearer credential for the remote endpoint's whole lifetime.

### Automating the setup: `Connect-ScryRemote`

The setup above is mechanical and easy to get wrong by hand. `Connect-ScryRemote` below performs
exactly the documented steps - nothing else - and `Disconnect-ScryRemote` removes exactly what it
added. **Use these functions rather than improvising the steps**, so that what runs on the user's
machines is this reviewed sequence rather than ad-hoc commands.

**Before calling `Connect-ScryRemote`, ask the user for what you do not already know**, in one
question, and do not guess:

- the remote computer name
- the process (a pid, or a name - the function lists the candidates with their pid, bitness, owning
  user, service or app-pool name and full command line if a name is ambiguous, so the user can pick)
- the adapters (`wpf`/`winforms`) and the AppDomain, if the target needs either. **For IIS - a
  `w3wp` process, or anything the user describes as an IIS or ASP.NET application - use
  `--appdomain auto`**, which `Connect-ScryRemote` applies by itself for `w3wp`. The application's
  own code lives in a non-default AppDomain, so a plain attach reaches nothing useful. `auto` is
  safe where an explicit selector is not: it resolves to the single non-default domain or else the
  default one, never to a guess among several, and modern .NET ignores it. If the pool hosts
  several applications it lands in the default domain, and `appdomain.list` then
  `appdomain.start` reaches the right one from there - no second injection needed, and the process
  only ever gets one.
- whether to use the current Windows identity or prompt for a credential

Then state plainly what the run will do, and get an explicit "yes" before the first call:

> This attaches Scry to `<process>` on `<computer>` (injecting into that process), forwards its
> loopback port here, and needs local administrator rights for the port forward. If the connection
> does not come up without one, it also adds an inbound firewall rule on `<computer>` for that one
> port, restricted to this machine's address. `Disconnect-ScryRemote` removes the forwards, the
> rule and the local descriptor afterwards.

The user must have administrative rights on the target machine and be authorized to attach to that
process. This is the same authorization rule as every other use of Scry - it is not loosened by
being remote.

#### Running these functions

**Install them once, do not re-transcribe them every session.** Check
`%LOCALAPPDATA%\Scry.NET\ScryRemote.ps1`: if it is missing, or its first line is not
`# ScryRemote 2`, write the code block below to that path verbatim. Then dot-source it:

```powershell
. "$env:LOCALAPPDATA\Scry.NET\ScryRemote.ps1"
```

Copying several hundred lines into a scratch file on every run costs two tool calls and risks a
silent transcription error. Writing it once, keyed on that version line, costs nothing after the
first time.

**Never try to drive `Get-Credential` yourself.** It needs a live interactive prompt, which an
agent's shell does not have: running it through a terminal-handoff tool typically leaves the
command sitting unexecuted and then cancels it, so the dialog appears and dies. There are two
supported routes, in this order:

1. **A saved credential.** Ask the user to run this once, in their own terminal, for the target
   machine. `Connect-ScryRemote` then finds it automatically and nothing is interactive afterwards:

   ```powershell
   Save-ScryRemoteCredential -ComputerName <computer>
   ```

   It is stored with `Export-Clixml`, so the password is DPAPI-protected and readable only by that
   Windows account on that machine. It is still a reusable secret: tell the user it exists and that
   `Remove-ScryRemoteCredential -ComputerName <computer>` deletes it.

2. **A one-off handoff**, when the user would rather not save anything. Write a small driver script
   that calls `Get-Credential` and then `Connect-ScryRemote`, give the user the single command that
   runs it, and wait. Do not try to execute it for them. When they say it is done, read
   `%LOCALAPPDATA%\Scry.NET\remote\<id>\connection.json` for the result rather than scraping their
   terminal.

The current Windows identity is used when neither applies, which is the normal case for a
domain-joined target reached by name. A bare IP address or a workgroup machine almost always needs
one of the two routes above.

```powershell
# ScryRemote 2

function Connect-ScryRemote {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ComputerName,
        # A pid, or a process name. A name matching several processes stops with the candidates
        # listed - ask the user which one rather than picking.
        [Parameter(Mandatory)][string]$Process,
        [ValidateSet("wpf", "winforms")][string[]]$Adapters,
        # Defaults to "auto" for an IIS worker - see the note where it is applied below.
        [string]$AppDomain,
        # Omit to use a credential saved by Save-ScryRemoteCredential, or failing that the caller's
        # own Windows identity. Never collect a password yourself and never put one in a script.
        [pscredential]$Credential,
        [int]$LocalPort = 0,
        # Only used if the firewall fallback below turns out to be needed.
        [string]$ClientAddress,
        [ValidateSet("auto", "ssh", "netsh")][string]$Transport = "auto",
        [string]$RemoteTargetsDir = "C:\ScryRendezvous"
    )

    $ErrorActionPreference = "Stop"

    # A credential saved earlier for this machine beats prompting, because an agent cannot prompt.
    if (-not $Credential) { $Credential = Get-ScryRemoteCredential -ComputerName $ComputerName }

    # Preflight WinRM before anything else runs. A bare IP or a workgroup host usually needs both
    # an explicit credential and a TrustedHosts entry, and saying which is missing beats letting
    # New-PSSession fail thirty seconds later with a generic message.
    $wsman = @{ ComputerName = $ComputerName; ErrorAction = "SilentlyContinue" }
    if ($Credential) { $wsman.Credential = $Credential; $wsman.Authentication = "Negotiate" }
    if (-not (Test-WSMan @wsman)) {
        $hint = ""
        if (-not $Credential) {
            $hint = (" No credential was supplied or saved - if this is a bare IP or a workgroup " +
                     "machine, ask the user to run " +
                     "'Save-ScryRemoteCredential -ComputerName $ComputerName' in their own terminal.")
        }
        throw ("WinRM did not answer on $ComputerName. Check that the service is running there, " +
               "and that this machine trusts it (Get-Item WSMan:\localhost\Client\TrustedHosts)." +
               " Scry never changes WinRM configuration itself - ask the user to do it.$hint")
    }

    $sessionArgs = @{ ComputerName = $ComputerName }
    if ($Credential) { $sessionArgs.Credential = $Credential }
    $session = New-PSSession @sessionArgs
    # Every key exists from the start, and each is filled the moment the thing it describes is
    # created - never at the end. A connect that dies half way must still hand the cleanup path a
    # complete picture of what it made, or it leaks (a stranded descriptor is a stranded bearer
    # credential).
    $state = [ordered]@{
        Id = $null; Computer = $ComputerName; CreatedAt = (Get-Date -Format "o"); StateDir = $null
        RemoteProxy = $null; LocalProxy = $null; FirewallRule = $null; SshProcessId = $null
    }

    try {
        # 1. Identify the process and its bitness. Bitness decides which scry.exe can attach at
        #    all - an x64 injector cannot touch an x86 target, and vice versa.
        $candidates = @(Invoke-Command -Session $session -ArgumentList $Process -ScriptBlock {
            param($spec)
            $signature = @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool IsWow64Process(System.IntPtr hProcess, out bool wow64Process);
'@
            $kernel32 = Add-Type -MemberDefinition $signature -Name ScryKernel32 `
                -Namespace ScryRemoteSetup -PassThru
            $found = if ($spec -match '^\d+$') {
                @(Get-Process -Id ([int]$spec) -ErrorAction SilentlyContinue)
            } else {
                @(Get-Process -Name ($spec -replace '\.exe$', '') -ErrorAction SilentlyContinue)
            }

            $byPid = @{}
            Get-CimInstance Win32_Process | ForEach-Object { $byPid[[int]$_.ProcessId] = $_ }
            $services = @{}
            Get-CimInstance Win32_Service | Where-Object { $_.ProcessId } |
                ForEach-Object { $services[[int]$_.ProcessId] = $_.Name }

            foreach ($item in $found) {
                # On a 64-bit OS, "not running under WOW64" means a 64-bit process.
                $isWow64 = $false
                try { [void]$kernel32::IsWow64Process($item.Handle, [ref]$isWow64) } catch { }
                $info = $byPid[[int]$item.Id]
                $owner = $null
                if ($info) {
                    try { $owner = (Invoke-CimMethod -InputObject $info -MethodName GetOwner).User }
                    catch { }
                }
                $commandLine = $null
                if ($info) { $commandLine = $info.CommandLine }
                [pscustomobject]@{
                    Id = $item.Id
                    Name = $item.ProcessName
                    Bitness = $(if ($isWow64) { "x86" } else { "x64" })
                    User = $owner
                    Service = $services[[int]$item.Id]
                    CommandLine = $commandLine
                }
            }
        })

        if ($candidates.Count -eq 0) {
            throw "No process matching '$Process' on $ComputerName."
        }
        if ($candidates.Count -gt 1) {
            # The command line is what actually tells two w3wp.exe or two service hosts apart -
            # an IIS worker carries its pool as `-ap "<pool>"`.
            $listing = ($candidates | ForEach-Object {
                "  pid $($_.Id)  $($_.Bitness)  user=$($_.User)  service=$($_.Service)`n" +
                "      $($_.CommandLine)"
            }) -join "`n"
            throw ("'$Process' matches $($candidates.Count) processes on $ComputerName. " +
                   "Ask the user which pid to attach to:`n$listing")
        }

        $target = $candidates[0]
        $architecture = $target.Bitness

        # An IIS application's own code does not run in the process's default AppDomain, so a plain
        # attach to w3wp lands somewhere that can confirm the process is alive and nothing else.
        # "auto" takes the single non-default domain when there is exactly one - the ordinary
        # one-application pool - and otherwise the default domain, which is just a plain attach. It
        # is never a guess among several, so it cannot land somewhere the caller did not ask for,
        # and it is ignored outright on modern .NET (an in-process ASP.NET Core worker is also
        # w3wp.exe). That makes it strictly better than omitting it here.
        if (-not $AppDomain -and $target.Name -eq "w3wp") {
            $AppDomain = "auto"
            Write-Host "IIS worker: attaching with --appdomain auto."
        }

        # 2. Make sure a matching scry.exe exists on the remote machine, in a folder the *target's*
        #    account can read. The native helper and payload are loaded inside the target under
        #    its own token, so an install under the connecting user's profile works against a
        #    LocalSystem service and fails against one running as a domain account. ProgramData is
        #    readable by every local account by default. The folder is versioned, so installing a
        #    newer copy never has to overwrite files an already-attached target still has loaded.
        #    This machine's own install is copied over the session; the target downloads nothing.
        $localExe = Resolve-ScryCli -Architecture $architecture
        $localDir = Split-Path $localExe -Parent
        $version = (& $localExe --version).Trim()
        $remoteDir = Invoke-Command -Session $session -ArgumentList $version, $architecture `
            -ScriptBlock {
                param($ver, $arch)
                return (Join-Path $env:ProgramData "Scry.NET\cli\$ver\$arch")
            }
        $installed = Invoke-Command -Session $session -ArgumentList $remoteDir -ScriptBlock {
            param($dir)
            # Readable by every account, writable only by administrators. ProgramData itself lets
            # Users *create* files in subfolders, and anything a non-administrator could add here
            # would be loaded by an elevated scry.exe or by the target - so the inherited ACL is
            # replaced, on every connect, which also corrects an install made before this existed.
            # Well-known SIDs (SYSTEM, Administrators, Users) so it works in any display language.
            $root = Join-Path $env:ProgramData "Scry.NET\cli"
            New-Item -ItemType Directory -Force -Path $root | Out-Null
            icacls $root /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" `
                "*S-1-5-32-545:(OI)(CI)RX" /Q | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not restrict the ACL on $root (icacls exit $LASTEXITCODE)." }
            Test-Path (Join-Path $dir "scry.exe")
        }

        if (-not $installed) {
            # Copy into a staging folder and rename it into place last, so an interrupted copy can
            # never leave a half-populated folder that the check above would then trust.
            Invoke-Command -Session $session -ArgumentList $remoteDir -ScriptBlock {
                param($dir)
                Remove-Item "$dir.partial" -Recurse -Force -ErrorAction SilentlyContinue
                New-Item -ItemType Directory -Force -Path "$dir.partial" | Out-Null
            }
            Write-Host "Copying scry $version ($architecture) to $ComputerName`:$remoteDir"
            Copy-Item -Path (Join-Path $localDir "*") -Destination "$remoteDir.partial" `
                -ToSession $session -Recurse -Force
            Invoke-Command -Session $session -ArgumentList $remoteDir -ScriptBlock {
                param($dir) Rename-Item "$dir.partial" (Split-Path $dir -Leaf)
            }
        }
        $remoteExe = Join-Path $remoteDir "scry.exe"

        # 3. Attach on the remote machine. --tcp-port 0 is mandatory for a forward to exist at all,
        #    and --targets-dir keeps the descriptor somewhere both identities can reach.
        $adapterArgument = $null
        if ($Adapters) { $adapterArgument = ($Adapters -join ",") }
        $attach = Invoke-Command -Session $session -ScriptBlock {
            param($exe, $targetPid, $dir, $adapters, $domain)
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $arguments = @("attach", "$targetPid", "--tcp-port", "0", "--targets-dir", $dir)
            if ($adapters) { $arguments += @("--adapters", $adapters) }
            if ($domain) { $arguments += @("--appdomain", $domain) }
            $output = (& $exe @arguments 2>&1 | Out-String)

            # An endpoint already in the process is a reusable result, not a failure: a process
            # gets exactly one injection for its lifetime, and there is no detach.
            if ($output -notmatch '"success"\s*:\s*true') {
                $existing = (& $exe discover --targets-dir $dir 2>&1 | Out-String)
                return [pscustomobject]@{ Attach = $output; Discover = $existing }
            }
            return [pscustomobject]@{ Attach = $output; Discover = $null }
        } -ArgumentList $remoteExe, $target.Id, $RemoteTargetsDir, $adapterArgument, $AppDomain

        $descriptorPathRemote = $null
        $remotePort = $null
        $attachResult = $null
        try { $attachResult = $attach.Attach | ConvertFrom-Json } catch { }

        if ($attachResult -and $attachResult.success) {
            $descriptorPathRemote = $attachResult.descriptorPath
            $remotePort = $attachResult.tcpPort
        } elseif ($attach.Discover) {
            $discovered = $null
            try { $discovered = $attach.Discover | ConvertFrom-Json } catch { }
            $match = $null
            if ($discovered) {
                $match = $discovered.targets |
                    Where-Object { $_.target.processId -eq $target.Id } | Select-Object -First 1
            }
            if (-not $match) { throw "Attach failed on $ComputerName`: $($attach.Attach)" }
            if (-not $match.tcpPort) {
                throw ("Process $($target.Id) already hosts a Scry endpoint with no TCP listener, " +
                       "so it cannot be reached from this machine. The process has to be " +
                       "restarted and re-attached with --tcp-port 0.")
            }
            Write-Host "Reusing the endpoint already attached to pid $($target.Id)."
            $descriptorPathRemote = $match.descriptorPath
            $remotePort = $match.tcpPort
        } else {
            throw "Attach failed on $ComputerName`: $($attach.Attach)"
        }

        # 4. Bring the descriptor here, over the session. It is a bearer credential, so it travels
        #    only on this channel, is written outside the local targets directory, and is never
        #    printed.
        $connectionId = [guid]::NewGuid().ToString("n").Substring(0, 12)
        $stateDir = Join-Path $env:LOCALAPPDATA "Scry.NET\remote\$connectionId"
        New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
        $state.Id = $connectionId
        $state.StateDir = $stateDir
        $descriptorPath = Join-Path $stateDir "descriptor.json"
        $descriptorJson = Invoke-Command -Session $session -ArgumentList $descriptorPathRemote `
            -ScriptBlock { param($path) Get-Content -LiteralPath $path -Raw }
        Set-Content -LiteralPath $descriptorPath -Value $descriptorJson -Encoding utf8

        if ($LocalPort -le 0) {
            $listener = New-Object System.Net.Sockets.TcpListener(
                [System.Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $LocalPort = $listener.LocalEndpoint.Port
            $listener.Stop()
        }

        $targetIp = ([System.Net.Dns]::GetHostAddresses($ComputerName) |
            Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1).ToString()

        # 5. Stand up the forward. SSH is preferred when it is there: it carries the token inside
        #    an encrypted tunnel, and it needs no administrator rights or firewall changes.
        $useSsh = $false
        if ($Transport -ne "netsh") {
            $useSsh = $null -ne (Get-Command ssh -ErrorAction SilentlyContinue) -and
                (Test-NetConnection -ComputerName $ComputerName -Port 22 `
                    -InformationLevel Quiet -WarningAction SilentlyContinue)
            if ($Transport -eq "ssh" -and -not $useSsh) {
                throw "-Transport ssh was requested but port 22 on $ComputerName is not reachable."
            }
        }

        if ($useSsh) {
            $sshUser = $env:USERNAME
            if ($Credential) { $sshUser = $Credential.UserName }
            $tunnel = Start-Process ssh -PassThru -WindowStyle Hidden -ArgumentList @(
                "-N", "-L", "${LocalPort}:127.0.0.1:$remotePort", "$sshUser@$ComputerName")
            $state.SshProcessId = $tunnel.Id
            $transportUsed = "ssh"
        } else {
            $isAdmin = ([Security.Principal.WindowsPrincipal] `
                [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
                    [Security.Principal.WindowsBuiltInRole]::Administrator)
            if (-not $isAdmin) {
                throw ("The netsh port forward needs local administrator rights. Re-run this from " +
                       "an elevated session, or enable OpenSSH Server on $ComputerName to use the " +
                       "ssh transport instead.")
            }

            Invoke-Command -Session $session -ArgumentList $targetIp, $remotePort -ScriptBlock {
                param($ip, $port)
                netsh interface portproxy add v4tov4 listenaddress=$ip listenport=$port `
                    connectaddress=127.0.0.1 connectport=$port | Out-Null
            }
            $state.RemoteProxy = @{ Address = $targetIp; Port = $remotePort }

            netsh interface portproxy add v4tov4 listenaddress=127.0.0.1 listenport=$LocalPort `
                connectaddress=$targetIp connectport=$remotePort | Out-Null
            $state.LocalProxy = @{ Address = "127.0.0.1"; Port = $LocalPort }
            $transportUsed = "netsh"
        }

        # 6. Prove the path end to end. The firewall rule is added only if this fails - most
        #    networks do not need one, and an unnecessary rule is an unnecessary opening.
        # `capabilities` is the cheapest proof of a working link: it completes a full handshake
        # (so the token and the forward are both good) and runs nothing inside the target.
        $localExe = Resolve-ScryCli -Architecture x64
        function Test-ScryRemoteLink {
            # A failed probe writes to stderr, which Windows PowerShell turns into a terminating
            # NativeCommandError while $ErrorActionPreference is Stop. A failed probe is an
            # expected outcome here, so let it come back as text.
            $previous = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            try {
                $probe = (& $localExe capabilities --descriptor $descriptorPath `
                    --address "127.0.0.1:$LocalPort" 2>&1 | Out-String)
            } finally { $ErrorActionPreference = $previous }
            return ($probe -match '"success"\s*:\s*true')
        }

        if (-not (Test-ScryRemoteLink)) {
            if ($transportUsed -eq "netsh") {
                if (-not $ClientAddress) {
                    $ClientAddress = (Find-NetRoute -RemoteIPAddress $targetIp `
                        -ErrorAction SilentlyContinue | Select-Object -First 1).IPAddress
                }
                if ($ClientAddress) {
                    $ruleName = "Scry remote $connectionId"
                    Write-Host "Connection did not come up; adding a scoped firewall rule."
                    Invoke-Command -Session $session -ScriptBlock {
                        param($name, $port, $client)
                        New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow `
                            -Protocol TCP -LocalPort $port -RemoteAddress $client | Out-Null
                    } -ArgumentList $ruleName, $remotePort, $ClientAddress
                    $state.FirewallRule = $ruleName
                }
            }

            if (-not (Test-ScryRemoteLink)) {
                Disconnect-ScryRemote -Connection ([pscustomobject]$state) -Session $session
                $reachable = Test-NetConnection -ComputerName $targetIp -Port $remotePort `
                    -InformationLevel Quiet -WarningAction SilentlyContinue
                throw ("Could not reach the endpoint. Remote listener port: $remotePort; " +
                       "transport: $transportUsed; $targetIp`:$remotePort reachable: $reachable. " +
                       "Everything this function added has been removed.")
            }
        }

        $state.ProcessId = $target.Id
        $state.Bitness = $architecture
        $state.LocalPort = $LocalPort
        $state.RemotePort = $remotePort
        $state.DescriptorPath = $descriptorPath
        $state.Transport = $transportUsed
        $statePath = Join-Path $stateDir "connection.json"
        ($state | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $statePath -Encoding utf8

        Remove-PSSession $session
        Write-Host ("Connected: pid $($target.Id) ($architecture) on $ComputerName via " +
                    "$transportUsed. Use --descriptor `"$descriptorPath`" --address 127.0.0.1:$LocalPort")
        return [pscustomobject]$state
    } catch {
        if ($session) {
            try { Disconnect-ScryRemote -Connection ([pscustomobject]$state) -Session $session }
            catch { }
            Remove-PSSession $session -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Disconnect-ScryRemote {
    [CmdletBinding()]
    param(
        # The object Connect-ScryRemote returned, or its connection.json.
        [Parameter(Mandatory)]$Connection,
        # Internal: reuses the open session during a failed connect.
        $Session
    )

    if ($Connection -is [string]) { $Connection = Get-Content $Connection -Raw | ConvertFrom-Json }

    # The endpoint itself stays: a process gets exactly one injection for its lifetime and Scry
    # has no detach. This removes the reachability and the credential, not the endpoint.
    $ownsSession = $false
    if (-not $Session -and ($Connection.RemoteProxy -or $Connection.FirewallRule)) {
        $Session = New-PSSession -ComputerName $Connection.Computer
        $ownsSession = $true
    }

    $removed = @()
    if ($Connection.SshProcessId) {
        Stop-Process -Id $Connection.SshProcessId -Force -ErrorAction SilentlyContinue
        $removed += "ssh tunnel"
    }
    if ($Connection.LocalProxy) {
        netsh interface portproxy delete v4tov4 `
            listenaddress=$($Connection.LocalProxy.Address) `
            listenport=$($Connection.LocalProxy.Port) | Out-Null
        $removed += "local port forward"
    }
    if ($Connection.RemoteProxy) {
        Invoke-Command -Session $Session -ArgumentList $Connection.RemoteProxy -ScriptBlock {
            param($proxy)
            netsh interface portproxy delete v4tov4 listenaddress=$($proxy.Address) `
                listenport=$($proxy.Port) | Out-Null
        }
        $removed += "remote port forward"
    }
    if ($Connection.FirewallRule) {
        Invoke-Command -Session $Session -ArgumentList $Connection.FirewallRule -ScriptBlock {
            param($name) Remove-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
        }
        $removed += "firewall rule"
    }
    if ($Connection.StateDir -and (Test-Path $Connection.StateDir)) {
        Remove-Item $Connection.StateDir -Recurse -Force -ErrorAction SilentlyContinue
        $removed += "descriptor"
    }
    if ($ownsSession -and $Session) { Remove-PSSession $Session -ErrorAction SilentlyContinue }
    if ($removed.Count -gt 0) { Write-Host "Removed: $($removed -join ', ')." }
}

function Get-ScryRemoteConnection {
    [CmdletBinding()]
    param()

    # Forwards and descriptors outlive the session that created them: a session that ends without
    # calling Disconnect-ScryRemote leaves both in place. Start here rather than assuming a clean
    # slate, and offer to disconnect anything stale.
    $root = Join-Path $env:LOCALAPPDATA "Scry.NET\remote"
    if (-not (Test-Path $root)) { return }

    foreach ($dir in Get-ChildItem $root -Directory) {
        $statePath = Join-Path $dir.FullName "connection.json"
        if (Test-Path $statePath) {
            $connection = Get-Content $statePath -Raw | ConvertFrom-Json
            $connection | Add-Member -NotePropertyName Tracked -NotePropertyValue $true -Force
            $connection
        } else {
            # A connect that died before writing its state file. Whatever forwards it made are no
            # longer described anywhere, but the descriptor it left behind is still a bearer
            # credential - so surface it as something to remove.
            [pscustomobject]@{
                Id = $dir.Name; Computer = $null; StateDir = $dir.FullName; Tracked = $false
            }
        }
    }
}

function Get-ScryRemoteCredentialPath {
    param([Parameter(Mandatory)][string]$ComputerName)
    $safe = ($ComputerName.ToLowerInvariant() -replace '[^a-z0-9._-]', '_')
    return (Join-Path $env:LOCALAPPDATA "Scry.NET\remote-credentials\$safe.xml")
}

function Save-ScryRemoteCredential {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ComputerName)

    # The user runs this themselves. Get-Credential needs a live prompt, which an agent shell does
    # not have. Export-Clixml protects the password with DPAPI, so the file can only be read back
    # by this Windows account on this machine - it is not portable and not plaintext. It is still a
    # reusable secret: Remove-ScryRemoteCredential deletes it.
    $path = Get-ScryRemoteCredentialPath -ComputerName $ComputerName
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    Get-Credential -Message "Credential for $ComputerName (WinRM)" | Export-Clixml -LiteralPath $path
    Write-Host ("Saved a DPAPI-protected credential for $ComputerName. Delete it with " +
                "Remove-ScryRemoteCredential -ComputerName $ComputerName.")
}

function Get-ScryRemoteCredential {
    param([Parameter(Mandatory)][string]$ComputerName)
    $path = Get-ScryRemoteCredentialPath -ComputerName $ComputerName
    if (Test-Path $path) {
        # A credential saved by another account, or copied from another machine, cannot be
        # decrypted here. Say so rather than failing later as an authentication error.
        try { return (Import-Clixml -LiteralPath $path) }
        catch { Write-Warning "A saved credential for $ComputerName could not be read: $_" }
    }
    return $null
}

function Remove-ScryRemoteCredential {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ComputerName)
    $path = Get-ScryRemoteCredentialPath -ComputerName $ComputerName
    if (Test-Path $path) {
        Remove-Item $path -Force
        Write-Host "Removed the saved credential for $ComputerName."
    } else {
        Write-Host "No saved credential for $ComputerName."
    }
}
```

Using the result: every later command runs **locally** against the forwarded port, exactly as the
section above describes.

```powershell
. "$env:LOCALAPPDATA\Scry.NET\ScryRemote.ps1"
# -AppDomain defaults to "auto" for w3wp; pass a FriendlyName prefix to override it.
$remote = Connect-ScryRemote -ComputerName APP-01 -Process w3wp
try {
    scry inspect --descriptor $remote.DescriptorPath `
        --address "127.0.0.1:$($remote.LocalPort)" --request request.json
} finally {
    Disconnect-ScryRemote -Connection $remote
}
```

**Before connecting, run `Get-ScryRemoteConnection`.** A session that ended without disconnecting
leaves its port forwards up and its descriptor on disk, and neither expires. Report anything it
returns to the user and offer to `Disconnect-ScryRemote` it. An entry with `Tracked = $false` is a
connect that died midway: its descriptor is still a live bearer credential, so it should be removed
even though there is nothing else to tear down.

Always run `Disconnect-ScryRemote` when the task ends, including on failure - the `finally` above is
the shape to use. It removes the port forwards, any firewall rule that was added and the local
descriptor. Never print the descriptor's contents, and never pass its path to anything but `scry`.

## Attaching across Windows identities on the same machine

A target running as a different Windows identity than the one attaching - a service account, or
an IIS application pool, especially one with no loaded user profile - needs `--targets-dir <path>`
on `scry attach`, and that **same** `--targets-dir` on every command afterward, including
`scry discover`: the descriptor lives only in the directory it was told to use. It also needs
`--tcp-port 0` (or a fixed port) on the attach itself, because the named pipe is protected to the
identity that created it - a cross-identity attach can only ever be verified over TCP, never the
pipe, no exceptions. If `--targets-dir` was given but the target's identity still cannot write to
it, grant access once with `icacls <path> /grant "<target identity>":(OI)(CI)M`; Scry never
modifies permissions itself. The target's identity must also be able to **read the scry folder
itself**: the native helper and payload are loaded inside the target under its own account, not
yours, so an install under your profile (where `Resolve-ScryCli` puts it by default) works against
a LocalSystem service and fails against one running as a domain account. `scry attach` checks this
before injecting and refuses with `target_cannot_read_components`, naming the account and file;
copy the scry folder somewhere that account can read, such as under `C:\ProgramData`, and attach
from the copy. See
[`README.md`, "Attaching to a service or IIS application pool"](../../README.md#attaching-to-a-service-or-iis-application-pool).

## Reaching a chosen AppDomain (.NET Framework)

Injection always lands in the target's default AppDomain, which for a host like IIS is not where
the hosted application's own code runs - an ordinary attach there can confirm the process is alive
and nothing about the application inside it. Use `scry attach <pid> --appdomain <id|name|auto>` to
place the endpoint in a specific AppDomain instead, or attach normally and reach another domain
afterward with `scry appdomain.list --target <alias> --request '{}'` then
`scry appdomain.start --target <alias> --request '{"selector":"<selector>"}'` - the latter is
strictly better once an endpoint already exists, since a bad `--appdomain` selector at attach time
falls back to the default domain rather than failing (the process gets exactly one native
injection, so a failed attach cannot simply be retried), while `appdomain.start` fails cleanly on
a selector that matches zero or several domains. A selector may be a numeric id, an exact
`FriendlyName`, or a *prefix* of one - the way to name an ASP.NET application stably, since its
domain's `FriendlyName` carries a volatile trailing sequence that changes on recycle but its
leading application id does not. Both `--appdomain` and `appdomain.*` are .NET Framework only;
there is nothing to select on a modern .NET target. See
[`README.md`, "Reaching a chosen AppDomain"](../../README.md#reaching-a-chosen-appdomain).

## Structured error recovery

Use the error code before the message:

- `invalid_request`, `argument_*`, or `usage_error`: correct fields/types using
  `scry help <command>` or `scry schema`.
- `target_not_found`: rediscover; an old target may have exited or an alias may be
  ambiguous.
- `target_cannot_read_components` (attach): the target's account cannot read the scry folder.
  Nothing was injected, so retrying from a readable copy is safe - tell the user which account and
  folder the message names rather than changing permissions yourself.
- `operation_not_found`: refresh capabilities; do not invent registered operation names.
- `operation_not_supported`: the target/CLI protocol surface does not support that core
  operation.
- `handle_not_found`, lease, or scope errors: reacquire the value and keep target/session
  identity consistent.
- `compilation_failed`: inspect diagnostics and change source; do not retry unchanged.
- `execution_timed_out`: assume synchronous work may still be running unless the operation
  is known to cooperate with cancellation.
- connection/protocol failure: rediscover and retry once only when the requested action is
  read-only or known idempotent.

Never blindly retry `set`, `invoke`, `execute`, job start, or another mutating request.
First determine whether the target may already have applied it. Use correlation IDs and a
fresh read/status query to establish the current state.

## Safety boundaries

- Use Scry.NET only for local development and test targets that deliberately opted in.
- Never copy descriptors or capability tokens to logs, chat, telemetry, source control, or
  remote systems.
- Never expose or bridge the named-pipe protocol over a network.
- Setting up a remote target changes two machines: it injects into a process on the target and
  opens a port forward. Confirm the computer, the process and the credential mode with the user
  first, use `Connect-ScryRemote` rather than improvising the steps, and always finish with
  `Disconnect-ScryRemote`. Never collect a password yourself - pass `(Get-Credential)` so the user
  types it, or rely on the current Windows identity.
- Treat non-public inspection, C# execution and method hooks as privileged debugging actions. A hook
  patches the live process and keeps captured arguments (which may be secrets) in memory until read,
  drained or removed: hook only methods you need, keep `capacity` small, and always `hooks remove`.
- Prefer public structured operations and the least mutation necessary.
- Do not execute downloaded, unreviewed, or user-secret-bearing source.
- Do not claim a projection is complete when it is bounded or truncated.
- Do not claim a hard timeout; cancellation is cooperative.
- Preserve operation/correlation IDs in failure reports, but redact sensitive result data.
