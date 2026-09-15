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

## Start safely

1. Confirm the target is a local development or test process.
2. Run `scry discover`.
3. Select one exact target. Prefer its `targetId`; use an alias only when it is unambiguous.
4. Run `scry capabilities` and `scry roots`.
5. Prefer `inspect`, `get`, `set`, `invoke`, and `enumerate`.
6. Use `wpf.*` or `winforms.*` projection operations for UI traversal and synchronization.
7. Use `evaluate` or `execute` only when the structured operations cannot do the job.
8. Validate mutations with a fresh read, wait, or assertion.
9. Release leased handles when a persistent session is used.

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
`%LOCALAPPDATA%\Scry\targets` directory - they can never surface a target that lives on another
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

## Structured error recovery

Use the error code before the message:

- `invalid_request`, `argument_*`, or `usage_error`: correct fields/types using
  `scry help <command>` or `scry schema`.
- `target_not_found`: rediscover; an old target may have exited or an alias may be
  ambiguous.
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
- Treat non-public inspection and C# execution as privileged debugging actions.
- Prefer public structured operations and the least mutation necessary.
- Do not execute downloaded, unreviewed, or user-secret-bearing source.
- Do not claim a projection is complete when it is bounded or truncated.
- Do not claim a hard timeout; cancellation is cooperative.
- Preserve operation/correlation IDs in failure reports, but redact sensitive result data.
