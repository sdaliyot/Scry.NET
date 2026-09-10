# Foundation development guide

## Architecture

| Project | Responsibility |
|---|---|
| `Scry.Contracts` | Wire contracts, framing, target descriptors, discovery |
| `Scry.Runtime` | Named-pipe server, sessions, handles, reflection, Roslyn execution, assembly catalog |
| `Scry.Sdk` | Embedded `AgentHost`, registration builder, protocol client |
| `Scry.Wpf` | Optional dispatcher-safe WPF projections, waits/assertions, screenshots |
| `Scry.WinForms` | Optional control-owner-marshalled WinForms projections, waits/assertions, screenshots |
| `Scry.Cli` | Stateless `scry` JSON command line |
| `Scry.SampleHost` | Non-UI embedded example |
| `Scry.Tests` | Protocol, runtime, and discovery tests |
| `Scry.Wpf.Tests` | STA dispatcher tests for the optional WPF adapter |
| `Scry.WinForms.Tests` | STA message-loop tests for the optional WinForms adapter |

Libraries use `ScryLibraryTargetFrameworks` from `Directory.Build.props`. It currently contains only `net9.0`, matching the installed reference packs. A later .NET Framework layer can change it to `net9.0;net48` after adding compatibility shims and the real net48 reference assemblies; no unsupported target is advertised today.

## Protocol and security

Frames are a 4-byte little-endian length followed by UTF-8 JSON. Protocol version 1 requires `handshake` first. The handshake authenticates a 256-bit random capability token, negotiates the version, creates or resumes a target-qualified session, and returns capabilities. Subsequent requests use structured success/error envelopes. Ordinary operation exceptions cross the boundary with type, message, stack, HResult, source, and recursively captured inner exceptions. Fatal runtime failures such as process termination, stack overflow, corrupted state, or fail-fast can bypass this boundary.

Discovery descriptors live under `%LOCALAPPDATA%\Scry\targets` and are removed on host disposal and normal process exit. The named pipe uses `PipeOptions.CurrentUserOnly`; descriptors and tokens must never be copied to logs, command-line arguments, telemetry, or remote systems. The CLI accepts a descriptor **path** or target identity/alias and reads the token locally.

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
    new AgentHostOptions { Alias = "my-test-target" });
```

`RegisterValue` retains a specific object, while `RegisterRoot` evaluates its factory for each request. Registered operations receive structured JSON rather than source text. Session and handle limits, lease durations, alias, and preview length are configurable.

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

Roslyn metadata references come only from compatible, file-backed managed assemblies already loaded in the target's default load context. Dynamic, native, unreadable, and non-default-context modules are skipped. This restriction preserves runtime type identity: Roslyn cannot safely bind script code to an existing isolated-context assembly instance. Optional `references` entries validate that named compatible target assemblies are loaded; they do not load files. Use `load-assembly` with the `default` policy first when code must name its types.

Timeouts and cancellation are cooperative. The configured server deadline cancels `Context.CancellationToken` and Roslyn async execution; target shutdown also cancels it. Code that awaits with the token observes `execution_timed_out`. Cancelling `ScryClient.RequestAsync` cancels local pipe I/O and faults that client connection, but protocol version 1 has no request-cancellation frame, so it does not claim to cancel work already executing in the target. Synchronous code that never observes server cancellation cannot be forcibly stopped safely inside the target process and can continue blocking that connection. Scry does not claim process isolation or hard timeouts.

Host defaults are configurable through `AgentHostOptions`: source length, default/maximum execution milliseconds, imports/references, bounded logs, type result/member limits, and assembly file size. The protocol frame limit remains an independent upper bound.

## Assembly loading and type discovery

`load-assembly` requires an absolute path:

- `default` calls `AssemblyLoadContext.Default.LoadFromAssemblyPath`. This gives normal target identity/unification behavior and is not unloadable.
- `isolated` creates a named collectible `AssemblyLoadContext` with `AssemblyDependencyResolver`. Scry retains the context for the host lifetime; there is no unload operation in this layer. Its assemblies are available to list/find/describe operations but are intentionally excluded from Roslyn references because scripts cannot preserve their existing load-context type identity.

Loading is explicit: evaluation never loads assemblies by path or probes arbitrary directories. `list-assemblies` reports identity, location, dynamic status, load context, default-context status, and collectibility. `find-types` performs bounded filtering over loaded types and reports each type's load context. `describe-type` returns bounded member metadata; `assembly` and `loadContext` selectors disambiguate duplicate full type names across assemblies or contexts.

## Extensibility boundaries

- Add protocol operations and capability names without changing framing.
- Keep runtime adapters (WPF/WinForms) as registered roots/operations rather than coupling UI assemblies into the core.
- Add background jobs as a separate capability with dedicated lifecycle controls; execution in this layer remains request-scoped.
- Keep attach/injection responsible only for loading and bootstrapping the same runtime endpoint.
- A future Skill should drive the stable CLI JSON surface rather than acquire in-process state.
