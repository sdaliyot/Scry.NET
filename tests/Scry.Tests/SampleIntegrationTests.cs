using System.Diagnostics;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Tests;

// The whole suite drives the samples out-of-process through the scry CLI, which is net9.0
// only, and two of the samples are net9.0-windows. It also relies on ProcessStartInfo.
// ArgumentList, Process.WaitForExitAsync, Process.Kill(entireProcessTree) and
// Task.WaitAsync, none of which exist on .NET Framework. Scope it to the modern leg, the
// same way the other CLI-driving tests in this project are scoped.
#if NET9_0_OR_GREATER
[Collection("Scry integration")]
public sealed class SampleIntegrationTests
{
    public static TheoryData<SampleCase> Samples => new()
    {
        new(
            "Scry.SampleHost",
            "Scry.SampleHost.dll",
            "app",
            "Count",
            21,
            "counter.set",
            new { value = 21 },
            "counter.recalculate",
            new { milliseconds = 25, value = 34 }),
        new(
            "Scry.SampleWorker",
            "Scry.SampleWorker.dll",
            "worker",
            "Mode",
            "paused",
            "queue.set-mode",
            new { mode = "paused" },
            "queue.drain",
            new { milliseconds = 25, items = 3 }),
        new(
            "Scry.SampleWpf",
            "Scry.SampleWpf.dll",
            "editor",
            "Title",
            "Agent draft",
            "editor.set-title",
            new { title = "Agent draft" },
            "editor.load-document",
            new { milliseconds = 25, title = "Loaded document" },
            "wpf.assert",
            new { root = "main", name = "caption", state = "textEquals", expected = "Agent draft" }),
        new(
            "Scry.SampleWinForms",
            "Scry.SampleWinForms.dll",
            "orders",
            "Status",
            "Reviewing",
            "orders.set-status",
            new { status = "Reviewing" },
            "orders.import",
            new { milliseconds = 25, count = 4 },
            "winforms.assert",
            new { root = "main", name = "status", state = "textEquals", expected = "Reviewing" })
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Embedded_samples_support_cli_discovery_mutation_assertion_and_jobs(SampleCase sample)
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException("Could not determine the test configuration.");
        var samplePath = FindBuiltAssembly(
            Path.Combine(root, "samples", sample.Project, "bin", configuration),
            sample.Assembly);
        var cliPath = FindBuiltAssembly(
            Path.Combine(root, "src", "Scry.Cli", "bin", configuration),
            "scry.dll");
        var alias = $"{sample.Project.ToLowerInvariant()}-{Guid.NewGuid():N}";
        await using var process = await SampleProcess.StartAsync(samplePath, alias);

        var capabilities = await RunCliAsync(cliPath, "capabilities", "--descriptor", process.DescriptorPath);
        var registered = capabilities.GetProperty("result")
            .GetProperty("registeredOperations")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("name").GetString()!, StringComparer.Ordinal);
        Assert.Contains(sample.MutationOperation, registered.Keys);
        Assert.True(registered[sample.MutationOperation].GetProperty("requiresConfirmation").GetBoolean());
        Assert.Contains(sample.JobOperation, registered.Keys);

        var roots = await RunCliAsync(cliPath, "roots", "--descriptor", process.DescriptorPath);
        Assert.Contains(
            roots.GetProperty("result").GetProperty("roots").EnumerateArray(),
            item => item.GetProperty("name").GetString() == sample.Root);
        Assert.Contains(
            roots.GetProperty("result").GetProperty("roots").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "sample.kind" ||
                item.GetProperty("name").GetString() == "service.name");

        await RunCliAsync(
            cliPath,
            "invoke",
            "--descriptor",
            process.DescriptorPath,
            "--json",
            JsonSerializer.Serialize(
                new
                {
                    registeredOperation = sample.MutationOperation,
                    arguments = sample.MutationArguments
                },
                ScryJson.Options));
        var inspected = await RunCliAsync(
            cliPath,
            "get",
            "--descriptor",
            process.DescriptorPath,
            "--json",
            JsonSerializer.Serialize(
                new { root = sample.Root, member = sample.Member },
                ScryJson.Options));
        Assert.True(
            JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(sample.ExpectedValue, ScryJson.Options),
                inspected.GetProperty("result").GetProperty("value").GetProperty("value")),
            inspected.GetRawText());

        if (sample.AssertOperation is not null)
        {
            await RunCliAsync(
                cliPath,
                "invoke",
                "--descriptor",
                process.DescriptorPath,
                "--json",
                JsonSerializer.Serialize(
                    new
                    {
                        registeredOperation = sample.AssertOperation,
                        arguments = sample.AssertArguments
                    },
                    ScryJson.Options));
        }

        var started = await RunCliAsync(
            cliPath,
            "jobs",
            "start",
            "--descriptor",
            process.DescriptorPath,
            "--json",
            JsonSerializer.Serialize(
                new
                {
                    operation = "invoke",
                    payload = new
                    {
                        registeredOperation = sample.JobOperation,
                        arguments = sample.JobArguments
                    }
                },
                ScryJson.Options));
        var job = started.GetProperty("result").GetProperty("job");
        var completed = await RunCliAsync(
            cliPath,
            "jobs",
            "wait",
            "--descriptor",
            process.DescriptorPath,
            "--json",
            JsonSerializer.Serialize(new { job, timeoutMilliseconds = 5000 }, ScryJson.Options));
        Assert.Equal(
            JobStates.Succeeded,
            completed.GetProperty("result").GetProperty("job").GetProperty("state").GetString());
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

    private static string FindBuiltAssembly(string directory, string assembly)
    {
        var path = Directory.EnumerateFiles(directory, assembly, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return path ?? throw new FileNotFoundException(
            $"Could not locate a current '{assembly}' integration-test assembly.",
            directory);
    }

    private static async Task<JsonElement> RunCliAsync(string cliPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(cliPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the scry CLI.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw new TimeoutException(
                $"The CLI timed out while running: {string.Join(" ", arguments)}");
        }
        var standardOutput = await output.ConfigureAwait(false);
        var standardError = await error.ConfigureAwait(false);
        Assert.True(process.ExitCode == 0, standardError);
        using var document = JsonDocument.Parse(standardOutput);
        var response = document.RootElement.Clone();
        Assert.True(response.GetProperty("success").GetBoolean(), standardOutput);
        return response;
    }

    public sealed record SampleCase(
        string Project,
        string Assembly,
        string Root,
        string Member,
        object ExpectedValue,
        string MutationOperation,
        object MutationArguments,
        string JobOperation,
        object JobArguments,
        string? AssertOperation = null,
        object? AssertArguments = null);

    private sealed class SampleProcess : IAsyncDisposable
    {
        private readonly Process _process;

        private SampleProcess(Process process, string descriptorPath)
        {
            _process = process;
            DescriptorPath = descriptorPath;
        }

        public string DescriptorPath { get; }

        public static async Task<SampleProcess> StartAsync(string samplePath, string alias)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(samplePath);
            startInfo.ArgumentList.Add("--alias");
            startInfo.ArgumentList.Add(alias);
            startInfo.ArgumentList.Add("--wait-for-stdin");
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start an embedded sample.");
            try
            {
                var targetLine = await process.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                var descriptorLine = await process.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                if (targetLine?.StartsWith("Target: ", StringComparison.Ordinal) != true ||
                    descriptorLine?.StartsWith("Descriptor: ", StringComparison.Ordinal) != true)
                {
                    throw new InvalidOperationException(
                        $"Sample did not publish startup metadata: '{targetLine}' / '{descriptorLine}'.");
                }

                return new(process, descriptorLine["Descriptor: ".Length..]);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }

                process.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync().ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                try
                {
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }

            _process.Dispose();
            Assert.False(File.Exists(DescriptorPath));
        }
    }
}
#endif
