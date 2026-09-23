using System.Diagnostics;
using System.Text.Json;
using Scry.Contracts;
using Scry.Endpoint;

namespace Scry.Tests;

// Every test here shells out to the scry CLI, which is modern-.NET only, and uses
// ProcessStartInfo.ArgumentList and Process.WaitForExitAsync - neither of which exists on
// .NET Framework. Scope it to the modern leg, like the other CLI-driving tests here.
#if !NETFRAMEWORK
[Collection("Scry integration")]
public sealed class CliContractTests
{
    [Fact]
    public async Task Help_and_schema_describe_the_real_command_contract()
    {
        var cliPath = FindBuiltCli();

        var topLevelHelp = await RunCliAsync(cliPath, ["--help"]);
        Assert.Equal(0, topLevelHelp.ExitCode);
        Assert.Empty(topLevelHelp.StandardError);
        Assert.Contains("scry schema", topLevelHelp.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("wpf.snapshot", topLevelHelp.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("winforms.snapshot", topLevelHelp.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Exit codes: 0 success", topLevelHelp.StandardOutput, StringComparison.Ordinal);

        var commandHelp = await RunCliAsync(cliPath, ["jobs", "wait", "--help"]);
        Assert.Equal(0, commandHelp.ExitCode);
        Assert.Contains(
            "Usage: scry jobs wait",
            commandHelp.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("timeoutMilliseconds", commandHelp.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("operationId", commandHelp.StandardOutput, StringComparison.Ordinal);

        var versionRun = await RunCliAsync(cliPath, ["version"]);
        Assert.Equal(0, versionRun.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(versionRun.StandardOutput));

        var versionFlagRun = await RunCliAsync(cliPath, ["--version"]);
        Assert.Equal(0, versionFlagRun.ExitCode);
        Assert.Equal(versionRun.StandardOutput, versionFlagRun.StandardOutput);

        var schemaRun = await RunCliAsync(cliPath, ["schema"]);
        Assert.Equal(0, schemaRun.ExitCode);
        using var schema = JsonDocument.Parse(schemaRun.StandardOutput);
        var root = schema.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ProtocolConstants.Version, root.GetProperty("protocolVersion").GetInt32());

        var operations = root.GetProperty("commands")
            .EnumerateArray()
            .Where(command => command.TryGetProperty("operation", out _))
            .Select(command => command.GetProperty("operation").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var expectedOperations = ProtocolConstants.CoreCapabilities
            .Concat(
            [
                "wpf.snapshot",
                "wpf.wait",
                "wpf.assert",
                "wpf.screenshot",
                "winforms.snapshot",
                "winforms.wait",
                "winforms.assert",
                "winforms.screenshot",
                "appdomain.list",
                "appdomain.start"
            ])
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expectedOperations, operations);

        var documentedExitCodes = root.GetProperty("exitCodes")
            .EnumerateArray()
            .Select(item => item.GetProperty("code").GetInt32());
        Assert.Equal([0, 2, 3, 4, 5, 6, 70], documentedExitCodes);
    }

    [Fact]
    public async Task Exit_codes_and_envelopes_distinguish_validation_target_and_operation_failures()
    {
        var cliPath = FindBuiltCli();
        await using var host = EndpointHost.Start(
            builder => builder
                .RegisterOperation(
                    "fail",
                    _ => throw new InvalidOperationException("Expected CLI operation failure."))
                .RegisterOperation(
                    "wpf.snapshot",
                    arguments => JsonSerializer.SerializeToElement(
                        new
                        {
                            requestedRoot = arguments.TryGetProperty("root", out var root)
                                ? root.GetString()
                                : null
                        },
                        ScryJson.Options)),
            new EndpointOptions { Alias = $"cli-contract-{Guid.NewGuid():N}" });

        var success = await RunCliAsync(
            cliPath,
            ["capabilities", "--descriptor", host.DescriptorPath, "--correlation", "cli-success"]);
        Assert.Equal(0, success.ExitCode);
        using (var response = JsonDocument.Parse(success.StandardOutput))
        {
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(
                response.RootElement.GetProperty("operationId").GetString()));
            Assert.Equal(
                "cli-success",
                response.RootElement.GetProperty("correlationId").GetString());
        }

        var validation = await RunCliAsync(cliPath, ["inspect", "--target"]);
        Assert.Equal(2, validation.ExitCode);
        using (var response = JsonDocument.Parse(validation.StandardOutput))
        {
            Assert.False(response.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(
                "usage_error",
                response.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        var missingTarget = await RunCliAsync(
            cliPath,
            ["roots", "--target", $"missing-{Guid.NewGuid():N}"]);
        Assert.Equal(3, missingTarget.ExitCode);
        using (var response = JsonDocument.Parse(missingTarget.StandardOutput))
        {
            Assert.Equal(
                "target_not_found",
                response.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        var operationFailure = await RunCliAsync(
            cliPath,
            ["invoke", "--descriptor", host.DescriptorPath],
            """{"registeredOperation":"fail","arguments":{}}""");
        Assert.Equal(5, operationFailure.ExitCode);
        using (var response = JsonDocument.Parse(operationFailure.StandardOutput))
        {
            Assert.False(response.RootElement.GetProperty("success").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(
                response.RootElement.GetProperty("operationId").GetString()));
            Assert.Equal(
                "operation_failed",
                response.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        var executionFailure = await RunCliAsync(
            cliPath,
            ["evaluate", "--descriptor", host.DescriptorPath],
            "this is not valid C#");
        Assert.Equal(5, executionFailure.ExitCode);
        using (var response = JsonDocument.Parse(executionFailure.StandardOutput))
        {
            Assert.Equal(
                "compilation_failed",
                response.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.True(
                response.RootElement.GetProperty("error").GetProperty("diagnostics").GetArrayLength() > 0);
        }

        var adapterOperation = await RunCliAsync(
            cliPath,
            ["wpf.snapshot", "--descriptor", host.DescriptorPath],
            """{"root":"main"}""");
        Assert.Equal(0, adapterOperation.ExitCode);
        using var adapterResponse = JsonDocument.Parse(adapterOperation.StandardOutput);
        Assert.True(adapterResponse.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            "main",
            adapterResponse.RootElement.GetProperty("result")
                .GetProperty("requestedRoot")
                .GetString());

        var scenarioPayload = JsonSerializer.Serialize(
            new
            {
                mode = "sequential",
                commands = new[]
                {
                    new
                    {
                        id = "adapter",
                        descriptor = host.DescriptorPath,
                        operation = "wpf.snapshot",
                        payload = new { root = "main" }
                    }
                }
            },
            ScryJson.Options);
        var adapterScenario = await RunCliAsync(cliPath, ["scenario"], scenarioPayload);
        Assert.Equal(0, adapterScenario.ExitCode);
        using var scenarioResponse = JsonDocument.Parse(adapterScenario.StandardOutput);
        Assert.True(scenarioResponse.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            "wpf.snapshot",
            scenarioResponse.RootElement.GetProperty("results")[0]
                .GetProperty("operation")
                .GetString());
        Assert.Equal(
            "main",
            scenarioResponse.RootElement.GetProperty("results")[0]
                .GetProperty("response")
                .GetProperty("result")
                .GetProperty("requestedRoot")
                .GetString());

        var requestAliasScenario = await RunCliAsync(
            cliPath,
            ["scenario", "--request", "-"],
            scenarioPayload);
        Assert.Equal(0, requestAliasScenario.ExitCode);

        var adapterJobStart = await RunCliAsync(
            cliPath,
            ["jobs", "start", "--descriptor", host.DescriptorPath],
            """{"operation":"wpf.snapshot","payload":{"root":"main"}}""");
        Assert.Equal(0, adapterJobStart.ExitCode);
        var startResponse = JsonSerializer.Deserialize<ProtocolResponse>(
            adapterJobStart.StandardOutput,
            ScryJson.Options)!;
        var job = startResponse.Result!.Value.Deserialize<JobSnapshot>(ScryJson.Options)!.Job;
        var adapterJobWait = await RunCliAsync(
            cliPath,
            ["jobs", "wait", "--descriptor", host.DescriptorPath],
            JsonSerializer.Serialize(new JobWaitRequest(job, 5000), ScryJson.Options));
        Assert.Equal(0, adapterJobWait.ExitCode);
        var waitResponse = JsonSerializer.Deserialize<ProtocolResponse>(
            adapterJobWait.StandardOutput,
            ScryJson.Options)!;
        var completedJob = waitResponse.Result!.Value
            .Deserialize<JobWaitResult>(ScryJson.Options)!.Job;
        Assert.Equal(JobStates.Succeeded, completedJob.State);
        Assert.Equal(
            "main",
            completedJob.Result!.Value.GetProperty("value")
                .GetProperty("value")
                .GetProperty("requestedRoot")
                .GetString());
    }

    /// <summary>
    /// The schema is what an agent is told to trust for request fields, so a field that exists on
    /// the wire but is missing from the schema is not a documentation nit - it makes a working
    /// capability invisible. That happened: <c>marshal</c> was absent from evaluate and execute
    /// while being present on assert and wait, which told agents those two submissions could not
    /// reach the UI thread, and therefore could not touch a DependencyObject or a Control at all.
    /// Comparing against the contract type catches the next omission instead of a reader doing it.
    /// </summary>
    [Fact]
    public async Task Schema_documents_every_execution_request_field()
    {
        var schemaRun = await RunCliAsync(FindBuiltCli(), ["schema"]);
        Assert.Equal(0, schemaRun.ExitCode);

        var expected = typeof(ExecutionRequest)
            .GetProperties()
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .ToHashSet(StringComparer.Ordinal);

        using var schema = JsonDocument.Parse(schemaRun.StandardOutput);
        var commands = schema.RootElement.GetProperty("commands").EnumerateArray().ToArray();

        foreach (var name in new[] { "evaluate", "execute" })
        {
            var command = commands.Single(candidate =>
                candidate.TryGetProperty("operation", out var operation) &&
                operation.GetString() == name);
            var documented = command.GetProperty("request")
                .GetProperty("fields")
                .EnumerateArray()
                .Select(field => field.GetProperty("name").GetString()!)
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(
                expected.SetEquals(documented),
                $"'scry {name}' documents {string.Join(", ", documented.OrderBy(x => x))} but " +
                $"ExecutionRequest carries {string.Join(", ", expected.OrderBy(x => x))}.");
        }
    }

    /// <summary>
    /// <c>AttachCommandLine.Usage</c> and the schema's <c>attach</c> command fields are two
    /// independent, hand-written descriptions of the same option set - nothing keeps them in sync
    /// mechanically. Added alongside <c>--targets-dir</c>, whose usage string and <c>CliField</c>
    /// entry are exactly the kind of pair this drift could silently separate.
    /// </summary>
    [Fact]
    public async Task Every_attach_option_in_the_usage_string_is_also_a_documented_field()
    {
        var schemaRun = await RunCliAsync(FindBuiltCli(), ["schema"]);
        Assert.Equal(0, schemaRun.ExitCode);

        using var schema = JsonDocument.Parse(schemaRun.StandardOutput);
        var attach = schema.RootElement.GetProperty("commands")
            .EnumerateArray()
            .Single(command => command.GetProperty("command").GetString() == "attach");
        var usage = attach.GetProperty("usage").GetString()!;
        var documented = attach.GetProperty("request")
            .GetProperty("fields")
            .EnumerateArray()
            .Select(field => field.GetProperty("name").GetString()!)
            .Where(name => name.StartsWith("--", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var inUsage = System.Text.RegularExpressions.Regex.Matches(usage, "--[a-z-]+")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            inUsage.SetEquals(documented),
            $"attach usage names {string.Join(", ", inUsage.OrderBy(x => x))} but the schema " +
            $"documents {string.Join(", ", documented.OrderBy(x => x))}.");
    }

    private static string FindBuiltCli()
    {
        var root = FindRepositoryRoot();
        var targetFramework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException("Could not determine the test configuration.");
        var path = Path.Combine(
            root,
            "src",
            "Scry.Cli",
            "bin",
            configuration,
            targetFramework,
            "scry.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Could not locate a current CLI assembly.", path);
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

    private static async Task<ProcessResult> RunCliAsync(
        string cliPath,
        IReadOnlyList<string> arguments,
        string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = standardInput is not null,
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
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await output, await error);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
#endif
