# Scry.NET

Scry.NET is a Windows-only developer and test framework for inspecting and deliberately mutating a running managed application. Each target owns its endpoint and state; the stateless `scry` CLI connects over a local Windows named pipe.

Two hosting models are planned:

- **Embedded mode (implemented):** the target opts in with `Scry.Sdk`, registers roots, values, and operations, and starts an `AgentHost`.
- **Attach mode (future):** tooling injects or loads the runtime into an existing managed process. Injection is not part of this foundation.

The endpoint supports non-UI processes as a first-class scenario. This foundation provides a versioned JSON protocol, current-user named-pipe transport, capability-token authentication, discovery, target-qualified sessions and leased handles, reflection inspection/mutation/invocation, collection pagination, an embedded SDK, and a JSON CLI. WPF/WinForms adapters, Roslyn execution, jobs, .NET Framework 4.8, injection, and an agent Skill are intentionally deferred.

Scry.NET permits deliberate code execution and state mutation inside the target. It is **local-only developer/test tooling**, not a remote administration service. Pipe names and tokens are random, pipes are current-user-only, and capability tokens are stored only in the current user's rendezvous directory. Do not expose descriptors or bridge the protocol to untrusted clients.

Build and test:

```powershell
dotnet build Scry.sln
dotnet test Scry.sln --no-build
```

Run `dotnet run --project samples\Scry.SampleHost`, then use the emitted descriptor path:

```powershell
dotnet run --project src\Scry.Cli -- capabilities --descriptor <path>
dotnet run --project src\Scry.Cli -- roots --descriptor <path>
```

See [`docs/development.md`](docs/development.md) for protocol and extension guidance.
