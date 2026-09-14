# Threat model

This consolidates the security reasoning that is otherwise scattered across `README.md` and
`docs/development.md`, and states plainly what is a considered decision versus what is simply not
built yet. Read it before deciding whether Scry.NET is appropriate for a given machine or process.

## What Scry.NET is

Scry.NET is a **local, same-user remote-code-execution surface, by design.** `evaluate` and
`execute` run arbitrary C# inside the target process; `set` and `invoke` mutate live state; `wpf.*`
and `winforms.*` walk and act on the UI. None of that is a vulnerability to be patched - it is the
entire feature. The question this document answers is not "how do we stop that" but "who can reach
it, and what does the target's own log say happened afterward."

## Assets

- **The target process's memory and behavior.** Anyone who can complete a handshake can read,
  write, and execute inside it.
- **The capability token.** A 256-bit random value generated per endpoint instance
  (`RuntimeHost.cs`), transmitted once at handshake, and never written to disk outside the
  connection descriptor. Possessing it is equivalent to possessing full access to that endpoint for
  its lifetime.
- **The connection descriptor** (`%LOCALAPPDATA%\Scry\targets\<targetId>.json`), which carries the
  token in plain text. Anyone who can read that file has the same access as the target's own user.
- **The audit log** (`%LOCALAPPDATA%\Scry\audit\*.jsonl`, see below), which is a record of what
  happened - valuable to an investigator, and a target for tampering by anyone with write access to
  it.

## Trust boundary

The boundary is the current Windows user, not the process. The named pipe is
`PipeOptions.CurrentUserOnly` on .NET 9 and carries an explicit single-SID DACL on .NET Framework
4.7.2 (`RuntimeHost.CreatePipe`) - only processes running as the same Windows user can even open the
pipe. Within that boundary, Scry.NET adds one further gate (the capability token) and does not
attempt a second one:

**There is no capability restriction and no production-enablement gate, by decision.** `EndpointHost.Start()`
with no arguments is a valid call that opens a fully-capable endpoint in any build configuration -
there is no `#if DEBUG`, no required environment variable, no acknowledgement parameter. This was
considered and deliberately not built. The reasoning: a caller who can reach the point of injecting
a DLL into a process, or of adding a line of code to it, can already manipulate that process by
other means at the same trust level - a library-side gate adds a check the same actor can simply not
call, or route around, while adding an API surface for every embedding host to reason about for a
protection it doesn't actually provide against that actor. The decision **does** shift real
responsibility onto the hosting application: an application that calls `EndpointHost.Start()`
unconditionally in a shipped build has created a remote-code-execution surface for anyone who can
reach it as that Windows user, and Scry.NET will not stop that call from happening. Gate it in your
own code - a build-configuration check, a feature flag, an environment variable - the same way you
would gate any other capability you don't want reachable in production.

## What the audit log proves, and what it does not

Every connection, handshake attempt, and operation is recorded to
`%LOCALAPPDATA%\Scry\audit\*.jsonl` by default (`AuditLog`, `Scry.Runtime`), specifically so a
rejected authentication - previously invisible entirely - leaves a trace. Three honest limits:

- **`clientName` is self-asserted, not authenticated.** It is whatever the connecting client claims
  at handshake. A record can say a client calling itself `"scry"` did something; it cannot prove
  that claim.
- **No process identity is captured.** Capturing one would need the first native interop call in
  `Scry.Runtime` (`GetNamedPipeClientProcessId`), and that cost was judged higher than the value
  here. So an `authentication_failed` record can say *that* a bad token was presented and what the
  caller called itself, but not which process presented it. `connectionId` distinguishes concurrent
  callers within one run, but is not a stable identity across runs.
- **Attach mode can lose its last batch of records.** The audit log is flushed per write, but the
  in-process queue between "enqueued" and "written" is not guaranteed to drain if the process is
  terminated abruptly (`TerminateProcess`) rather than exiting normally - `AppDomain.ProcessExit`
  does not run in that case. Closing this window fully would cost a disk sync per record; the
  residual exposure is a handful of records at most, and is accepted rather than paid for.

## Attach mode specifically

Attach mode requires the same Windows user and an equal or higher integrity level than the target,
and inspects process architecture and loaded CLR modules before writing anything into target
memory (`ProcessInspector`, `BindingPolicyInspector`). It refuses ambiguous or unrecognized
runtimes rather than guessing. None of this defends against a hostile *target* application - it
defends against attaching to the wrong thing by accident, and against a small set of failure modes
(architecture mismatch, a binding-policy conflict) that would otherwise corrupt the target rather
than failing cleanly.

Injecting code is inherently disruptive: it can destabilize the target process, and the primitives
involved (`CreateRemoteThread`, remote `LoadLibrary`) are exactly what endpoint-security products
watch for and commonly block or quarantine. Do not weaken security controls globally to make attach
mode work; authorize the specific binary, or test in an isolated environment. **Never attach to a
process you do not own or do not have explicit permission to test.**

## Unsupported and out of scope

Current, as of this document:

- **No output capture.** A script's `Console.WriteLine`/`Trace.Write` inside the target is not
  intercepted; only explicit `Context.Log(...)` calls reach the response. Capturing target output
  correctly needs a process-wide `Console.SetOut`/`TraceListener` scoped per concurrent submission
  (connections are served concurrently, so a naive redirect would cross-contaminate them), and that
  has not been built.
- **No handle for a projected UI node.** A `wpf.snapshot`/`winforms.snapshot` node cannot be turned
  into a leasable reference for a later `get`/`set`/`invoke`; only its `path` can be fed back into
  `wpf.wait`/`wpf.screenshot`. Bridging the projection model to the session handle table is a design
  change to the adapters, not a gap in the current one.
- **No secondary AppDomains, no ARM64, no remote machines, no unload/detach.** The endpoint starts
  only in the default AppDomain (or default CoreCLR load context) of a local process on Windows
  x86/x64.
- **No production packaging.** There is no NuGet publication and no signed release; building from
  source is the only supported path today.
- **Fatal runtime failures bypass the protocol entirely.** Process termination, a stack overflow, or
  a fail-fast in the target crosses no boundary cleanly - the connection simply ends. This is a
  property of the CLR, not something an in-process endpoint can intercept.

## A decision-in-waiting: remote transport

Moving from named pipes to HTTP, so one agent or test can drive a desktop client and a remote
server component in a single end-to-end flow, is a capability worth having and has been discussed.
The reasoning in this document - "a same-user actor who can already manipulate the process gains
little from a library-side gate" - is what makes the *current* lack of a stronger authorization
model defensible. **That reasoning does not survive a move to a remote, unauthenticated transport.**
Reachability changes from "already executing code as this user, on this machine" to "anyone who can
open a connection to this port," which hands an unauthenticated network peer arbitrary code
execution inside the target. If this transport change happens, it should happen alongside, not
after, a real authentication story for the remote case - and the audit log's actor fields become
considerably more load-bearing than they are today, with a peer address as the natural
process-identity field the current design deliberately omits.

## Reporting

Scry.NET has no dedicated security contact or disclosure process at this time; it is local-only
tooling, not a deployed service. If that changes with packaging, this section should be updated
before publication.
