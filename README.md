# Scry.NET

Scry.NET is a Windows-only developer and test framework for inspecting and deliberately mutating a running managed application. Each target owns its endpoint and state; the stateless `scry` CLI connects over a local Windows named pipe.

Two hosting models are planned:

- **Embedded mode (implemented):** the target opts in with `Scry.Sdk`, registers described roots, values, and policy-tagged operations, and starts an `AgentHost`. Registrations can also be replaced or removed safely while the host is running.
- **Attach mode (future):** tooling injects or loads the runtime into an existing managed process. Injection is not part of this foundation.

The endpoint supports non-UI processes as a first-class scenario. The core packages provide a versioned JSON protocol, current-user named-pipe transport, capability-token authentication, multi-process discovery with aliases, target-qualified sessions and leased handles, reflection inspection/mutation/invocation, collection pagination, Roslyn-backed C# evaluation and statement execution, explicit assembly/type discovery, endpoint-owned long-running jobs, an embedded SDK, and a stateless JSON CLI with multi-target scenarios. Optional `Scry.Wpf` and `Scry.WinForms` packages add desktop UI inspection without adding UI framework references to `Scry.Contracts`, `Scry.Runtime`, or `Scry.Sdk`. Injection and an agent Skill remain deferred.

| Component | Supported targets |
|---|---|
| `Scry.Contracts`, `Scry.Runtime`, `Scry.Sdk` | .NET 9 and .NET Framework 4.7.2 |
| `Scry.SampleHost` | .NET 9 and .NET Framework 4.7.2 |
| `Scry.Cli` | .NET 9 only; it can connect to either runtime |
| `Scry.Wpf`, `Scry.WinForms` | .NET 9 (Windows) and .NET Framework 4.7.2 |

Scry.NET permits deliberate code execution and state mutation inside the target. It is **local-only developer/test tooling**, not a remote administration service. Pipe names and tokens are random, pipes are current-user-only, and capability tokens are stored only in the current user's rendezvous directory. .NET 9 uses `PipeOptions.CurrentUserOnly`; .NET Framework 4.7.2 creates a protected pipe DACL granting only the current Windows SID. Do not expose descriptors or bridge the protocol to untrusted clients.

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
dotnet run --project src\Scry.Cli -- jobs start --target scry-sample --json '{"operation":"invoke","payload":{"registeredOperation":"delay","arguments":{"milliseconds":1000}}}'
dotnet run --project src\Scry.Cli -- scenario --input scenario.json
```

See [`docs/development.md`](docs/development.md) for protocol and extension guidance.

## Embedded SDK

Register the smallest intentional surface an agent needs. Root factories are evaluated per request, registered values retain a stable object, and operation descriptions and policy metadata are returned by `capabilities`:

```csharp
using var host = AgentHost.Start(
    builder => builder
        .RegisterRoot("orders", () => orderState, "Current order processing state.")
        .RegisterValue("service.name", "checkout", "Stable service identity.")
        .RegisterOperation(
            "orders.reprocess",
            ReprocessOrder,
            "Reprocesses one order.",
            new AgentOperationPolicy { RequiresConfirmation = true }),
    new AgentHostOptions { Alias = "checkout-worker" });
```

Each registered operation reports `executionPolicy` (`worker-thread` or `ui-owner`), `isReadOnly`, and `requiresConfirmation`. These fields document the handler's contract; `ui-owner` means the handler or adapter performs the required marshalling, not that the core runtime guesses a dispatcher. Agents should call `roots` and `capabilities` first, prefer described read-only operations, request approval before confirmation-required operations, and avoid raw reflection mutation when a named helper exists.

Runtime registration changes are atomic:

```csharp
host.Registrations.RegisterValue(
    "feature.flags",
    refreshedFlags,
    "Current feature flags.",
    AgentRegistrationMode.ReplaceExisting);
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
using var host = AgentHost.Start(builder => builder.UseWpf(
    Application.Current,
    wpf => wpf.RegisterWindow("main", Application.Current.MainWindow)));
```

```csharp
using var host = AgentHost.Start(builder => builder.UseWinForms(
    mainForm,
    winForms => winForms.RegisterRoot("main", mainForm)));
```

The adapters register `wpf.*` or `winforms.*` snapshot, wait, assertion, and screenshot operations as read-only, `ui-owner` helpers. Their projections are deliberately bounded and framework-specific. WPF visual and logical trees are separate views; WinForms exposes managed controls, open/owned forms, tool strips and menus, and bindings. Neither adapter claims to represent owner-drawn pixels, WebView2/ActiveX content, native child windows, popups/separate HWNDs, or out-of-process surfaces completely.

## Running a submission on the UI thread

`evaluate` and `execute` run on whichever endpoint thread serves the request, which is **not** the UI thread. A submission that touches a `DependencyObject` or a `Control` therefore fails with `InvalidOperationException: The calling thread cannot access this object because a different thread owns it`. That is WPF's and WinForms' own thread affinity, not an endpoint restriction.

Add `"marshal": "ui"` to run the whole submission on the UI thread instead:

```powershell
scry evaluate --descriptor <path> --json '{"source":"System.Windows.Application.Current.MainWindow.Title","marshal":"ui"}'
```

Registering `UseWpf` or `UseWinForms` enables this; a capabilities response lists `ui-thread-marshalling` when it is available. Without a marshaller the request is refused with `marshal_target_unavailable` rather than failing later with a cross-thread exception, and an unrecognised target is refused with `marshal_target_not_supported`.

Two consequences worth knowing. The submission **blocks the UI thread** for its duration, so a long-running or looping script freezes the target, and `TimeoutMilliseconds` cannot interrupt work already running on that thread — keep marshalled submissions short. And the submission both *starts* and *resumes* there: a marshalled script that awaits comes back to the UI thread rather than falling onto the thread pool mid-way.
