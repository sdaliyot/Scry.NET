using System.Diagnostics;
using System.Text.Json;
using Scry.Contracts;
using Scry.Sdk;

namespace Scry.Tests;

[CollectionDefinition("Scry integration", DisableParallelization = true)]
public sealed class ScryIntegrationCollection;

[Collection("Scry integration")]
public sealed class JobAndScenarioTests
{
    [Fact]
    public async Task Jobs_are_scoped_correlated_cancellable_bounded_and_cleaned_up()
    {
        await using var first = JobTestHost.Start(
            aliases: ["job-primary"],
            retention: TimeSpan.FromMilliseconds(100),
            maximumJobs: 1,
            maximumLogs: 3);
        await using var second = JobTestHost.Start(aliases: ["job-secondary"]);
        await using var client = await ScryClient.ConnectAsync(first.Host.DescriptorPath);

        var start = await client.StartJobAsync(
            "invoke",
            new
            {
                registeredOperation = "work",
                arguments = new { milliseconds = 500 }
            },
            "correlation-1");
        Assert.True(start.Success);
        Assert.False(string.IsNullOrWhiteSpace(start.OperationId));
        Assert.Equal("correlation-1", start.CorrelationId);
        var job = start.Result!.Value.Deserialize<JobSnapshot>(ScryJson.Options)!;
        Assert.Equal(first.Host.TargetId, job.Job.TargetId);
        Assert.Equal(client.SessionId, job.Job.SessionId);
        Assert.Equal(job.Job.JobId, job.OperationId);
        Assert.Equal("correlation-1", job.CorrelationId);

        var limited = await client.StartJobAsync(
            "invoke",
            new
            {
                registeredOperation = "work",
                arguments = new { milliseconds = 1 }
            });
        Assert.Equal("job_limit_reached", limited.Error?.Code);

        var timedWait = await client.WaitForJobAsync(job.Job, TimeSpan.FromMilliseconds(10));
        var timedResult = timedWait.Result!.Value.Deserialize<JobWaitResult>(ScryJson.Options)!;
        Assert.True(timedResult.TimedOut);
        Assert.Contains(timedResult.Job.State, new[] { JobStates.Queued, JobStates.Running });

        await using (var otherSession = await ScryClient.ConnectAsync(first.Host.DescriptorPath))
        {
            var wrongSession = await otherSession.GetJobStatusAsync(job.Job);
            Assert.Equal("job_scope_mismatch", wrongSession.Error?.Code);
        }

        await using (var otherTarget = await ScryClient.ConnectAsync(second.Host.DescriptorPath))
        {
            var wrongTarget = await otherTarget.GetJobStatusAsync(job.Job);
            Assert.Equal("job_scope_mismatch", wrongTarget.Error?.Code);
        }

        var cancel = await client.CancelJobAsync(job.Job);
        Assert.True(cancel.Success);
        Assert.True(cancel.Result!.Value.GetProperty("cancellationRequested").GetBoolean());
        var completed = await client.WaitForJobAsync(job.Job, TimeSpan.FromSeconds(5));
        var completedResult = completed.Result!.Value.Deserialize<JobWaitResult>(ScryJson.Options)!;
        Assert.False(completedResult.TimedOut);
        Assert.Equal(JobStates.Canceled, completedResult.Job.State);

        var logs = await client.ReadJobLogsAsync(job.Job);
        var logResult = logs.Result!.Value.Deserialize<JobLogResult>(ScryJson.Options)!;
        Assert.True(logResult.Truncated);
        Assert.True(logResult.OldestCursor > 0);
        Assert.InRange(logResult.Entries.Count, 1, 3);
        Assert.Equal(
            logResult.Entries[^1].Cursor + 1,
            logResult.NextCursor);

        await Task.Delay(250);
        var expired = await client.GetJobStatusAsync(job.Job);
        Assert.Equal("job_not_found", expired.Error?.Code);
    }

    [Fact]
    public async Task Discovery_resolves_additional_aliases_and_rejects_ambiguous_aliases()
    {
        var shared = $"shared-{Guid.NewGuid():N}";
        var firstAlias = $"first-{Guid.NewGuid():N}";
        var secondAlias = $"second-{Guid.NewGuid():N}";
        await using var first = JobTestHost.Start(aliases: [shared, firstAlias]);
        await using var second = JobTestHost.Start(aliases: [shared, secondAlias]);

        var resolved = await TargetDiscovery.ResolveAsync(firstAlias);
        Assert.Equal(first.Host.TargetId, resolved.Descriptor.Target.TargetId);
        Assert.Contains(firstAlias, resolved.Descriptor.Target.Aliases!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TargetDiscovery.ResolveAsync(shared));
    }

    [Fact]
    public async Task Running_job_keeps_its_session_resumable_past_the_session_lease()
    {
        await using var host = JobTestHost.Start(sessionLease: TimeSpan.FromMilliseconds(500));
        JobSnapshot job;
        await using (var first = await ScryClient.ConnectAsync(host.Host.DescriptorPath))
        {
            var response = await first.StartJobAsync(
                "invoke",
                new
                {
                    registeredOperation = "work",
                    arguments = new { milliseconds = 1500 }
                });
            Assert.True(response.Success, response.Error?.Message);
            job = response.Result!.Value.Deserialize<JobSnapshot>(ScryJson.Options)!;
        }

        await Task.Delay(700);
        await using var resumed = await ScryClient.ConnectAsync(
            host.Host.DescriptorPath,
            job.Job.SessionId);
        var status = await resumed.GetJobStatusAsync(job.Job);
        Assert.True(status.Success);
        await resumed.CancelJobAsync(job.Job);
        var wait = await resumed.WaitForJobAsync(job.Job, TimeSpan.FromSeconds(5));
        Assert.Equal(
            JobStates.Canceled,
            wait.Result!.Value.GetProperty("job").GetProperty("state").GetString());
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task Cli_orchestrates_two_sample_processes_and_preserves_partial_failures()
    {
        var root = FindRepositoryRoot();
        var samplePath = FindBuiltAssembly(root, "samples", "Scry.SampleHost", "Scry.SampleHost.dll");
        var cliPath = FindBuiltAssembly(root, "src", "Scry.Cli", "scry.dll");
        var firstAlias = $"sample-a-{Guid.NewGuid():N}";
        var secondAlias = $"sample-b-{Guid.NewGuid():N}";
        await using var first = await SampleProcess.StartAsync(samplePath, firstAlias);
        await using var second = await SampleProcess.StartAsync(samplePath, secondAlias);

        var discovered = await TargetDiscovery.FindAsync();
        Assert.Contains(discovered, item => item.Descriptor.Target.TargetId == first.TargetId);
        Assert.Contains(discovered, item => item.Descriptor.Target.TargetId == second.TargetId);
        Assert.NotEqual(
            discovered.Single(item => item.Descriptor.Target.TargetId == first.TargetId).Descriptor.Target.ProcessId,
            discovered.Single(item => item.Descriptor.Target.TargetId == second.TargetId).Descriptor.Target.ProcessId);

        var delayPayload = JsonSerializer.SerializeToElement(
            new
            {
                registeredOperation = "delay",
                arguments = new { milliseconds = 800 }
            },
            ScryJson.Options);
        var concurrent = new ScenarioRequest(
            "concurrent",
            [
                new("first", "invoke", delayPayload, Target: firstAlias, CorrelationId: "scenario-first"),
                new("second", "invoke", delayPayload, Target: secondAlias, CorrelationId: "scenario-second")
            ]);
        var concurrentRun = await RunCliAsync(
            cliPath,
            "scenario",
            "--json",
            JsonSerializer.Serialize(concurrent, ScryJson.Options));

        Assert.True(concurrentRun.ExitCode == 0, concurrentRun.StandardError);
        var concurrentResult = JsonSerializer.Deserialize<ScenarioResult>(
            concurrentRun.StandardOutput,
            ScryJson.Options)!;
        Assert.True(concurrentResult.Success);
        Assert.Equal(new[] { "first", "second" }, concurrentResult.Results.Select(result => result.Id));
        Assert.Equal(
            new[] { "scenario-first", "scenario-second" },
            concurrentResult.Results.Select(result => result.Response!.CorrelationId));
        Assert.NotEqual(
            concurrentResult.Results[0].Target!.ProcessId,
            concurrentResult.Results[1].Target!.ProcessId);
        var intervals = concurrentResult.Results
            .Select(result => result.Response!.Result!.Value
                .GetProperty("value")
                .GetProperty("value")
                .GetString()!
                .Split(':')
                .Select(long.Parse)
                .ToArray())
            .ToArray();
        Assert.True(intervals[0][0] < intervals[1][1] && intervals[1][0] < intervals[0][1]);

        var partial = new ScenarioRequest(
            "sequential",
            [
                new(
                    "found",
                    "capabilities",
                    JsonSerializer.SerializeToElement(new { }, ScryJson.Options),
                    Target: firstAlias),
                new(
                    "missing",
                    "capabilities",
                    JsonSerializer.SerializeToElement(new { }, ScryJson.Options),
                    Target: $"missing-{Guid.NewGuid():N}")
            ]);
        var partialRun = await RunCliAsync(
            cliPath,
            "batch",
            "--json",
            JsonSerializer.Serialize(partial, ScryJson.Options));

        Assert.Equal(6, partialRun.ExitCode);
        var partialResult = JsonSerializer.Deserialize<ScenarioResult>(
            partialRun.StandardOutput,
            ScryJson.Options)!;
        Assert.False(partialResult.Success);
        Assert.True(partialResult.Results[0].Success);
        Assert.Equal("target_not_found", partialResult.Results[1].Error?.Code);
        Assert.Equal(new[] { 0, 1 }, partialResult.Results.Select(result => result.Index));

        var jobStart = await RunCliAsync(
            cliPath,
            "jobs",
            "start",
            "--target",
            firstAlias,
            "--correlation",
            "cli-job",
            "--json",
            """
            {"operation":"invoke","payload":{"registeredOperation":"delay","arguments":{"milliseconds":25}}}
            """);
        Assert.Equal(0, jobStart.ExitCode);
        var startResponse = JsonSerializer.Deserialize<ProtocolResponse>(
            jobStart.StandardOutput,
            ScryJson.Options)!;
        var startedJob = startResponse.Result!.Value.Deserialize<JobSnapshot>(ScryJson.Options)!;
        Assert.Equal("cli-job", startedJob.CorrelationId);

        var jobWait = await RunCliAsync(
            cliPath,
            "jobs",
            "wait",
            "--target",
            firstAlias,
            "--json",
            JsonSerializer.Serialize(new JobWaitRequest(startedJob.Job, 5000)));
        Assert.Equal(0, jobWait.ExitCode);
        var waitResponse = JsonSerializer.Deserialize<ProtocolResponse>(
            jobWait.StandardOutput,
            ScryJson.Options)!;
        var waitResult = waitResponse.Result!.Value.Deserialize<JobWaitResult>(ScryJson.Options)!;
        Assert.False(waitResult.TimedOut);
        Assert.Equal(JobStates.Succeeded, waitResult.Job.State);
    }
#endif

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

    private static string FindBuiltAssembly(
        string root,
        string parent,
        string project,
        string assemblyName)
    {
        var targetFramework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException("Could not determine the test configuration.");
        var path = Path.Combine(
            root,
            parent,
            project,
            "bin",
            configuration,
            targetFramework,
            assemblyName);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Could not locate a current integration-test assembly.", path);
    }

    private static async Task<ProcessResult> RunCliAsync(string cliPath, params string[] arguments)
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
        await process.WaitForExitAsync();
        return new(process.ExitCode, await output, await error);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class JobTestHost : IAsyncDisposable
    {
        private JobTestHost(AgentHost host)
        {
            Host = host;
        }

        public AgentHost Host { get; }

        public static JobTestHost Start(
            IReadOnlyList<string>? aliases = null,
            TimeSpan? retention = null,
            TimeSpan? sessionLease = null,
            int maximumJobs = 32,
            int maximumLogs = 32)
        {
            var host = AgentHost.Start(
                builder => builder.RegisterJobOperation(
                    "work",
                    async (arguments, context) =>
                    {
                        for (var index = 0; index < 5; index++)
                        {
                            context.Log($"log-{index}");
                        }

                        await Task.Delay(
                            arguments.GetProperty("milliseconds").GetInt32(),
                            context.CancellationToken);
                        return new { targetId = context.OperationId };
                    }),
                new AgentHostOptions
                {
                    Alias = $"job-test-{Guid.NewGuid():N}",
                    Aliases = aliases ?? [],
                    SessionLease = sessionLease ?? TimeSpan.FromMinutes(1),
                    JobRetention = retention ?? TimeSpan.FromMinutes(1),
                    MaximumJobs = maximumJobs,
                    MaximumJobLogEntries = maximumLogs
                });
            return new(host);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private sealed class SampleProcess : IAsyncDisposable
    {
        private readonly Process _process;

        private SampleProcess(Process process, string targetId, string descriptorPath)
        {
            _process = process;
            TargetId = targetId;
            DescriptorPath = descriptorPath;
        }

        public string TargetId { get; }

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
                ?? throw new InvalidOperationException("Failed to start the sample host.");
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
                        $"Sample host did not publish startup metadata: '{targetLine}' / '{descriptorLine}'.");
                }

                return new(
                    process,
                    targetLine["Target: ".Length..],
                    descriptorLine["Descriptor: ".Length..]);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                process.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync();
                await _process.StandardInput.FlushAsync();
                try
                {
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }

            _process.Dispose();
            Assert.False(File.Exists(DescriptorPath));
        }
    }
}
