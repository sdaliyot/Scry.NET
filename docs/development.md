# Foundation development guide

See [`threat-model.md`](threat-model.md) for the security reasoning behind what follows - assets,
the trust boundary, and what is deliberately not defended against versus what simply is not built.

## Architecture

| Project | Responsibility |
|---|---|
| `Scry.Contracts` | Wire contracts, framing, target descriptors, discovery |
| `Scry.Runtime` | Named-pipe server, sessions, handles, reflection, Roslyn execution, assembly catalog, process-scoped jobs |
| `Scry.Endpoint` | In-process `EndpointHost` and registration builder, started by the target or by an injected payload |
| `Scry.Client` | `ScryClient` - connects to an endpoint over the named pipe. References only `Scry.Contracts`, so a client never carries the engine |
| `Scry.Wpf` | Optional dispatcher-safe WPF projections, waits/assertions, screenshots |
| `Scry.WinForms` | Optional control-owner-marshalled WinForms projections, waits/assertions, screenshots |
| `Scry.Injector.Payload` | Minimal managed entry point that retains the shared `EndpointHost` |
| `Scry.Injector` | Process detection, refusal policy, native loading, and attach orchestration |
| `Scry.Injector.Native` | Architecture-specific Win32/CLR bootstrap DLL |
| `Scry.Cli` | Stateless `scry` JSON command line |
| `Scry.SampleHost` | Console embedded example |
| `Scry.SampleWorker` | Long-running background/worker embedded example |
| `Scry.SampleWpf` | Minimal runnable WPF embedded example |
| `Scry.SampleWinForms` | Minimal runnable WinForms embedded example |
| `Scry.AttachTarget` | Non-UI process that deliberately does not reference Scry |
| `Scry.Tests` | Protocol, runtime, discovery, and TCP transport tests (`TcpTransportTests.cs` - the loopback listener, address override, and cross-transport session resumption) |
| `Scry.Wpf.Tests` | STA dispatcher tests for the optional WPF adapter |
| `Scry.WinForms.Tests` | STA message-loop tests for the optional WinForms adapter |

Target names and compatibility package versions are centralized in `Directory.Build.props`.

| Project | Target frameworks | Notes |
|---|---|---|
| `Scry.Contracts` | `net9.0`, `net472` | Identical protocol and descriptor shape |
| `Scry.Runtime` | `net9.0`, `net472` | CoreCLR load contexts or desktop CLR default-AppDomain behavior |
| `Scry.Endpoint` | `net9.0`, `net472` | In-process endpoint host |
| `Scry.Client` | `net9.0`, `net472` | Client; no Roslyn, no runtime |
| `Scry.SampleHost` | `net9.0`, `net472` | Non-UI embedded sample |
| `Scry.Tests` | `net9.0`, `net472` | Runtime integration suite; CLI tests run on `net9.0` |
| `Scry.Wpf` | `net9.0-windows`, `net472` | Optional WPF adapter; desktop CLR uses direct assembly references |
| `Scry.WinForms` | `net9.0-windows`, `net472` | Optional Windows Forms adapter; desktop CLR uses direct assembly references |
| `Scry.Wpf.Tests` | `net9.0-windows`, `net472` | STA dispatcher tests, both runtimes |
| `Scry.WinForms.Tests` | `net9.0-windows`, `net472` | STA message-loop tests, both runtimes |
| `Scry.Cli` | `net9.0` | Modern-only executable that interoperates with both host targets |
| `Scry.Injector.Payload` | `net9.0`, `net472` | Calls the same `EndpointHost.Start` used by embedded mode |
| `Scry.Injector` | `net9.0` | Must run with the same x86/x64 architecture as the target |
| `Scry.Injector.Native` | Win32 x86, x64 | Native DLL loaded into the target |

The `Microsoft.NETFramework.ReferenceAssemblies.net472` package makes SDK-style net472 builds independent of machine-installed targeting packs. Runtime validation still requires Windows with .NET Framework 4.7.2 installed.

## Protocol and security

Frames are a 4-byte little-endian length followed by UTF-8 JSON. Protocol version 1 requires `handshake` first. The handshake authenticates a 256-bit random capability token, negotiates the version, creates or resumes a target-qualified session, and returns capabilities. Subsequent requests use structured success/error envelopes. Every handled request receives a target-generated `operationId`; a supplied `correlationId` is echoed, or defaults to that operation ID. Ordinary operation exceptions cross the boundary with type, message, stack, HResult, source, and recursively captured inner exceptions. Fatal runtime failures such as process termination, stack overflow, corrupted state, or fail-fast can bypass this boundary.

Discovery descriptors live under `%LOCALAPPDATA%\Scry\targets` - or under `RuntimeHostOptions.TargetsDirectory` / `EndpointOptions.TargetsDirectory` / `scry attach --targets-dir`, when overridden - and are removed on host disposal and normal process exit. Any number of embedded hosts may publish simultaneously, including multiple processes with the same alias. Resolution accepts a target ID, canonical alias, or additional alias; an ambiguous alias is rejected and callers must select a target ID. On .NET 9 the named pipe uses `PipeOptions.CurrentUserOnly`. On .NET Framework 4.7.2 the server creates a protected, non-inheriting DACL with an allow rule only for the current Windows user SID; it does not fall back to a broadly accessible pipe. Descriptors and tokens must never be copied to logs, command-line arguments, telemetry, or remote systems. The CLI accepts a descriptor **path** or target identity/alias and reads the token locally.

An endpoint can additionally start a loopback-only TCP listener (`RuntimeHostOptions.TcpPort` / `EndpointOptions.TcpPort` / `scry attach --tcp-port`), off by default, so a caller can reach it through a port forward set up outside Scry.NET (see `README.md`, "Reaching an endpoint on another machine"). `ScryClient.ConnectOverTcpAsync` never runs implicitly - a descriptor advertising a TCP listener still connects over the pipe unless a caller opts in explicitly. The pipe's per-user DACL has no TCP equivalent: enabling the listener means any local process, as any Windows user, can attempt a handshake, with the capability token as the only remaining gate. See `docs/threat-model.md` for the full trust-boundary discussion this requires.

Sessions belong to one target. Object references contain target, session, and handle IDs, preventing accidental cross-target/session use. Handles are strong references with sliding leases, stable identity within a session, explicit release, and cleanup on expiry/session disposal. Previews are bounded and are not object serialization.

## Embedded API

Start the endpoint once and keep the returned host alive:

```csharp
using var host = EndpointHost.Start(
    builder => builder
        .RegisterValue("services", serviceProvider, "Application service provider.")
        .RegisterRoot("current", () => currentState, "Current test state.")
        .RegisterOperation(
            "reset",
            (_, _) =>
            {
                currentState.Reset();
                return ValueTask.FromResult<object?>(null);
            },
            "Resets current state to its test baseline.",
            new OperationPolicy { RequiresConfirmation = true }),
    new EndpointOptions
    {
        Alias = "my-test-target",
        Aliases = ["checkout-a", "worker"]
    });
```

`RegisterValue` retains a specific object, while `RegisterRoot` evaluates its factory for each request. Registered operations receive structured JSON rather than source text. `RegisterJobOperation` additionally receives an `OperationExecutionContext` with the operation ID, correlation ID, cooperative `CancellationToken`, and bounded job logger. Session, handle, and job limits; lease and retention durations; aliases; preview length; and log bounds are configurable. A retained job keeps its qualified session addressable until the job is removed.

Operation registrations accept an optional `OperationPolicy`:

- `ExecutionPolicy` defaults to `WorkerThread`. Set it to `UiOwner` only when the handler or an adapter marshals all UI-owned access itself.
- `IsReadOnly` tells an agent that the helper is intended only to observe state. It is guidance, not a process security boundary.
- `RequiresConfirmation` tells an agent to obtain explicit approval before invocation.

Descriptions and all three policy fields are emitted under `registeredOperations` by `capabilities`. The response also includes agent guidance: discover first, prefer read-only helpers, honor confirmation requirements, and keep worker-thread code away from UI-owned state. The `roots` response includes each root name, description, and projected value. Names should be stable and domain-specific; descriptions should explain intent, side effects, expected arguments, and important bounds clearly enough for an AI agent to select the safe operation without reading application source.

### Registration lifetime

The builder's registrations remain available as `host.Registrations` for runtime changes. Registration snapshots and lookups are synchronized, so discovery and invocation see either the old registration or the complete replacement:

```csharp
host.Registrations.RegisterRoot(
    "current",
    () => replacementState,
    "Current replacement state.",
    RegistrationMode.ReplaceExisting);

var removed = host.Registrations.UnregisterOperation("reset");
```

The default `RejectDuplicate` mode preserves startup typo detection. `ReplaceExisting` can replace a root factory/value or switch an operation between normal and job-aware handlers. `UnregisterRoot` and `UnregisterOperation` return `true` only when an entry existed. Replacement and removal govern future name resolution. They do not invalidate handles already leased from an earlier root, and they do not cancel an invocation or job that already captured a handler. Normal handle leases, explicit `release`, job cancellation, and host disposal remain the lifecycle controls for that work.

### Process-specific adoption

| Process | Startup and threading guidance | Runnable sample |
|---|---|---|
| Console | Start one host near process startup; dispose it before exit. Worker handlers must cooperate with cancellation. | `samples\Scry.SampleHost` |
| Worker/service | Keep the host alive for the service lifetime. Expose domain state rather than infrastructure internals, and make long work a `RegisterJobOperation`. | `samples\Scry.SampleWorker` |
| WPF | Start on the application dispatcher and call `UseWpf`. Custom UI operations must marshal through `Dispatcher`; label them `UiOwner`. | `samples\Scry.SampleWpf` |
| WinForms | Create the owner handle on its UI thread before `UseWinForms`. Custom UI operations must use the owner control for marshalling and be labeled `UiOwner`. | `samples\Scry.SampleWinForms` |

All four samples register a root factory, a retained value, a domain operation, and a cancellable job operation. A realistic agent flow is:

1. Run `capabilities` and inspect descriptions, safety flags, and execution policy.
2. Run `roots` and choose the described domain root rather than guessing object names.
3. Invoke the domain mutation only after honoring `requiresConfirmation`.
4. Verify through `get`/`inspect`; desktop agents should additionally use `wpf.assert` or `winforms.assert`.
5. Start longer work with `jobs start`, then use the returned qualified handle with `jobs wait`, `jobs logs`, or `jobs cancel`.

### Desktop adapter integration

The desktop packages depend on `Scry.Endpoint`, but the dependency never points in the opposite direction. A non-UI target can use the core endpoint without loading PresentationFramework, WindowsBase, or System.Windows.Forms. UI targets opt in during host construction:

```csharp
builder.UseWpf(
    Application.Current,
    adapter => adapter.RegisterWindow("main", Application.Current.MainWindow));

builder.UseWinForms(
    mainForm,
    adapter => adapter.RegisterRoot("main", mainForm));
```

`UseWpf` registers a `wpf` service root plus `wpf.snapshot`, `wpf.wait`, `wpf.assert`, and `wpf.screenshot`. These helpers are described as read-only with `ui-owner` execution policy. Calls marshal through the selected `Dispatcher`. Snapshot payloads accept `tree` (`visual` or `logical`) and optional `root`; waits/assertions add `path`, `name`, `automationId`, `state`, `expected`, and optional timeout/poll intervals.

`UseWinForms` registers a `winforms` service root plus `winforms.snapshot`, `winforms.wait`, `winforms.assert`, and `winforms.screenshot`. These helpers are described as read-only with `ui-owner` execution policy. Calls marshal through the selected owner control with `BeginInvoke`; construct the adapter after that control has created its handle. Snapshot payloads accept an optional `root`; waits/assertions add `path`, `name`, `state`, `expected`, and optional timeout/poll intervals.

Both adapters also expose typed `WpfAdapter`/`WinFormsAdapter` services and condition/result models for reusable in-process test code. Maximum depth/node counts, wait defaults, and screenshot dimensions and encoded byte sizes are configurable. A negative assertion is inconclusive when its bounded projection is truncated.

### UI-thread execution marshalling

`evaluate` and `execute` run on the endpoint thread serving the request. WPF and WinForms objects are thread-affine, so a submission that touches one throws `InvalidOperationException` from `Dispatcher.VerifyAccess` or the `Control.InvokeRequired` check. Setting `Marshal` to `"ui"` on the request (`ExecutionMarshalTargets.UiThread`) runs the submission on the UI thread instead:

```json
{ "source": "System.Windows.Application.Current.MainWindow.Title", "marshal": "ui" }
```

Without the flag, the same read has to marshal itself, which is the code the flag replaces:

```csharp
// equivalent to "marshal": "ui", written by hand inside an unmarshalled submission
System.Windows.Application.Current.Dispatcher.Invoke(
    new System.Func<string>(() => System.Windows.Application.Current.MainWindow.Title))
```

That hand-written form marshals one expression. The flag marshals the whole submission, so every statement — and every continuation after an `await` — runs on the UI thread, and no part of a multi-statement `execute` runs off it.

`Scry.Runtime` owns no UI framework reference, so it does not resolve a dispatcher itself. The host supplies one as an `ExecutionMarshaller`:

```csharp
public delegate Task<object?> ExecutionMarshaller(
    Func<Task<object?>> callback,
    CancellationToken cancellationToken);
```

`UseWpf` and `UseWinForms` register an implementation over the dispatcher they already hold, so enabling an adapter is all that is required:

```csharp
// what UseWpf registers for you
builder.UseExecutionMarshaller(async (callback, cancellationToken) =>
    await await adapter.Marshaller.InvokeAsync(callback, cancellationToken).ConfigureAwait(false));
```

The double `await` is deliberate: `InvokeAsync` returns a task whose result is the submission's own task. `ExecutionEngine` does not apply `ConfigureAwait(false)` inside the marshalled callback, so the dispatcher's `SynchronizationContext` is captured and awaited continuations resume on the UI thread. Unmarshalled there is no context to capture, so that path is unchanged.

A host with its own single-threaded context can call `UseExecutionMarshaller` directly without an adapter; only one marshaller may be registered per host. A capabilities response advertises `ui-thread-marshalling` when one is present. `marshal` is rejected up front — `marshal_target_unavailable` when no marshaller is registered, `marshal_target_not_supported` for an unrecognised target — rather than being allowed to fail later as an opaque cross-thread exception.

Two operational limits apply. The submission holds the UI thread for its duration, so the target's UI is unresponsive until it completes, and `TimeoutMilliseconds` observes cooperative cancellation only: it cannot interrupt work already executing on that thread. Prefer short, targeted marshalled submissions, and use the bounded `wpf.*`/`winforms.*` operations for anything that polls or waits.

WPF visual and logical snapshots are intentionally distinct: the visual view can omit logical-only values and unopened template or popup content, while the logical view can omit template-generated visuals. WinForms projects managed `Control` children, `Application.OpenForms`, owned-form metadata, `ToolStrip`/menu items, and bindings. Owner-drawn pixels, WebView2, ActiveX, `HwndHost`, native-child HWND internals, separate popup windows, protected content, and out-of-process surfaces can be absent. Screenshots use `RenderTargetBitmap` or `Control.DrawToBitmap` and return or throw explicit unsupported/failure results rather than claiming those surfaces were captured.

## Attach mode

`scry attach <pid|process-name> [--alias <name>]` attaches to an already-running managed process that has not referenced Scry. Process names must resolve to exactly one process; otherwise the command requires a PID. The command returns structured JSON, waits for discovery, and performs the same capability-token handshake as every other CLI connection.

Attach uses a strict inspect-before-write sequence:

1. Open the target for limited query access and call `IsWow64Process2` to classify x86/x64.
2. Enumerate loaded modules and require exactly one supported runtime family: `clr.dll` for .NET Framework or `coreclr.dll` for modern .NET.
3. Refuse architecture mismatch, ARM64, no CLR, mixed CLR families, access denial, a missing payload/helper, or an existing live Scry descriptor.
4. Allocate a DLL path in the target, start `LoadLibraryW`, locate the injected module, and call its exported bootstrap on a second remote thread.
5. For .NET Framework, the shim obtains the already-loaded CLR v4 through `ICLRMetaHost`/`ICLRRuntimeInfo`, verifies it is loaded, and calls `ICLRRuntimeHost::ExecuteInDefaultAppDomain`.
6. For .NET 9, the shim obtains a hostfxr runtime delegate compatible with the already-running CoreCLR and calls the payload's `UnmanagedCallersOnly` entry point. It does not call `coreclr_initialize` and does not create a second runtime.
7. `Scry.Injector.Payload` calls and retains `EndpointHost.Start`; runtime, discovery, transport, authentication, capabilities, and behavior therefore remain identical to embedded mode.

Native work is deliberately kept out of `DllMain`; `DllMain` only records the module handle and exported bootstrap work runs on the injector-created thread. The injector bounds all copied strings, waits with finite timeouts, releases remote allocations and handles, and maps Win32/bootstrap failures to stable codes including `permission_denied`, `loader_failed`, `security_software_interference`, and `bootstrap_failed`.

Build both helpers before the managed Release build:

```powershell
.\build-native.ps1
dotnet build Scry.sln -c Release
```

Two separate commands because `Scry.Injector.Native` is a vcxproj and is deliberately not a member
of `Scry.sln`. A vcxproj imports the C++ targets through `$(VCTargetsPath)`, which the .NET CLI does
not ship, so putting it in the solution would break `dotnet build`, `dotnet test` and `dotnet format`
over that solution at evaluation time. It is built with full MSBuild by `build-native.ps1`, and the
managed build consumes its output by path rather than through a project reference.

The managed build stages the x86/x64 helpers and the complete `payload\netfx` and `payload\net`
directories beside `scry-injector`. Those directories are named by CLR family rather than by target
framework on purpose: the consumer picks one from the target's detected runtime, so retargeting the
.NET Framework leg needs no code change. Each also carries the optional `Scry.Wpf` and
`Scry.WinForms` adapters, which the payload loads only when `--adapters` asks for one. Publish or
run the injector for the target architecture; an x64 process cannot safely inject an x86 target and
vice versa.

The default build produces an x64 injector. For a 32-bit target, publish the matching one - the
payload and adapters are architecture-neutral and are staged into the publish directory alongside
both native helpers:

```powershell
dotnet publish srcScry.Injector -c Release -f net9.0 -r win-x86 --self-contained false -o <dir>
```

It is framework-dependent, so the x86 .NET runtime must be installed. Attaching with a mismatched
injector fails with `architecture_mismatch` before anything is written into the target.

Build the native helper before the managed build, and use `-p:RequireNativeInjector=true` for
Release and CI so a forgotten native build fails the build instead of silently producing a CLI that
cannot attach:

```powershell
.\build-native.ps1 -Architecture x64
dotnet build Scry.sln -c Release -p:RequireNativeInjector=true
```

Before injecting into a .NET Framework target, the injector reads the target's `.exe.config` and
refuses with `binding_conflict` if a `bindingRedirect` would downgrade one of the payload's own
dependencies. This matters because the .NET Framework path loads the payload into the target's
default AppDomain, under the target's binding policy, and the payload's `AssemblyResolve` hook
cannot recover: a redirect is applied *before* that event fires, and the event only runs when a bind
fails, not when it succeeds against the wrong version. The check is driven off the staged payload's
real assembly versions rather than a fixed list, and is deliberately permissive about anything it
cannot parse.

### Desktop adapters in an attached target

An embedded host calls `UseWpf`/`UseWinForms` itself. An attached target by definition cannot, so
without `--adapters` an injected endpoint has only the framework-neutral surface: no `wpf.*` or
`winforms.*` operations, and no execution marshaller, which means `evaluate`/`execute` cannot use
`"marshal": "ui"` and therefore cannot touch a `DependencyObject` or a `Control` at all. That is the
correct default for a non-UI target and useless for a desktop one, so the selection is explicit:

```powershell
scry attach <pid|process-name> --adapters wpf
```

`DesktopAdapterWiring` then loads the staged adapter reflectively and calls
`UseWpf(Application.Current)` (or `UseWinForms` with the first open form). Two consequences worth
knowing. The adapter needs no cooperation from the target: `UseWpf(Application)` enumerates
`Application.Windows` when no roots are registered, and reuses the dispatcher the target already
has rather than creating one. And the load is reflective rather than a project reference, both
because the payload targets `net9.0` while the modern adapters target `net9.0-windows`, and because
a hard reference would make every attach - including into a non-UI process - depend on the
WindowsDesktop shared framework being present in the target.

`--adapters wpf` against a process with no WPF loaded is refused with an explanatory error, as is a
WPF target whose `Application.Current` is null.

Attach mode is local, invasive tooling for development and testing. It requires the same Windows user and an equal or higher integrity level. Protected processes and process-mitigation policies can prohibit remote allocation, writes, thread creation, or DLL loading. Antivirus/EDR products commonly block or quarantine these exact primitives. Do not weaken security controls globally; authorize the binary or test in an isolated environment. Never attach to software you do not own or have explicit permission to test.

Current limits:

- The endpoint starts only in the default AppDomain/default CoreCLR load context.
- Supported targets are .NET Framework 4.7.2 (or later 4.x) and .NET 9 on Windows x86/x64, both verified end to end on both CLR families.
- Secondary AppDomains, ARM64, cross-architecture injection, remote machines, unload/detach, and production packaging are not implemented.
- Runtime detection requires the managed runtime to be loaded before attach.
- Native dependency resolution and host policy can still be constrained by target-specific mitigations or hosting models; failures are reported rather than falling back to an unsafe runtime start.

## Operations

All non-handshake requests use the negotiated session. Subjects are selected with either `"root":"name"` or `"reference":{...}`. `inspect`, `get`, `set`, and `invoke` accept `"includeNonPublic":true` as an explicit opt-in.

`inspect`, `get`, `set`, `invoke` and `enumerate` also accept `"marshal":"ui"`, because they read or
mutate live objects and so are subject to UI thread affinity. It is applied centrally in
`OperationDispatcher.DispatchAsync` rather than threaded through each operation, and resolved by the
same `MarshalTarget` helper `evaluate`/`execute` use, so every operation accepts identical targets
and produces identical errors. The metadata operations (`load-assembly`, `list-assemblies`,
`find-types`, `describe-type`) have no thread affinity and do not take it.

### Framework-neutral conditions

`wait` and `assert` take a `ConditionRequest`: a C# expression plus an operator from
`ConditionOperators` (`isTrue`, `equals`, `notEquals`, `contains`, `isNull`, `isNotNull`). They are
deliberately expression-based rather than member-path based, because that is the only shape that
works identically in a non-UI target - which has no UI tree to query - while still reaching
view-model state in a desktop target, which the adapter conditions cannot do because they only
observe the bounded UI-tree projection.

Both reuse `evaluate` wholesale, including its `marshal` handling. `wait` therefore marshals each
individual evaluation rather than the polling loop, so a long marshalled wait never occupies the UI
thread between attempts; it is excluded from the central marshalling above for exactly that reason.

Comparison runs against the raw CLR value, not its JSON projection, so a bounded preview can never
change the verdict. `expected` must be a string, number, boolean or null; a structured operand is
refused with `invalid_request` rather than silently compared against a preview string.

`wait` reports a timeout as a successful `ConditionResult` with `satisfied: false`, matching the
`wpf.wait`/`winforms.wait` convention. `assert` raises `assertion_failed` so a failed check is a
failed request with a non-zero exit code.

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
| `evaluate` | source, imports, references, timeoutMilliseconds, marshal |
| `execute` | source, imports, references, timeoutMilliseconds, marshal |
| `wait` | source, operator, expected, timeoutMilliseconds, pollIntervalMilliseconds, imports, references, marshal |
| `assert` | source, operator, expected, imports, references, marshal |
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

The CLI reads request objects with `--request <file|->` (or the compatible `--input` spelling). `evaluate` and `execute` accept C# through `--source <file|->` or redirected stdin. Rich execution settings belong in a request JSON file. Inline `--json` remains available for non-execution compatibility, but execution source is deliberately not forced onto command lines. Exit codes are stable: `0` success, `2` usage/JSON error, `3` target resolution error, `4` connection/authentication/protocol error, `5` target operation error, `6` scenario partial failure, and `70` unexpected CLI failure. Tokens are never accepted as command-line options.

Fresh CLI connections negotiate ephemeral sessions that are removed on disconnect, so stateless command use does not retain target resources. Passing `--session` resumes a persistent session instead; callers own its handles until release or lease expiry.

### CLI and agent contract

The CLI is the sole agent interface in this release. `scry --help` provides the complete
top-level surface, and every command supports `scry help <command>` or a trailing
`--help`. `scry schema` (also available as `scry --json-schema`) emits a deterministic
JSON catalog generated from the same command definitions used by CLI validation. It
documents selectors, input modes, request fields, response envelopes, result shapes, and
all exit codes, including `6` for a scenario or batch partial failure.

Every target operation accepts exactly one of `--target <id-or-alias>` and
`--descriptor <path>`, plus optional `--session` and `--correlation`. Successful and
failed target responses preserve the protocol `operationId` and `correlationId`; failures
that occur locally before target acceptance cannot have an operation ID. Discovery,
schema, help, and local validation errors are CLI-local shapes rather than protocol
responses.

The direct `wpf.snapshot`, `wpf.wait`, `wpf.assert`, `wpf.screenshot`,
`winforms.snapshot`, `winforms.wait`, `winforms.assert`, and
`winforms.screenshot` commands are ergonomic translations to `invoke` with the matching
registered operation. Scenario commands and `job.start` payloads apply the same
translation when their operation is one of these adapter names. Adapter registrations
return a scalar `JsonElement`, allowing the direct CLI command to place the structured
adapter result directly in the protocol response before its ephemeral session closes.

The agent-facing workflow and realistic request/response examples live in
[`skills/scry/SKILL.md`](../skills/scry/SKILL.md). Keep that Skill and `scry schema`
updated whenever the CLI contract changes.

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

Registered root factories are evaluated once at the start of each execution. `Resolve` enforces the current target/session handle scope. Logs are bounded by entry count and message length and report dropped entries. Compilation failures use the normal failure envelope with code `compilation_failed` and structured diagnostics containing ID, severity, message, and one-based source spans. Exceptions thrown by compiled code use the ordinary recursive exception envelope: `InnerException` is followed to a depth of `ExceptionDetail.MaximumDepth` (8), past which a node reports `Truncated: true` rather than continuing, and message/stack-trace text is capped independently, so an unusually deep or verbose exception is reported in bounded form rather than risking the JSON depth limit or frame size cap and failing to serialize at all. An `AggregateException` reports every one of its faults (also capped at 8, with the count of any dropped) in `InnerExceptions`, and `InnerException` still holds the first fault for a caller that only looks there.

Roslyn metadata references come only from compatible, file-backed managed assemblies already loaded in the target's default load context, the injected agent's host context, or the .NET Framework default AppDomain. Dynamic, native, and unreadable modules are skipped. On .NET 9, unrelated non-default-context modules are skipped because Roslyn cannot safely bind script code to an existing isolated-context assembly instance. Optional `references` entries validate that named compatible target assemblies are loaded; they do not load files. Use `load-assembly` with the `default` policy first when code must name its types.

Timeouts and cancellation are cooperative. The configured server deadline cancels `Context.CancellationToken` and Roslyn async execution; target shutdown also cancels it. Code that awaits with the token observes `execution_timed_out`. Cancelling `ScryClient.RequestAsync` cancels local pipe I/O and faults that client connection, but protocol version 1 has no request-cancellation frame, so it does not claim to cancel work already executing in the target. Synchronous code - or a marshalled submission that never observes cancellation while holding the host's UI thread - cannot be forcibly stopped inside the target process. Scry does not claim process isolation or hard timeouts.

`ScryClient.RequestTimeout` (default 60s, `scry`'s `--timeout` on the CLI) bounds only the
*client's* wait for a reply, not the target's execution. Before it existed, a submission like
this hung the calling client - and every later request on it - forever, because the pipe read
had no deadline of its own and .NET Framework's `PipeStream` does not honour a cancellation
token once a read has started. On expiry the client raises `TimeoutException` and retires the
connection rather than leaving it open: the abandoned request's response can still arrive later,
and reading it as the reply to a subsequent request would be worse than failing outright. The
target-side thread the submission occupied is not reclaimed by this - only a fresh attach or
restart of the target does that.

Host defaults are configurable through `EndpointOptions`: source length, default/maximum execution milliseconds, imports/references, bounded logs, type result/member limits, and assembly file size. The protocol frame limit remains an independent upper bound.


### Compilation reuse

Compiling a submission is expensive in a real process: it builds a Roslyn metadata reference for
every compatible loaded assembly and then runs codegen. Measured against a WPF application with a
few hundred loaded assemblies, one `evaluate` cost roughly eight seconds. `wait` re-evaluates a
single expression until it holds, so without reuse every poll paid that again - a poll loop cost
seconds per attempt rather than milliseconds.

Two caches address it, and between them a repeated submission goes from about eight seconds to
zero:

- `AssemblyCatalog` caches `MetadataReference` instances by assembly file path. A loaded
  assembly's file cannot be swapped underneath the loaded image, so the parsed metadata stays
  valid for the life of the process. This speeds up *every* compile, including the first one for a
  new submission.
- `ScriptCache` caches the compiled `Script` by source, imports and explicit references, bounded
  by `MaximumCachedScripts` (default 64) with approximate least-recently-used eviction. The key
  uses the already-wrapped source, so an `evaluate` expression and an `execute` statement body
  with the same text cannot share an entry.

Only successful compilations are cached, which is what keeps this honest in a process that is
still loading assemblies: a submission that failed to compile because its type was not loaded yet
is recompiled next time and can then succeed, while a submission that already compiled stays valid
because the assemblies it bound to cannot be unloaded from the default AppDomain.

`ExecutionResult.CompilationCached` reports which path a submission took, so a caller can tell a
fast repeat from a cold compile, and tests can assert the behaviour without relying on timing.
`CompileMilliseconds` (null on a cache hit) and `RunMilliseconds` split the same distinction out
as timing, separate from `ElapsedMilliseconds`'s combined total - a slow compile points at
reference resolution or script complexity, a slow run points at the submission's own work.
`Marshalled` and `ThreadId` report whether the submission actually ran through the host's
execution marshaller and which managed thread it completed on; before these existed, a caller
asking for `"marshal": "ui"` had no positive confirmation it was honoured beyond the request not
failing.

One cost no cache removes: a marshalled submission has to wait for the target's UI thread. While
the target is busy - during its own startup, say - each marshalled poll queues behind that work.
Measured on an idle application a marshalled `wait` attempt costs about seven milliseconds beyond
the poll interval; during application startup the same attempt cost about a second, all of it
waiting for the dispatcher.

## Assembly loading and type discovery

`load-assembly` requires an absolute path. Loading differs by runtime:

- On .NET 9, `default` calls `AssemblyLoadContext.Default.LoadFromAssemblyPath`. `isolated` creates a named collectible `AssemblyLoadContext` with `AssemblyDependencyResolver`. Scry retains isolated contexts for the host lifetime; there is no unload operation in this release. Isolated assemblies are available to list/find/describe operations but are intentionally excluded from Roslyn references.
- On .NET Framework 4.7.2, only `AppDomain.CurrentDomain` is supported. `default` uses `Assembly.LoadFrom` in that AppDomain, and descriptions report `DefaultAppDomain`. `isolated` fails with `load_policy_not_supported`: a child AppDomain cannot preserve Scry's in-process roots, handles, reflection objects, and Roslyn type identity.

Loading is explicit: evaluation never loads assemblies by path or probes arbitrary directories. `list-assemblies` reports identity, location, dynamic status, load context, default-context status, and collectibility. `find-types` performs bounded filtering over loaded types and reports each type's load context. `describe-type` returns bounded member metadata; `assembly` and `loadContext` selectors disambiguate duplicate full type names across assemblies or contexts.

## Jobs

Jobs execute an ordinary non-job protocol operation in the target process. `job.start` returns immediately with a `JobSnapshot` and a `JobHandle` containing `targetId`, `sessionId`, and `jobId`. The endpoint owns execution, cancellation, result/error state, and logs; the CLI stores no state. `scry jobs status|wait|cancel|logs` infers the session from the supplied handle, so a later CLI process can resume it.

States are `queued`, `running`, `succeeded`, `failed`, and `canceled`. A wait timeout returns `{ "job": <current snapshot>, "timedOut": true }`; timeout is never represented as a job state. Cancellation is cooperative. Completed entries expire after `JobRetention`, and admission remains bounded by `MaximumJobs`. Logs are bounded per job and use monotonically increasing cursors. If requested entries have already rolled off, `truncated` is true and `oldestCursor` identifies the first retained entry.

```powershell
scry jobs start --target my-test-target --correlation build-42 --request job-start.json

scry jobs wait --target my-test-target --request job-wait.json
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

`.\validate.ps1` runs every step below in order and stops at the first failure, naming which step
failed rather than leaving that to be read out of a wall of build output. It is the same matrix
`.github/workflows/ci.yml` runs, kept in one place so neither can drift from the other. Pass
`-Quick` to skip the native rebuild and the x86 leg for a faster inner-loop check; a real
validation pass should use neither.

Run the modern and desktop CLR suites explicitly:

```powershell
.\build-native.ps1
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
- The agent Skill drives the stable CLI JSON surface rather than acquiring in-process state.
