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

The boundary is the current Windows user for the named pipe - but not for the optional TCP
listener, which has no such boundary at all. The two transports must be reasoned about separately:

**The named pipe** is `PipeOptions.CurrentUserOnly` on .NET 9 and carries an explicit single-SID
DACL on .NET Framework 4.7.2 (`RuntimeHost.CreatePipe`) - only processes running as the same
Windows user can even open the pipe. Within that boundary, Scry.NET adds one further gate (the
capability token) and does not attempt a second one.

**The optional loopback TCP listener** (`RuntimeHostOptions.TcpPort` / `EndpointOptions.TcpPort` /
`scry attach --tcp-port`, off by default) has no equivalent boundary. A TCP socket has no
`CurrentUserOnly` flag and no DACL: once the listener is enabled, **any local process, running as
any Windows user, can open a connection and attempt a handshake against it.** The 256-bit
capability token becomes the *only* gate - there is no longer a Windows-user check standing in
front of it. Restoring an equivalent peer check would need `GetExtendedTcpTable` (mapping a
loopback connection's ephemeral port back to the owning process and its token), which is the same
class of native interop this document already declines for process identity in the audit log
below; it has not been built. This is judged acceptable because the listener is opt-in, off by
default, loopback-only (never a routable bind - the token would otherwise cross a real network in
cleartext, which is a materially different exposure), and the token remains a full 256 bits of
CSPRNG output. It is **not** acceptable to assume TCP is defended by anything the pipe used to
provide for free: **enabling `--tcp-port` on a shared or multi-user machine hands every other
locally-logged-in user, and any process running under a service account on that machine, the same
access as the target's own user, gated only by whether they can guess or obtain the token.**

A consequence that follows directly: **a copied connection descriptor is now, unambiguously, a
bearer credential.** This was already true in spirit for the pipe (the token was always the
practical gate, with the Windows-user check as a second, independent one) but the TCP case makes it
literal - the descriptor file is the *only* thing standing between "nobody" and "full
remote-code-execution-equivalent access to that process, for the rest of the endpoint's lifetime."
Whoever holds a copy of it can drive the target for as long as the process runs, from anywhere that
can reach the forwarded port. Treat it accordingly: never commit it, log it, or leave it lying
around after the task that needed it is done - delete it.

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
  `Scry.Runtime` (`GetNamedPipeClientProcessId`, or `GetExtendedTcpTable` for the TCP case), and
  that cost was judged higher than the value here. So an `authentication_failed` record can say
  *that* a bad token was presented and what the caller called itself, but not which process
  presented it. `connectionId` distinguishes concurrent callers within one run, but is not a stable
  identity across runs; over TCP, `peerAddress` (an ephemeral loopback `host:port`) plays the same
  role and is subject to the same limit - it tells concurrent callers apart, it does not identify a
  process.
- **`hostUser` means something different per transport.** Over the pipe it is authenticated: only
  the current Windows user could have opened the pipe at all, so `hostUser` genuinely describes the
  caller. Over TCP it only ever describes the *host process's* user - any local process, as any
  Windows user, could be the actual caller - so `hostUser` on a `transport: "tcp"` record must not be
  read as an authenticated fact about who connected.
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
- **No secondary AppDomains, no ARM64, no unload/detach.** The endpoint starts only in the default
  AppDomain (or default CoreCLR load context) of a local process on Windows x86/x64. It can now be
  *reached* from another machine through an operator-established port forward to its loopback TCP
  listener (see "Remote transport" below), but the endpoint itself still only ever runs as a local
  process - Scry.NET performs no remote injection and sets up no tunnel of its own.
- **No production packaging.** There is no NuGet publication and no signed release; building from
  source is the only supported path today.
- **Fatal runtime failures bypass the protocol entirely.** Process termination, a stack overflow, or
  a fail-fast in the target crosses no boundary cleanly - the connection simply ends. This is a
  property of the CLR, not something an in-process endpoint can intercept.

## Remote transport: what was built, and why it stops at loopback

An earlier version of this document flagged remote transport as a decision-in-waiting and said
explicitly that the change must not land ahead of its reasoning. This section is that reasoning,
now that the change has landed: a loopback TCP listener (`RuntimeHostOptions.TcpPort` /
`EndpointOptions.TcpPort` / `scry attach --tcp-port`), off by default, reachable by a caller through
a port forward the operator sets up themselves (`ssh -L`, `netsh portproxy`). See `README.md`,
"Reaching an endpoint on another machine," for the operator-facing recipe.

**What did not change, and was never on the table:** the endpoint itself is not reachable over a
real network. There is no routable bind - `RuntimeHostOptions.TcpPort` binds
`IPAddress.Loopback` only, unconditionally, with no bind-address option to configure otherwise. The
capability token still crosses the wire in cleartext, so a listener bound to anything but loopback
would mean "shared secret sent in the clear over a network," which contradicts everything else in
this document. The reasoning that made a same-user actor's access to the endpoint the accepted
status quo - "an actor who can already manipulate the process gains little from a library-side
gate" - continues to hold for the pipe. It does **not** extend to TCP, and the previous section says
plainly what changes there: the Windows-user check disappears, and the token is the only gate.

**Why loopback-only is enough to ship, without a stronger authentication story.** A remote,
routable listener really would hand an unauthenticated network peer code execution, which is why
that was never built and remains explicitly out of scope. A *loopback* listener reached only
through a forward the operator controls is different in kind: the operator already decided who can
reach that port (their SSH access model, their `netsh portproxy` rule), and Scry.NET's token is a
second, independent gate on top of that decision, not the only thing standing between the process
and the public internet. The tradeoff being made is specifically "any local process/user on the
target machine can attempt a handshake" (see the previous section), not "anyone on the network
can."

**What is deliberately still not built:** TLS on the TCP path (the tunnel is expected to provide
transport security, the same way an SSH port forward already does), a routable/non-loopback bind of
any kind, IPv6 (an `::1` listener cannot share an ephemeral port with the required IPv4 one, so
`--tcp-port 0` would have to publish two ports for one descriptor to name - out of scope for this
round), a relay or sidecar process, and any capability restriction (a read-only endpoint). None of
these are ruled out permanently; they simply were not needed to make "drive a local client and
assert on a remote server component in one flow" work, and each would need its own reasoning
section here before landing.

**The one new operational obligation this creates:** a connection descriptor copied off the target
machine is a bearer credential good for full access to that process for its entire lifetime (stated
above, repeated here because it is the load-bearing consequence of this whole feature). Delete it
after use. There is no revocation mechanism short of restarting the endpoint.

## Reporting

Scry.NET has no dedicated security contact or disclosure process at this time; it is local-only
tooling, not a deployed service. If that changes with packaging, this section should be updated
before publication.
