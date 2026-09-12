# Scry.NET

Scry.NET is a Windows-only developer and test framework for inspecting and deliberately mutating a running managed application. Each target owns its endpoint and state; the stateless `scry` CLI connects over a local Windows named pipe.

Two hosting models are available:

- **Embedded mode:** the target opts in with `Scry.Endpoint`, registers described roots, values, and policy-tagged operations, and starts an `EndpointHost`. Registrations can also be replaced or removed safely while the host is running.
- **Attach mode:** `scry attach` loads an architecture-matched native bootstrap into an already-running managed process and starts the same `EndpointHost` in its default AppDomain, so the target never references Scry.NET.

The endpoint supports non-UI processes as a first-class scenario. The core packages provide a versioned JSON protocol, current-user named-pipe transport, capability-token authentication, multi-process discovery with aliases, target-qualified sessions and leased handles, reflection inspection/mutation/invocation, collection pagination, framework-neutral waits and assertions, Roslyn-backed C# evaluation and statement execution, explicit assembly/type discovery, endpoint-owned long-running jobs, an embedded endpoint host, a standalone client, and a stateless JSON CLI with multi-target scenarios. Optional `Scry.Wpf` and `Scry.WinForms` packages add desktop UI inspection without adding UI framework references to `Scry.Contracts`, `Scry.Runtime`, or `Scry.Endpoint`. An agent Skill ships in [`skills/scry`](skills/scry/SKILL.md).

| Component | Supported targets |
|---|---|
| `Scry.Contracts`, `Scry.Runtime`, `Scry.Endpoint`, `Scry.Client` | .NET 9 and .NET Framework 4.7.2 |
| `Scry.SampleHost` | .NET 9 and .NET Framework 4.7.2 |
| `Scry.Cli` | .NET 9 only; it can connect to either runtime |
| `Scry.Wpf`, `Scry.WinForms` | .NET 9 (Windows) and .NET Framework 4.7.2 |
| `Scry.Injector`, `Scry.Injector.Payload`, native bootstrap | The injector runs on .NET 9 and attaches to Windows x86/x64 processes on .NET Framework 4.7.2 or .NET 9. x64 is verified end to end; x86 is implemented but unverified. |

Scry.NET permits deliberate code execution and state mutation inside the target. It is **local-only tooling for development and testing**, not a remote administration service. That is a statement about authorization, not a technical limit: attach works against Release builds as readily as Debug ones, because nothing in the path reads debug symbols - `CreateRemoteThread`/`LoadLibrary` is an operating-system facility, `ExecuteInDefaultAppDomain` is a CLR hosting API, and Roslyn compiles against metadata, which is identical either way. Two differences are worth knowing when targeting a Release build: an obfuscated assembly breaks expressions that name members, and `#if DEBUG` code is absent, so the application itself can behave differently. Pipe names and tokens are random, pipes are current-user-only, and capability tokens are stored only in the current user's rendezvous directory. .NET 9 uses `PipeOptions.CurrentUserOnly`; .NET Framework 4.7.2 creates a protected pipe DACL granting only the current Windows SID. Do not expose descriptors or bridge the protocol to untrusted clients.

Attach mode is intentionally restricted to processes running at the same or a lower Windows integrity level and requires an injector with the same architecture as the target. It inspects process architecture and loaded CLR modules before writing target memory, refuses unknown/ambiguous runtimes, and reports structured failures for access, loader, bootstrap, duplicate-injection, and likely antivirus/EDR blocking. Injecting code can destabilize the target and commonly triggers endpoint-security controls; use it only on applications and machines you are authorized to test.

Current attach limits are: default AppDomain/default CoreCLR load context only, x86 and x64 only (x64 verified, x86 unverified), .NET Framework 4.7.2 and .NET 9 only, no secondary-AppDomain targeting, no ARM64, and no production packaging.

Build and test:

```powershell
dotnet build Scry.sln
dotnet test Scry.sln --no-build
dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net472 --artifacts-path artifacts\net472-x64 -p:PlatformTarget=x64 -- RunConfiguration.TargetPlatform=x64
dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net472 --artifacts-path artifacts\net472-x86 -p:PlatformTarget=x86 -- RunConfiguration.TargetPlatform=x86
dotnet test tests\Scry.Wpf.Tests\Scry.Wpf.Tests.csproj -c Release -f net472
dotnet test tests\Scry.WinForms.Tests\Scry.WinForms.Tests.csproj -c Release -f net472
```

Run any embedded sample, then use the emitted descriptor path:

```powershell
dotnet run --project src\Scry.Cli -- capabilities --descriptor <path>
dotnet run --project src\Scry.Cli -- roots --descriptor <path>
dotnet run --project src\Scry.Cli -- evaluate --descriptor <path> --source expression.csx
Get-Content statements.csx | dotnet run --project src\Scry.Cli -- execute --descriptor <path>
dotnet run --project src\Scry.Cli -- jobs start --target scry-sample --request job-start.json
dotnet run --project src\Scry.Cli -- scenario --input scenario.json
```

Run `scry --help` or `scry help <command>` for examples and the stable exit-code contract.
`scry schema` emits the deterministic machine-readable command, argument, request, result,
and exit-code catalog. Request payloads should come from `--request <file|->` (or
`--input`) and C# source from `--source <file|->` or redirected stdin; agents never need
to put source or secrets on a command line. Direct `wpf.*` and `winforms.*` CLI commands
translate to their registered structured operations and return their structured adapter
result inline before the ephemeral CLI session closes.

AI coding agents should follow the comprehensive
[`skills/scry/SKILL.md`](skills/scry/SKILL.md) workflow. It covers discovery and safe
target selection, structured inspection before code execution, desktop and non-UI
recipes, waits/assertions, jobs, multi-process scenarios, retries, expected JSON shapes,
and security boundaries.

## Attaching to a process that does not reference Scry

```powershell
dotnet run --project src\Scry.Cli -c Release -- attach <pid-or-process-name> [--adapters wpf|winforms]
```

The command injects the endpoint, waits for its discovery descriptor, performs a real protocol handshake, and prints structured JSON without exposing the capability token. Build the architecture-matched native helpers first as described in [`docs/development.md`](docs/development.md).

Pass `--adapters wpf` (or `winforms`) to wire the matching desktop adapter inside the target. Without it an attached endpoint has only the framework-neutral surface, so `wpf.*` operations are absent and `evaluate`/`execute` cannot use `"marshal": "ui"` - which means they cannot touch a `DependencyObject` or a `Control` at all. The adapter needs no cooperation from the target application: it discovers `Application.Current.Windows` and reuses the target's existing dispatcher.

See [`docs/development.md`](docs/development.md) for protocol and extension guidance.

## Embedded SDK

Register the smallest intentional surface an agent needs. Root factories are evaluated per request, registered values retain a stable object, and operation descriptions and policy metadata are returned by `capabilities`:

```csharp
using var host = EndpointHost.Start(
    builder => builder
        .RegisterRoot("orders", () => orderState, "Current order processing state.")
        .RegisterValue("service.name", "checkout", "Stable service identity.")
        .RegisterOperation(
            "orders.reprocess",
            ReprocessOrder,
            "Reprocesses one order.",
            new OperationPolicy { RequiresConfirmation = true }),
    new EndpointOptions { Alias = "checkout-worker" });
```

Each registered operation reports `executionPolicy` (`worker-thread` or `ui-owner`), `isReadOnly`, and `requiresConfirmation`. These fields document the handler's contract; `ui-owner` means the handler or adapter performs the required marshalling, not that the core runtime guesses a dispatcher. Agents should call `roots` and `capabilities` first, prefer described read-only operations, request approval before confirmation-required operations, and avoid raw reflection mutation when a named helper exists.

Runtime registration changes are atomic:

```csharp
host.Registrations.RegisterValue(
    "feature.flags",
    refreshedFlags,
    "Current feature flags.",
    RegistrationMode.ReplaceExisting);
host.Registrations.UnregisterOperation("orders.reprocess");
```

Duplicate registration throws unless `ReplaceExisting` is selected. Replacement affects subsequent lookups; existing leased references remain valid until released or expired. Unregistration is idempotent through its `bool` result and does not revoke already leased objects or stop an operation/job that has already started.

Runnable integrations cover every supported process style:

| Sample | Process type | Domain flow |
|---|---|---|
| `samples\Scry.SampleHost` | Console | Set/inspect a counter, run a recalculation job |
| `samples\Scry.SampleWorker` | Long-running worker | Change queue mode, inspect heartbeats, drain work as a job |
| `samples\Scry.SampleWpf` | WPF | Update dispatcher-owned editor state, assert projected UI, load as a job |
| `samples\Scry.SampleWinForms` | WinForms | Update owner-thread order state, assert controls, import as a job |

Each prints `Target` and `Descriptor` on startup and exits cleanly when Enter is sent. See the development guide for complete adoption and validation flows.

## Optional desktop adapters

Reference only the adapter used by the target application, then register it while configuring the embedded host:

```csharp
using var host = EndpointHost.Start(builder => builder.UseWpf(
    Application.Current,
    wpf => wpf.RegisterWindow("main", Application.Current.MainWindow)));
```

```csharp
using var host = EndpointHost.Start(builder => builder.UseWinForms(
    mainForm,
    winForms => winForms.RegisterRoot("main", mainForm)));
```

The adapters register `wpf.*` or `winforms.*` snapshot, wait, assertion, and screenshot operations as read-only, `ui-owner` helpers. Their projections are deliberately bounded and framework-specific. WPF visual and logical trees are separate views; WinForms exposes managed controls, open/owned forms, tool strips and menus, and bindings. Neither adapter claims to represent owner-drawn pixels, WebView2/ActiveX content, native child windows, popups/separate HWNDs, or out-of-process surfaces completely.

## Waiting and asserting

`wait` and `assert` evaluate a C# expression and compare its result, with operators `isTrue`
(the default), `equals`, `notEquals`, `contains`, `isNull` and `isNotNull`. They are
framework-neutral, so they work in a console, service or worker target that has no UI tree at all,
and they can assert on application state that the `wpf.*`/`winforms.*` conditions cannot see,
because those only observe the bounded UI-tree projection.

```powershell
scry wait --target app --request wait-loaded.json
scry assert --target app --request assert-count.json
```

`wait` polls until the condition holds or `timeoutMilliseconds` elapses, and reports a timeout as a
successful response carrying `satisfied: false` — read the flag, do not infer it from the exit code.
Repeating a submission reuses its compiled script, so polling costs about the poll interval rather
than a recompile per attempt; `evaluate`/`execute` results carry `compilationCached` to distinguish
a reused script from a cold compile.
`assert` evaluates once and fails the request with `assertion_failed` and a description of the
comparison. Use `wait` to gate, `assert` to fail.

## Reaching UI-owned state

WPF and WinForms objects have thread affinity, and requests are served on a non-UI thread. So
`get`, `set`, `invoke`, `inspect`, `enumerate`, `evaluate`, `execute`, `wait` and `assert` all fail
with `InvalidOperationException: The calling thread cannot access this object because a different
thread owns it` when they touch a `DependencyObject` or a `Control`. That is WPF's and WinForms' own
rule, not an endpoint restriction.

Add `"marshal": "ui"` to run on the target's UI thread instead:

```powershell
scry evaluate --descriptor <path> --json '{"source":"System.Windows.Application.Current.MainWindow.Title","marshal":"ui"}'
```

Registering `UseWpf` or `UseWinForms`, or attaching with `--adapters`, enables this; a capabilities
response lists `ui-thread-marshalling` when it is available. Without a marshaller the request is
refused with `marshal_target_unavailable` rather than failing later with a cross-thread exception,
and an unrecognised target is refused with `marshal_target_not_supported`.

Two consequences worth knowing. A marshalled `evaluate`/`execute` **occupies the UI thread** for the
whole submission, so a long-running or looping script freezes the target, and `TimeoutMilliseconds`
cannot interrupt work already running there — keep marshalled submissions short. Such a submission
both *starts* and *resumes* there: a marshalled script that awaits comes back to the UI thread rather
than falling onto the thread pool mid-way. `wait` is the exception by design: it marshals each
evaluation rather than the polling loop, so a long marshalled wait never holds the UI thread between
attempts.
