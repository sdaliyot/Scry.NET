# Foundation development guide

## Architecture

| Project | Responsibility |
|---|---|
| `Scry.Contracts` | Wire contracts, framing, target descriptors, discovery |
| `Scry.Runtime` | Named-pipe server, sessions, handles, reflection operations |
| `Scry.Sdk` | Embedded `AgentHost`, registration builder, protocol client |
| `Scry.Cli` | Stateless `scry` JSON command line |
| `Scry.SampleHost` | Non-UI embedded example |
| `Scry.Tests` | Protocol, runtime, and discovery tests |

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

## Operations

All non-handshake requests use the negotiated session. Subjects are selected with either `"root":"name"` or `"reference":{...}`. `inspect`, `get`, `set`, and `invoke` accept `"includeNonPublic":true` as an explicit opt-in.

| Operation | Important payload fields |
|---|---|
| `capabilities` | none |
| `roots` | none |
| `inspect` | root/reference, includeNonPublic |
| `get` | root/reference, member, includeNonPublic |
| `set` | root/reference, member, value, includeNonPublic |
| `invoke` | root/reference + member + arguments, or registeredOperation + arguments |
| `enumerate` | root/reference, offset, limit (1-1000) |
| `release` | handleId or handleIds |

Values are returned as `RemoteValue`: scalars are inline and objects receive an `ExternalReference`. Passing a reference as an argument preserves object identity. Collection pages deliberately return handles for non-scalar items.

The CLI reads request objects with `--input <file>`, `--input -`, redirected stdin, or `--json <object>`. Exit codes are stable: `0` success, `2` usage/JSON error, `3` target resolution error, `4` connection/authentication/protocol error, `5` target operation error, and `70` unexpected CLI failure. Tokens are never accepted as command-line options.

Fresh CLI connections negotiate ephemeral sessions that are removed on disconnect, so stateless command use does not retain target resources. Passing `--session` resumes a persistent session instead; callers own its handles until release or lease expiry.

## Extensibility boundaries

- Add protocol operations and capability names without changing framing.
- Add runtime adapters (WPF/WinForms) as registered roots/operations rather than coupling UI assemblies into the core.
- Add Roslyn execution and background jobs as opt-in capabilities with dedicated request contracts and lifecycle controls.
- Keep attach/injection responsible only for loading and bootstrapping the same runtime endpoint.
- A future Skill should drive the stable CLI JSON surface rather than acquire in-process state.
