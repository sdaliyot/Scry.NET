# Foundation development guide

## Architecture

| Project | Responsibility |
|---|---|
| `Scry.Contracts` | Wire contracts, framing, target descriptors, discovery |
| `Scry.Runtime` | Named-pipe server, sessions, handles, reflection, Roslyn execution, assembly catalog, process-scoped jobs |
| `Scry.Sdk` | Embedded `AgentHost`, registration builder, protocol client |
| `Scry.Wpf` | Optional dispatcher-safe WPF projections, waits/assertions, screenshots |
| `Scry.WinForms` | Optional control-owner-marshalled WinForms projections, waits/assertions, screenshots |
| `Scry.Cli` | Stateless `scry` JSON command line |
| `Scry.SampleHost` | Non-UI embedded example |
| `Scry.Tests` | Protocol, runtime, and discovery tests |
| `Scry.Wpf.Tests` | STA dispatcher tests for the optional WPF adapter |
| `Scry.WinForms.Tests` | STA message-loop tests for the optional WinForms adapter |

Target names and compatibility package versions are centralized in `Directory.Build.props`.

| Project | Target frameworks | Notes |
|---|---|---|
| `Scry.Contracts` | `net9.0`, `net472` | Identical protocol and descriptor shape |
| `Scry.Runtime` | `net9.0`, `net472` | CoreCLR load contexts or desktop CLR default-AppDomain behavior |
| `Scry.Sdk` | `net9.0`, `net472` | Embedded host and client |
| `Scry.SampleHost` | `net9.0`, `net472` | Non-UI embedded sample |
| `Scry.Tests` | `net9.0`, `net472` | Runtime integration suite; CLI tests run on `net9.0` |
| `Scry.Wpf` | `net9.0-windows`, `net472` | Optional WPF adapter; desktop CLR uses direct assembly references |
| `Scry.WinForms` | `net9.0-windows`, `net472` | Optional Windows Forms adapter; desktop CLR uses direct assembly references |
| `Scry.Wpf.Tests` | `net9.0-windows`, `net472` | STA dispatcher tests, both runtimes |
| `Scry.WinForms.Tests` | `net9.0-windows`, `net472` | STA message-loop tests, both runtimes |
| `Scry.Cli` | `net9.0` | Modern-only executable that interoperates with both host targets |

The `Microsoft.NETFramework.ReferenceAssemblies.net472` package makes SDK-style net472 builds independent of machine-installed targeting packs. Runtime validation still requires Windows with .NET Framework 4.7.2 installed.

## Protocol and security

Frames are a 4-byte little-endian length followed by UTF-8 JSON. Protocol version 1 requires `handshake` first. The handshake authenticates a 256-bit random capability token, negotiates the version, creates or resumes a target-qualified session, and returns capabilities. Subsequent requests use structured success/error envelopes. Every handled request receives a target-generated `operationId`; a supplied `correlationId` is echoed, or defaults to that operation ID. Ordinary operation exceptions cross the boundary with type, message, stack, HResult, source, and recursively captured inner exceptions. Fatal runtime failures such as process termination, stack overflow, corrupted state, or fail-fast can bypass this boundary.

Discovery descriptors live under `%LOCALAPPDATA%\Scry\targets` and are removed on host disposal and normal process exit. Any number of embedded hosts may publish simultaneously, including multiple processes with the same alias. Resolution accepts a target ID, canonical alias, or additional alias; an ambiguous alias is rejected and callers must select a target ID. On .NET 9 the named pipe uses `PipeOptions.CurrentUserOnly`. On .NET Framework 4.7.2 the server creates a protected, non-inheriting DACL with an allow rule only for the current Windows user SID; it does not fall back to a broadly accessible pipe. Descriptors and tokens must never be copied to logs, command-line arguments, telemetry, or remote systems. The CLI accepts a descriptor **path** or target identity/alias and reads the token locally.

Sessions belong to one target. Object references contain target, session, and handle IDs, preventing accidental cross-target/session use. Handles are strong references with sliding leases, stable identity within a session, explicit release, and cleanup on expiry/session disposal. Previews are bounded and are not object serialization.

## Embedded API

Start the endpoint once and keep the returned host alive:

```csharp
using var host = AgentHost.Start(
    builder => builder
        .RegisterValue("services", serviceProvider)
        .RegisterRoot("current", () => currentState)
        .RegisterOperation("reset", (_, _) =>
        {
            currentState.Reset();
            return ValueTask.FromResult<object?>(null);
        }),
    new AgentHostOptions
    {
        Alias = "my-test-target",
        Aliases = ["checkout-a", "worker"]
    });
```

`RegisterValue` retains a specific object, while `RegisterRoot` evaluates its factory for each request. Registered operations receive structured JSON rather than source text. `RegisterJobOperation` additionally receives an `OperationExecutionContext` with the operation ID, correlation ID, cooperative `CancellationToken`, and bounded job logger. Session, handle, and job limits; lease and retention durations; aliases; preview length; and log bounds are configurable. A retained job keeps its qualified session addressable until the job is removed.

### Desktop adapter integration

The desktop packages depend on `Scry.Sdk`, but the dependency never points in the opposite direction. A non-UI target can use the core endpoint without loading PresentationFramework, WindowsBase, or System.Windows.Forms. UI targets opt in during host construction:

```csharp
builder.UseWpf(
    Application.Current,
    adapter => adapter.RegisterWindow("main", Application.Current.MainWindow));

builder.UseWinForms(
    mainForm,
    adapter => adapter.RegisterRoot("main", mainForm));
```

`UseWpf` registers a `wpf` service root plus `wpf.snapshot`, `wpf.wait`, `wpf.assert`, and `wpf.screenshot`. Calls marshal through the selected `Dispatcher`. Snapshot payloads accept `tree` (`visual` or `logical`) and optional `root`; waits/assertions add `path`, `name`, `automationId`, `state`, `expected`, and optional timeout/poll intervals.

`UseWinForms` registers a `winforms` service root plus `winforms.snapshot`, `winforms.wait`, `winforms.assert`, and `winforms.screenshot`. Calls marshal through the selected owner control with `BeginInvoke`; construct the adapter after that control has created its handle. Snapshot payloads accept an optional `root`; waits/assertions add `path`, `name`, `state`, `expected`, and optional timeout/poll intervals.

Both adapters also expose typed `WpfAdapter`/`WinFormsAdapter` services and condition/result models for reusable in-process test code. Maximum depth/node counts, wait defaults, and screenshot dimensions and encoded byte sizes are configurable. A negative assertion is inconclusive when its bounded projection is truncated.

WPF visual and logical snapshots are intentionally distinct: the visual view can omit logical-only values and unopened template or popup content, while the logical view can omit template-generated visuals. WinForms projects managed `Control` children, `Application.OpenForms`, owned-form metadata, `ToolStrip`/menu items, and bindings. Owner-drawn pixels, WebView2, ActiveX, `HwndHost`, native-child HWND internals, separate popup windows, protected content, and out-of-process surfaces can be absent. Screenshots use `RenderTargetBitmap` or `Control.DrawToBitmap` and return or throw explicit unsupported/failure results rather than claiming those surfaces were captured.

## Operations

All non-handshake requests use the negotiated session. Subjects are selected with either `"root":"name"` or `"reference":{...}`. `inspect`, `get`, `set`, and `invoke` accept `"includeNonPublic":true` as an explicit opt-in.

| Operation | Important payload fields |
|---|---|
| `capabilities` | none |
| `roots` | none |
| `inspect` | root/reference, includeNonPublic |
| `get` | root/reference, member, includeNonPublic, asReference |
| `set` | root/reference, member, value, includeNonPublic, asReference |
| `invoke` | root/reference + member + arguments, or registeredOperation + arguments; asReference |
| `enumerate` | root/reference, offset, limit (1-1000), asReferences |
| `release` | handleId or handleIds |
| `evaluate` | source, imports, references, timeoutMilliseconds |
| `execute` | source, imports, references, timeoutMilliseconds |
| `load-assembly` | absolute path, loadPolicy (`default` or `isolated`) |
| `list-assemblies` | none |
| `find-types` | query, assembly, loadContext, namespace, includeNonPublic, limit |
| `describe-type` | type, assembly, loadContext, includeNonPublic |
| `job.start` | operation, payload, correlationId |
| `job.status` | job |
| `job.wait` | job, timeoutMilliseconds (0-300000) |
| `job.cancel` | job |
| `job.logs` | job, cursor, limit (1-1000) |

Values are returned as `RemoteValue`. Existing scalar types remain inline as `kind: "scalar"`, reference types receive an `ExternalReference`, and other value types are returned as `kind: "value"` with the negotiated `bounded-value-projection` capability. Struct projections recurse through value types to four levels and 64 total members, represent nested strings longer than 1,024 characters with a truncated `$value` marker, report inaccessible or throwing members with `$error`, and stop at reference-type members with a type marker. They never consume leased handles, so repeated reads of an unchanged struct have value semantics rather than artificial boxed identity.

Projections are bounded snapshots, not live subjects. A struct root remains directly inspectable by its registered root name. For a struct returned by `get`, `set`, or `invoke`, set `asReference: true` to deliberately lease that box for subsequent inspection or invocation; `enumerate` similarly accepts `asReferences: true` for value-type items. Explicit boxes consume handles and should be released. Projections containing `$reference`, `$truncated`, or `$error` markers are rejected as invocation/set arguments rather than silently fabricating omitted state. Enumeration also applies an aggregate response budget below the maximum frame size and sets `hasMore` when that budget ends a page early. Passing an `ExternalReference` as an argument preserves reference identity.

The CLI reads request objects with `--request <file|->` (or the compatible `--input` spelling). `evaluate` and `execute` accept C# through `--source <file|->` or redirected stdin. Rich execution settings belong in a request JSON file. Inline `--json` remains available for non-execution compatibility, but execution source is deliberately not forced onto command lines. Exit codes are stable: `0` success, `2` usage/JSON error, `3` target resolution error, `4` connection/authentication/protocol error, `5` target operation error, and `70` unexpected CLI failure. Tokens are never accepted as command-line options.

Fresh CLI connections negotiate ephemeral sessions that are removed on disconnect, so stateless command use does not retain target resources. Passing `--session` resumes a persistent session instead; callers own its handles until release or lease expiry.

## C# execution

`evaluate` compiles a C# script and returns its value. `execute` wraps source as an async statement body, so it can use statements, `await`, and an explicit `return`; falling through returns `null`. Both project results through the same scalar/value/reference `RemoteValue` model as reflection operations.

Execution exposes one global named `Context`:

```csharp
Context.SessionId
Context.Roots
Context.GetRoot("services")
Context.Resolve(reference)
Context.CancellationToken
Context.Log("message", "information")
```

Registered root factories are evaluated once at the start of each execution. `Resolve` enforces the current target/session handle scope. Logs are bounded by entry count and message length and report dropped entries. Compilation failures use the normal failure envelope with code `compilation_failed` and structured diagnostics containing ID, severity, message, and one-based source spans. Exceptions thrown by compiled code use the ordinary recursive exception envelope.

Roslyn metadata references come only from compatible, file-backed managed assemblies already loaded in the target's default load context or default AppDomain. Dynamic, native, and unreadable modules are skipped. On .NET 9, non-default-context modules are also skipped because Roslyn cannot safely bind script code to an existing isolated-context assembly instance. Optional `references` entries validate that named compatible target assemblies are loaded; they do not load files. Use `load-assembly` with the `default` policy first when code must name its types.

Timeouts and cancellation are cooperative. The configured server deadline cancels `Context.CancellationToken` and Roslyn async execution; target shutdown also cancels it. Code that awaits with the token observes `execution_timed_out`. Cancelling `ScryClient.RequestAsync` cancels local pipe I/O and faults that client connection, but protocol version 1 has no request-cancellation frame, so it does not claim to cancel work already executing in the target. Synchronous code that never observes server cancellation cannot be forcibly stopped safely inside the target process and can continue blocking that connection. Scry does not claim process isolation or hard timeouts.

Host defaults are configurable through `AgentHostOptions`: source length, default/maximum execution milliseconds, imports/references, bounded logs, type result/member limits, and assembly file size. The protocol frame limit remains an independent upper bound.

## Assembly loading and type discovery

`load-assembly` requires an absolute path. Loading differs by runtime:

- On .NET 9, `default` calls `AssemblyLoadContext.Default.LoadFromAssemblyPath`. `isolated` creates a named collectible `AssemblyLoadContext` with `AssemblyDependencyResolver`. Scry retains isolated contexts for the host lifetime; there is no unload operation in this release. Isolated assemblies are available to list/find/describe operations but are intentionally excluded from Roslyn references.
- On .NET Framework 4.7.2, only `AppDomain.CurrentDomain` is supported. `default` uses `Assembly.LoadFrom` in that AppDomain, and descriptions report `DefaultAppDomain`. `isolated` fails with `load_policy_not_supported`: a child AppDomain cannot preserve Scry's in-process roots, handles, reflection objects, and Roslyn type identity.

Loading is explicit: evaluation never loads assemblies by path or probes arbitrary directories. `list-assemblies` reports identity, location, dynamic status, load context, default-context status, and collectibility. `find-types` performs bounded filtering over loaded types and reports each type's load context. `describe-type` returns bounded member metadata; `assembly` and `loadContext` selectors disambiguate duplicate full type names across assemblies or contexts.

## Jobs

Jobs execute an ordinary non-job protocol operation in the target process. `job.start` returns immediately with a `JobSnapshot` and a `JobHandle` containing `targetId`, `sessionId`, and `jobId`. The endpoint owns execution, cancellation, result/error state, and logs; the CLI stores no state. `scry jobs status|wait|cancel|logs` infers the session from the supplied handle, so a later CLI process can resume it.

States are `queued`, `running`, `succeeded`, `failed`, and `canceled`. A wait timeout returns `{ "job": <current snapshot>, "timedOut": true }`; timeout is never represented as a job state. Cancellation is cooperative. Completed entries expire after `JobRetention`, and admission remains bounded by `MaximumJobs`. Logs are bounded per job and use monotonically increasing cursors. If requested entries have already rolled off, `truncated` is true and `oldestCursor` identifies the first retained entry.

```powershell
scry jobs start --target my-test-target --correlation build-42 --json `
  '{"operation":"invoke","payload":{"registeredOperation":"reindex","arguments":{}}}'

scry jobs wait --target my-test-target --json `
  '{"job":{"targetId":"...","sessionId":"...","jobId":"..."},"timeoutMilliseconds":30000}'
```

## Multi-target scenarios

`scry scenario` (also `scry batch`) accepts one object from `--input`, `--json`, or redirected stdin. Commands run in input order for `sequential` mode or in parallel for `concurrent` mode. Concurrent results are still emitted in input order. Each command explicitly selects exactly one target ID/alias or descriptor path and receives its own structured response envelope.

```json
{
  "mode": "concurrent",
  "commands": [
    {
      "id": "worker-a",
      "target": "worker-a",
      "operation": "capabilities",
      "payload": {},
      "correlationId": "deployment-17"
    },
    {
      "id": "worker-b",
      "descriptor": "C:\\path\\to\\target.json",
      "operation": "invoke",
      "payload": {
        "registeredOperation": "reset",
        "arguments": {}
      }
    }
  ]
}
```

Scenario output is a `ScenarioResult` containing `protocolVersion`, normalized `mode`, aggregate `success`, and ordered `results`. Every item preserves its command ID, index, operation, selector, resolved target metadata when available, and either the target's `ProtocolResponse` or a CLI-side `ProtocolError`. A scenario exits `0` only when every command succeeds and `6` when any command fails; individual commands retain the existing exit codes.

Generated Roslyn script assemblies and assemblies loaded into the .NET Framework default AppDomain cannot be unloaded independently. They remain until the host process exits. Scry does not create, marshal across, or unload child AppDomains in the net472 implementation.

## Validation

Run the modern and desktop CLR suites explicitly:

```powershell
dotnet build Scry.sln -c Release
dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net9.0 --no-build
dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net472 --artifacts-path artifacts\net472-x64 -p:PlatformTarget=x64 -- RunConfiguration.TargetPlatform=x64
dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net472 --artifacts-path artifacts\net472-x86 -p:PlatformTarget=x86 -- RunConfiguration.TargetPlatform=x86
dotnet test tests\Scry.Wpf.Tests\Scry.Wpf.Tests.csproj -c Release -f net472
dotnet test tests\Scry.WinForms.Tests\Scry.WinForms.Tests.csproj -c Release -f net472
dotnet format Scry.sln --verify-no-changes --no-restore
```

The net472 suite executes an embedded endpoint on the installed desktop CLR and covers framing (including partial and truncated reads), discovery, current-user pipe ACLs, capability authentication, sessions and handles, reflection, exception projection, limits, Roslyn evaluate/execute, assembly discovery/loading, and the unsupported isolated-policy response. The architecture-specific runs assert that the test host is actually x64 or x86.

## Extensibility boundaries

- Add protocol operations and capability names without changing framing or cross-runtime JSON shapes.
- Add runtime adapters (WPF/WinForms) as registered roots/operations rather than coupling UI assemblies into the core.
- Add Roslyn execution as an opt-in capability without coupling compiler services into the job/runtime layer.
- Keep attach/injection responsible only for loading and bootstrapping the same runtime endpoint.
- A future Skill should drive the stable CLI JSON surface rather than acquire in-process state.
