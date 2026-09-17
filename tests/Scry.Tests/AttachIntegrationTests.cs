#if !NETFRAMEWORK
using System.Diagnostics;
using System.Text.Json;
using Scry.Injector;

namespace Scry.Tests;

public sealed class AttachIntegrationTests
{
    [Fact]
    public async Task Injects_agent_into_unmodified_modern_dotnet_process()
    {
        if (!OperatingSystem.IsWindows() ||
            ProcessInspector.CurrentArchitecture != TargetArchitecture.X64)
        {
            return;
        }

        var root = FindRepositoryRoot();
        var targetAssembly = Path.Combine(
            root,
            "samples",
            "Scry.AttachTarget",
            "bin",
            "Release",
            "net8.0",
            "Scry.AttachTarget.dll");
        var cliAssembly = Path.Combine(
            root,
            "src",
            "Scry.Cli",
            "bin",
            "Release",
            "net8.0",
            "scry.dll");
        Assert.True(File.Exists(targetAssembly), $"Attach target is missing: {targetAssembly}");
        Assert.True(
            File.Exists(Path.Combine(
                Path.GetDirectoryName(cliAssembly)!,
                "native",
                "win-x64",
                "Scry.Injector.Native.dll")),
            "Build the native x64 helper before running the attach integration test.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{targetAssembly}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the attach target.");

        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reportedProcessId = await process.StandardOutput.ReadLineAsync(startupTimeout.Token);
            Assert.Equal(process.Id.ToString(), reportedProcessId);

            var alias = $"injected-test-{Guid.NewGuid():N}";
            var attached = await RunCliAsync(
                cliAssembly,
                $"attach {process.Id} --alias {alias}",
                input: null,
                TimeSpan.FromSeconds(30));
            Assert.True(
                attached.ExitCode == 0,
                $"Attach failed ({attached.ExitCode}): {attached.StandardOutput}{attached.StandardError}");
            using var attachJson = JsonDocument.Parse(attached.StandardOutput);
            Assert.True(attachJson.RootElement.GetProperty("success").GetBoolean());

            var evaluated = await RunCliAsync(
                cliAssembly,
                $"evaluate --target {alias}",
                "return 6 * 7;",
                TimeSpan.FromSeconds(20));
            Assert.True(
                evaluated.ExitCode == 0,
                $"Evaluation failed ({evaluated.ExitCode}): {evaluated.StandardOutput}{evaluated.StandardError}");
            using var resultJson = JsonDocument.Parse(evaluated.StandardOutput);
            Assert.Equal(
                42,
                resultJson.RootElement.GetProperty("result")
                    .GetProperty("value")
                    .GetProperty("value")
                    .GetInt32());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    /// <summary>
    /// End-to-end proof that <c>tcpPort=</c> makes it through the whole attach config-blob chain
    /// (<c>AttachCommandLine</c> -&gt; <c>AttachArguments</c> -&gt; <c>AttachService.AttachAsync</c>
    /// -&gt; <c>NativeBootstrap.BuildConfiguration</c> -&gt; <c>InjectedEndpointEntryPoint.ParseConfiguration</c>)
    /// and that the resulting listener is real: the attached process is driven with a genuine TCP
    /// handshake and an evaluate, not merely asserted to exist.
    /// </summary>
    [Fact]
    public async Task Attached_endpoint_with_tcp_port_is_reachable_over_a_real_tcp_handshake()
    {
        if (!OperatingSystem.IsWindows() ||
            ProcessInspector.CurrentArchitecture != TargetArchitecture.X64)
        {
            return;
        }

        var root = FindRepositoryRoot();
        var targetAssembly = Path.Combine(
            root, "samples", "Scry.AttachTarget", "bin", "Release", "net8.0", "Scry.AttachTarget.dll");
        var cliAssembly = Path.Combine(root, "src", "Scry.Cli", "bin", "Release", "net8.0", "scry.dll");
        Assert.True(File.Exists(targetAssembly), $"Attach target is missing: {targetAssembly}");
        Assert.True(
            File.Exists(Path.Combine(
                Path.GetDirectoryName(cliAssembly)!, "native", "win-x64", "Scry.Injector.Native.dll")),
            "Build the native x64 helper before running the attach integration test.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{targetAssembly}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the attach target.");

        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reportedProcessId = await process.StandardOutput.ReadLineAsync(startupTimeout.Token);
            Assert.Equal(process.Id.ToString(), reportedProcessId);

            var alias = $"injected-tcp-test-{Guid.NewGuid():N}";
            var attached = await RunCliAsync(
                cliAssembly,
                $"attach {process.Id} --alias {alias} --tcp-port 0",
                input: null,
                TimeSpan.FromSeconds(30));
            Assert.True(
                attached.ExitCode == 0,
                $"Attach failed ({attached.ExitCode}): {attached.StandardOutput}{attached.StandardError}");
            using var attachJson = JsonDocument.Parse(attached.StandardOutput);
            Assert.True(attachJson.RootElement.GetProperty("success").GetBoolean());
            var tcpPort = attachJson.RootElement.GetProperty("tcpPort").GetInt32();
            Assert.True(tcpPort is > 0 and <= 65535);

            var descriptorPath = (await Scry.Contracts.TargetDiscovery.ResolveAsync(alias)).Path;
            var descriptor = await Scry.Contracts.TargetDiscovery.ReadAsync(descriptorPath);
            Assert.Equal(tcpPort, descriptor.TcpPort);

            await using var client = await Scry.Client.ScryClient.ConnectOverTcpAsync(descriptor);
            Assert.Equal("tcp", client.Transport);
            var evaluated = await client.EvaluateAsync(new Scry.Contracts.ExecutionRequest("6 * 7"));
            Assert.Equal(42, evaluated.Value.Value!.Value.GetInt32());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    /// <summary>
    /// End-to-end proof that <c>--targets-dir</c> makes it through the same config-blob chain as
    /// <c>--tcp-port</c> above, added after an attach into an IIS application pool identity with no
    /// loaded user profile failed to publish its descriptor at all: the override must land the
    /// descriptor in the given directory and nowhere else, through the real injector and payload,
    /// not merely through <c>EndpointHost.Start</c> in-process (see <c>TargetsDirectoryTests</c> for
    /// that half).
    /// </summary>
    [Fact]
    public async Task Attached_endpoint_honours_a_targets_dir_override()
    {
        if (!OperatingSystem.IsWindows() ||
            ProcessInspector.CurrentArchitecture != TargetArchitecture.X64)
        {
            return;
        }

        var root = FindRepositoryRoot();
        var targetAssembly = Path.Combine(
            root, "samples", "Scry.AttachTarget", "bin", "Release", "net8.0", "Scry.AttachTarget.dll");
        var cliAssembly = Path.Combine(root, "src", "Scry.Cli", "bin", "Release", "net8.0", "scry.dll");
        Assert.True(File.Exists(targetAssembly), $"Attach target is missing: {targetAssembly}");
        Assert.True(
            File.Exists(Path.Combine(
                Path.GetDirectoryName(cliAssembly)!, "native", "win-x64", "Scry.Injector.Native.dll")),
            "Build the native x64 helper before running the attach integration test.");

        var directory = Path.Combine(
            Path.GetTempPath(), "scry-attach-targets-dir-tests", Guid.NewGuid().ToString("N"));

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{targetAssembly}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the attach target.");

        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reportedProcessId = await process.StandardOutput.ReadLineAsync(startupTimeout.Token);
            Assert.Equal(process.Id.ToString(), reportedProcessId);

            var alias = $"injected-targets-dir-test-{Guid.NewGuid():N}";
            var attached = await RunCliAsync(
                cliAssembly,
                $"attach {process.Id} --alias {alias} --tcp-port 0 --targets-dir \"{directory}\"",
                input: null,
                TimeSpan.FromSeconds(30));
            Assert.True(
                attached.ExitCode == 0,
                $"Attach failed ({attached.ExitCode}): {attached.StandardOutput}{attached.StandardError}");
            using var attachJson = JsonDocument.Parse(attached.StandardOutput);
            Assert.True(attachJson.RootElement.GetProperty("success").GetBoolean());
            var descriptorPath = attachJson.RootElement.GetProperty("descriptorPath").GetString()!;

            Assert.StartsWith(directory, descriptorPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(descriptorPath));

            var foundInOverride = await Scry.Contracts.TargetDiscovery.FindAsync(directory);
            Assert.Contains(foundInOverride, item => item.Descriptor.Target.Alias == alias);

            var foundInDefault = await Scry.Contracts.TargetDiscovery.FindAsync();
            Assert.DoesNotContain(foundInDefault, item => item.Descriptor.Target.Alias == alias);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    /// <summary>
    /// The acceptance test for attach mode: a .NET Framework WPF process that does not
    /// reference Scry at all, inspected and driven from outside. Covers the three things that make
    /// attach mode worth having, and that the modern-.NET console test above cannot show:
    /// the desktop adapter gets wired inside the target by --adapters, the WPF logical tree is
    /// reachable, and "marshal": "ui" lets a submission touch DependencyObjects.
    /// </summary>
    [Fact]
    public async Task Injects_agent_and_wpf_adapter_into_unmodified_net_framework_wpf_process()
    {
        if (!OperatingSystem.IsWindows() ||
            ProcessInspector.CurrentArchitecture != TargetArchitecture.X64)
        {
            return;
        }

        var root = FindRepositoryRoot();
        var targetExecutable = Path.Combine(
            root,
            "samples",
            "Scry.AttachWpfTarget",
            "bin",
            "Release",
            "net462",
            "Scry.AttachWpfTarget.exe");
        var cliAssembly = Path.Combine(root, "src", "Scry.Cli", "bin", "Release", "net8.0", "scry.dll");
        Assert.True(File.Exists(targetExecutable), $"Attach target is missing: {targetExecutable}");
        Assert.True(
            File.Exists(Path.Combine(
                Path.GetDirectoryName(cliAssembly)!,
                "native",
                "win-x64",
                "Scry.Injector.Native.dll")),
            "Build the native x64 helper before running the attach integration test.");
        Assert.True(
            File.Exists(Path.Combine(
                Path.GetDirectoryName(cliAssembly)!,
                "payload",
                "netfx",
                "Scry.Wpf.dll")),
            "The WPF adapter must be staged beside the .NET Framework payload for --adapters wpf.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = targetExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the WPF attach target.");

        var requestDirectory = Path.Combine(Path.GetTempPath(), $"scry-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(requestDirectory);
        try
        {
            // The target prints its PID from Window.Loaded, so this also serves as the signal that
            // the logical tree exists and is worth snapshotting.
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var reportedProcessId = await process.StandardOutput.ReadLineAsync(startupTimeout.Token);
            Assert.Equal(process.Id.ToString(), reportedProcessId);

            var alias = $"injected-wpf-test-{Guid.NewGuid():N}";
            var attached = await RunCliAsync(
                cliAssembly,
                $"attach {process.Id} --alias {alias} --adapters wpf",
                input: null,
                TimeSpan.FromSeconds(60));
            Assert.True(
                attached.ExitCode == 0,
                $"Attach failed ({attached.ExitCode}): {attached.StandardOutput}{attached.StandardError}");
            using var attachJson = JsonDocument.Parse(attached.StandardOutput);
            Assert.True(attachJson.RootElement.GetProperty("success").GetBoolean());
            var target = attachJson.RootElement.GetProperty("target");
            Assert.Equal(
                (int)TargetRuntimeFamily.NetFramework,
                target.GetProperty("runtimeFamily").GetInt32());
            Assert.Equal((int)TargetArchitecture.X64, target.GetProperty("architecture").GetInt32());

            // --adapters wpf has to do two separable things inside the target: register the wpf.*
            // operations, and register the execution marshaller that backs "marshal": "ui".
            // Assert both, because the first can succeed while the second silently does not.
            var capabilities = await RunCliAsync(
                cliAssembly,
                $"capabilities --target {alias}",
                input: null,
                TimeSpan.FromSeconds(30));
            Assert.True(
                capabilities.ExitCode == 0,
                $"capabilities failed: {capabilities.StandardOutput}{capabilities.StandardError}");
            using var capabilitiesJson = JsonDocument.Parse(capabilities.StandardOutput);
            var result = capabilitiesJson.RootElement.GetProperty("result");
            Assert.Contains(
                "ui-thread-marshalling",
                result.GetProperty("features").EnumerateArray().Select(item => item.GetString()));
            Assert.Contains(
                "wpf.snapshot",
                result.GetProperty("registeredOperations")
                    .EnumerateArray()
                    .Select(item => item.GetProperty("name").GetString()));

            // The logical tree is the projection UI Automation cannot supply for content that lives
            // in a separate HwndSource, and is the reason the adapter exists.
            var snapshotRequest = Path.Combine(requestDirectory, "snapshot.json");
            await File.WriteAllTextAsync(snapshotRequest, """{"tree":"logical"}""");
            var snapshot = await RunCliAsync(
                cliAssembly,
                $"wpf.snapshot --target {alias} --request \"{snapshotRequest}\"",
                input: null,
                TimeSpan.FromSeconds(30));
            Assert.True(
                snapshot.ExitCode == 0,
                $"wpf.snapshot failed: {snapshot.StandardOutput}{snapshot.StandardError}");
            using var snapshotJson = JsonDocument.Parse(snapshot.StandardOutput);
            var snapshotResult = snapshotJson.RootElement.GetProperty("result");
            Assert.Equal("logical", snapshotResult.GetProperty("treeKind").GetString());
            Assert.Contains("attach-probe-button", snapshotResult.ToString());

            // Without a marshaller this is WPF's own thread affinity failing, not an endpoint
            // restriction - assert it so the marshalled case below is a real A/B rather than a
            // test that would pass either way.
            var unmarshalledRequest = Path.Combine(requestDirectory, "unmarshalled.json");
            await File.WriteAllTextAsync(
                unmarshalledRequest,
                """{"source":"System.Windows.Application.Current.MainWindow.Title"}""");
            var unmarshalled = await RunCliAsync(
                cliAssembly,
                $"evaluate --target {alias} --request \"{unmarshalledRequest}\"",
                input: null,
                TimeSpan.FromSeconds(30));
            using var unmarshalledJson = JsonDocument.Parse(unmarshalled.StandardOutput);
            Assert.False(unmarshalledJson.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(
                "operation_failed",
                unmarshalledJson.RootElement.GetProperty("error").GetProperty("code").GetString());

            var marshalledRequest = Path.Combine(requestDirectory, "marshalled.json");
            await File.WriteAllTextAsync(
                marshalledRequest,
                """{"source":"System.Windows.Application.Current.MainWindow.Title","marshal":"ui"}""");
            var marshalled = await RunCliAsync(
                cliAssembly,
                $"evaluate --target {alias} --request \"{marshalledRequest}\"",
                input: null,
                TimeSpan.FromSeconds(30));
            Assert.True(
                marshalled.ExitCode == 0,
                $"Marshalled evaluate failed: {marshalled.StandardOutput}{marshalled.StandardError}");
            using var marshalledJson = JsonDocument.Parse(marshalled.StandardOutput);
            Assert.Equal(
                "Scry Attach WPF Target",
                marshalledJson.RootElement.GetProperty("result")
                    .GetProperty("value")
                    .GetProperty("value")
                    .GetString());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            Directory.Delete(requestDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The end-to-end proof for AppDomain targeting: <c>Scry.MultiDomainAttachTarget</c> carries a
    /// second AppDomain ("PluginDomain") with a type loaded only there
    /// (<c>Scry.MultiDomainAttachTarget.Plugin.PluginMarker</c>) and never referenced from the
    /// default domain's own code. An ordinary attach must not see it; <c>--appdomain PluginDomain</c>
    /// must land the endpoint there and see it. This is what actually proves the
    /// <c>ICorRuntimeHost</c> COM declaration and the cross-domain hop work, rather than merely
    /// that the attach call returned success.
    /// </summary>
    [Fact]
    public async Task Attaching_with_appdomain_reaches_a_type_invisible_to_the_default_domain()
    {
        if (!OperatingSystem.IsWindows() ||
            ProcessInspector.CurrentArchitecture != TargetArchitecture.X64)
        {
            return;
        }

        var root = FindRepositoryRoot();
        var targetExe = Path.Combine(
            root, "samples", "Scry.MultiDomainAttachTarget", "bin", "Release", "net462",
            "Scry.MultiDomainAttachTarget.exe");
        var cliAssembly = Path.Combine(root, "src", "Scry.Cli", "bin", "Release", "net8.0", "scry.dll");
        Assert.True(File.Exists(targetExe), $"Multi-domain attach target is missing: {targetExe}");
        Assert.True(
            File.Exists(Path.Combine(
                Path.GetDirectoryName(cliAssembly)!, "native", "win-x64", "Scry.Injector.Native.dll")),
            "Build the native x64 helper before running the attach integration test.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = targetExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the multi-domain attach target.");

        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reportedProcessId = await process.StandardOutput.ReadLineAsync(startupTimeout.Token);
            Assert.Equal(process.Id.ToString(), reportedProcessId);

            var defaultAlias = $"mdtarget-default-{Guid.NewGuid():N}";
            var defaultAttach = await RunCliAsync(
                cliAssembly, $"attach {process.Id} --alias {defaultAlias}", input: null, TimeSpan.FromSeconds(30));
            Assert.True(defaultAttach.ExitCode == 0, defaultAttach.StandardOutput + defaultAttach.StandardError);
            using var defaultJson = JsonDocument.Parse(defaultAttach.StandardOutput);
            Assert.False(
                defaultJson.RootElement.GetProperty("handshake").GetProperty("target")
                    .TryGetProperty("appDomainId", out _),
                "An ordinary attach must not report an AppDomainId.");

            var defaultFind = await RunCliAsync(
                cliAssembly,
                $"find-types --target {defaultAlias}",
                """{"query":"PluginMarker"}""",
                TimeSpan.FromSeconds(20));
            Assert.True(defaultFind.ExitCode == 0, defaultFind.StandardOutput + defaultFind.StandardError);
            using var defaultFindJson = JsonDocument.Parse(defaultFind.StandardOutput);
            Assert.Empty(defaultFindJson.RootElement.GetProperty("result").GetProperty("types").EnumerateArray());

            // appdomain.list / appdomain.start: reach the other domain from an endpoint that was
            // never given --appdomain, without spending the process's one native injection again.
            var listResult = await RunCliAsync(
                cliAssembly, $"appdomain.list --target {defaultAlias}", "{}", TimeSpan.FromSeconds(20));
            Assert.True(listResult.ExitCode == 0, listResult.StandardOutput + listResult.StandardError);
            using var listJson = JsonDocument.Parse(listResult.StandardOutput);
            var domains = listJson.RootElement.GetProperty("result").GetProperty("domains")
                .EnumerateArray().ToArray();
            Assert.Contains(domains, domain => domain.GetProperty("friendlyName").GetString() == "PluginDomain");

            var startResult = await RunCliAsync(
                cliAssembly,
                $"appdomain.start --target {defaultAlias}",
                """{"selector":"PluginDomain"}""",
                TimeSpan.FromSeconds(20));
            Assert.True(startResult.ExitCode == 0, startResult.StandardOutput + startResult.StandardError);
            using var startJson = JsonDocument.Parse(startResult.StandardOutput);
            var siblingAlias = startJson.RootElement.GetProperty("result").GetProperty("alias").GetString()!;
            Assert.Equal(
                2, startJson.RootElement.GetProperty("result").GetProperty("appDomainId").GetInt32());

            var siblingFind = await RunCliAsync(
                cliAssembly,
                $"find-types --target {siblingAlias}",
                """{"query":"PluginMarker"}""",
                TimeSpan.FromSeconds(20));
            Assert.True(siblingFind.ExitCode == 0, siblingFind.StandardOutput + siblingFind.StandardError);
            using var siblingFindJson = JsonDocument.Parse(siblingFind.StandardOutput);
            var siblingTypes = siblingFindJson.RootElement.GetProperty("result").GetProperty("types")
                .EnumerateArray().ToArray();
            Assert.Single(siblingTypes);
            Assert.Equal(
                "Scry.MultiDomainAttachTarget.Plugin.PluginMarker",
                siblingTypes[0].GetProperty("fullName").GetString());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    /// <summary>
    /// A selector that matches no AppDomain must not fail the attach - the native injection that
    /// got this far cannot be retried without recycling the target - so it falls back to the
    /// default domain and says why on <c>target.appDomainSelectionWarning</c>.
    /// </summary>
    [Fact]
    public async Task An_unmatched_appdomain_selector_falls_back_to_the_default_domain_with_a_warning()
    {
        if (!OperatingSystem.IsWindows() ||
            ProcessInspector.CurrentArchitecture != TargetArchitecture.X64)
        {
            return;
        }

        var root = FindRepositoryRoot();
        var targetExe = Path.Combine(
            root, "samples", "Scry.MultiDomainAttachTarget", "bin", "Release", "net462",
            "Scry.MultiDomainAttachTarget.exe");
        var cliAssembly = Path.Combine(root, "src", "Scry.Cli", "bin", "Release", "net8.0", "scry.dll");
        Assert.True(File.Exists(targetExe), $"Multi-domain attach target is missing: {targetExe}");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = targetExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the multi-domain attach target.");

        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reportedProcessId = await process.StandardOutput.ReadLineAsync(startupTimeout.Token);
            Assert.Equal(process.Id.ToString(), reportedProcessId);

            var alias = $"mdtarget-fallback-{Guid.NewGuid():N}";
            var attached = await RunCliAsync(
                cliAssembly,
                $"attach {process.Id} --alias {alias} --appdomain NoSuchDomain",
                input: null,
                TimeSpan.FromSeconds(30));
            Assert.True(attached.ExitCode == 0, attached.StandardOutput + attached.StandardError);
            using var attachJson = JsonDocument.Parse(attached.StandardOutput);
            var target = attachJson.RootElement.GetProperty("handshake").GetProperty("target");
            Assert.False(target.TryGetProperty("appDomainId", out _));
            Assert.Contains(
                "matched no AppDomain",
                target.GetProperty("appDomainSelectionWarning").GetString());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<CliResult> RunCliAsync(
        string cliAssembly,
        string arguments,
        string? input,
        TimeSpan timeout)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{cliAssembly}\" {arguments}",
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the Scry CLI.");

        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        var standardOutput = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        await process.WaitForExitAsync(timeoutSource.Token);
        return new(process.ExitCode, await standardOutput, await standardError);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scry.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed record CliResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
#endif
