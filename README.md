# Scry.NET

Scry.NET is a Windows-only developer and test framework for inspecting and deliberately mutating a running managed application. Each target owns its endpoint and state; the stateless `scry` CLI connects over a local Windows named pipe.

Two hosting models are planned:

- **Embedded mode (implemented):** the target opts in with `Scry.Sdk`, registers roots, values, and operations, and starts an `AgentHost`.
- **Attach mode (future):** tooling injects or loads the runtime into an existing managed process. Injection is not part of this foundation.

The endpoint supports non-UI processes as a first-class scenario. The core packages provide a versioned JSON protocol, current-user named-pipe transport, capability-token authentication, multi-process discovery with aliases, target-qualified sessions and leased handles, reflection inspection/mutation/invocation, collection pagination, Roslyn-backed C# evaluation and statement execution, explicit assembly/type discovery, endpoint-owned long-running jobs, an embedded SDK, and a stateless JSON CLI with multi-target scenarios. Optional `Scry.Wpf` and `Scry.WinForms` packages add desktop UI inspection without adding UI framework references to `Scry.Contracts`, `Scry.Runtime`, or `Scry.Sdk`. Injection and an agent Skill remain deferred.

| Component | Supported targets |
|---|---|
| `Scry.Contracts`, `Scry.Runtime`, `Scry.Sdk` | .NET 9 and .NET Framework 4.8 |
| `Scry.SampleHost` | .NET 9 and .NET Framework 4.8 |
| `Scry.Cli` | .NET 9 only; it can connect to either runtime |
| `Scry.Wpf`, `Scry.WinForms` | .NET 9 (Windows) only |

Scry.NET permits deliberate code execution and state mutation inside the target. It is **local-only developer/test tooling**, not a remote administration service. Pipe names and tokens are random, pipes are current-user-only, and capability tokens are stored only in the current user's rendezvous directory. .NET 9 uses `PipeOptions.CurrentUserOnly`; .NET Framework 4.8 creates a protected pipe DACL granting only the current Windows SID. Do not expose descriptors or bridge the protocol to untrusted clients.

Build and test:

```powershell
dotnet build Scry.sln
dotnet test Scry.sln --no-build
dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net48 --artifacts-path artifacts\net48-x64 -p:PlatformTarget=x64 -- RunConfiguration.TargetPlatform=x64
dotnet test tests\Scry.Tests\Scry.Tests.csproj -c Release -f net48 --artifacts-path artifacts\net48-x86 -p:PlatformTarget=x86 -- RunConfiguration.TargetPlatform=x86
```

Run `dotnet run --project samples\Scry.SampleHost`, then use the emitted descriptor path:

```powershell
dotnet run --project src\Scry.Cli -- capabilities --descriptor <path>
dotnet run --project src\Scry.Cli -- roots --descriptor <path>
dotnet run --project src\Scry.Cli -- evaluate --descriptor <path> --source expression.csx
Get-Content statements.csx | dotnet run --project src\Scry.Cli -- execute --descriptor <path>
dotnet run --project src\Scry.Cli -- jobs start --target scry-sample --json '{"operation":"invoke","payload":{"registeredOperation":"delay","arguments":{"milliseconds":1000}}}'
dotnet run --project src\Scry.Cli -- scenario --input scenario.json
```

See [`docs/development.md`](docs/development.md) for protocol and extension guidance.

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

The adapters register `wpf.*` or `winforms.*` snapshot, wait, assertion, and screenshot operations. Their projections are deliberately bounded and framework-specific. WPF visual and logical trees are separate views; WinForms exposes managed controls, open/owned forms, tool strips and menus, and bindings. Neither adapter claims to represent owner-drawn pixels, WebView2/ActiveX content, native child windows, popups/separate HWNDs, or out-of-process surfaces completely.
