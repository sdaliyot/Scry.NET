using System.Text.Json;
using Scry.Contracts;
using Scry.Injector;
using Scry.Sdk;

return await Cli.RunAsync(args);

internal static class Cli
{
    private const int Success = 0;
    private const int UsageError = 2;
    private const int TargetError = 3;
    private const int ConnectionError = 4;
    private const int OperationError = 5;
    private const int PartialFailure = 6;
    private const int InternalError = 70;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            CliContract.WriteHelp(null, Console.Error);
            return UsageError;
        }

        try
        {
            if (CliContract.TryGetHelpTopic(args, out var helpTopic))
            {
                CliContract.WriteHelp(helpTopic, Console.Out);
                return Success;
            }

            if (args[0].Equals("schema", StringComparison.OrdinalIgnoreCase) ||
                args[0] == "--json-schema")
            {
                if (args.Length != 1)
                {
                    throw new CliUsageException("The schema command does not accept options.");
                }

                CliContract.WriteSchema(Console.Out);
                return Success;
            }

            var (command, optionArguments) = ParseCommand(args);

            // attach takes a positional process ID or name rather than --target/--descriptor,
            // because the point is that the target has no endpoint to address yet.
            if (command == "attach")
            {
                return await AttachAsync(optionArguments).ConfigureAwait(false);
            }

            var options = ParseOptions(optionArguments);
            if (command == "discover")
            {
                if (options.Count != 0)
                {
                    throw new CliUsageException("The discover command does not accept options.");
                }

                return await DiscoverAsync().ConfigureAwait(false);
            }

            if (command is "scenario" or "batch")
            {
                EnsureOnly(options, "input", "request", "json");
                return await ScenarioAsync(options).ConfigureAwait(false);
            }

            if (!CliContract.IsEndpointOperation(command))
            {
                throw new CliUsageException($"Unknown command '{command}'.");
            }

            EnsureOnly(
                options,
                "descriptor",
                "target",
                "session",
                "input",
                "request",
                "source",
                "json",
                "correlation");
            var payload = await ReadPayloadAsync(options, command).ConfigureAwait(false);
            var descriptor = await ResolveDescriptorAsync(options).ConfigureAwait(false);
            options.TryGetValue("session", out var sessionId);
            sessionId ??= TryGetJobSession(payload);
            options.TryGetValue("correlation", out var correlationId);
            var persistentJobSession = command == "job.start";
            var request = CliContract.PrepareRequest(command, payload);
            await using var client = await ScryClient.ConnectAsync(
                descriptor,
                sessionId,
                clientName: "scry",
                ephemeralSession: sessionId is null && !persistentJobSession).ConfigureAwait(false);
            var response = await client.RequestCorrelatedAsync(
                request.Operation,
                request.Payload,
                correlationId).ConfigureAwait(false);
            response = CliContract.NormalizeResponse(command, response);
            WriteJson(response);
            return response.Success ? Success : OperationError;
        }
        catch (CliUsageException exception)
        {
            WriteError("usage_error", exception.Message);
            Console.Error.WriteLine("Run 'scry --help' or 'scry help <command>' for usage.");
            return UsageError;
        }
        catch (InjectionException exception)
        {
            var error = exception.ToError();
            WriteError(error.Code, error.Message, error.NativeError);
            return TargetError;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException or
            KeyNotFoundException or InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException)
        {
            WriteError("target_not_found", exception.Message);
            return TargetError;
        }

        catch (ScryRemoteException exception)
        {
            WriteJson(exception.Response);
            return ConnectionError;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException or ProtocolException)
        {
            WriteError("connection_failed", exception.Message);
            return ConnectionError;
        }
        catch (JsonException exception)
        {
            WriteError("invalid_json", exception.Message);
            return UsageError;
        }
        catch (Exception exception)
        {
            WriteError("internal_error", exception.Message);
            return InternalError;
        }
    }

    private static (string Command, string[] OptionArguments) ParseCommand(string[] args)
    {
        var command = args[0].ToLowerInvariant();
        if (command != "jobs")
        {
            return (command, args[1..]);
        }

        if (args.Length < 2)
        {
            throw new CliUsageException("The jobs command requires start, status, wait, cancel, or logs.");
        }

        var action = args[1].ToLowerInvariant();
        if (action is not ("start" or "status" or "wait" or "cancel" or "logs"))
        {
            throw new CliUsageException($"Unknown jobs action '{action}'.");
        }

        return ($"job.{action}", args[2..]);
    }

    private static async Task<int> AttachAsync(string[] args)
    {
        AttachArguments parsed;
        try
        {
            parsed = AttachCommandLine.Parse(args);
        }
        catch (AttachUsageException exception)
        {
            throw new CliUsageException(exception.Message);
        }

        var result = await AttachService
            .AttachAsync(parsed.Target, parsed.Alias, parsed.Adapters)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            WriteJson(result);
            return TargetError;
        }

        await using var client = await ScryClient.ConnectAsync(
            result.Descriptor!,
            clientName: "scry attach",
            ephemeralSession: true).ConfigureAwait(false);
        WriteJson(new
        {
            success = true,
            target = result.Target,
            descriptorPath = result.DescriptorPath,
            handshake = client.Handshake
        });
        return Success;
    }

    private static async Task<int> DiscoverAsync()
    {
        var targets = await TargetDiscovery.FindAsync().ConfigureAwait(false);
        var result = new
        {
            protocolVersion = ProtocolConstants.Version,
            targets = targets.Select(item => new DiscoveredTarget(
                item.Descriptor.ProtocolVersion,
                item.Descriptor.Target,
                item.Path,
                item.Descriptor.PublishedAt))
        };
        WriteJson(result);
        return Success;
    }

    private static async Task<int> ScenarioAsync(IReadOnlyDictionary<string, string> options)
    {
        var payload = await ReadPayloadAsync(options).ConfigureAwait(false);
        var scenario = payload.Deserialize<ScenarioRequest>(ScryJson.Options)
            ?? throw new CliUsageException("The scenario request is missing.");
        if (string.IsNullOrWhiteSpace(scenario.Mode) || scenario.Commands is null)
        {
            throw new CliUsageException("Scenario mode and commands are required.");
        }

        var mode = scenario.Mode.ToLowerInvariant();
        if (mode is not ("sequential" or "concurrent"))
        {
            throw new CliUsageException("Scenario mode must be 'sequential' or 'concurrent'.");
        }

        if (scenario.Commands.Count == 0)
        {
            throw new CliUsageException("A scenario must contain at least one command.");
        }

        if (scenario.Commands.Any(command =>
                command is null ||
                string.IsNullOrWhiteSpace(command.Id) ||
                string.IsNullOrWhiteSpace(command.Operation) ||
                command.Payload.ValueKind != JsonValueKind.Object ||
                (command.Target is null) == (command.Descriptor is null)) ||
            scenario.Commands.Select(command => command.Id).Distinct(StringComparer.Ordinal).Count() !=
                scenario.Commands.Count)
        {
            throw new CliUsageException(
                "Scenario command IDs and operations are required, IDs must be unique, payloads must be objects, " +
                "and each command must select exactly one target or descriptor.");
        }

        ScenarioCommandResult[] results;
        if (mode == "concurrent")
        {
            results = await Task.WhenAll(
                scenario.Commands.Select((command, index) => ExecuteScenarioCommandAsync(command, index)))
                .ConfigureAwait(false);
        }
        else
        {
            results = new ScenarioCommandResult[scenario.Commands.Count];
            for (var index = 0; index < scenario.Commands.Count; index++)
            {
                results[index] = await ExecuteScenarioCommandAsync(scenario.Commands[index], index)
                    .ConfigureAwait(false);
            }
        }

        var result = new ScenarioResult(
            ProtocolConstants.Version,
            mode,
            results.All(item => item.Success),
            results);
        WriteJson(result);
        return result.Success ? Success : PartialFailure;
    }

    private static async Task<ScenarioCommandResult> ExecuteScenarioCommandAsync(
        ScenarioCommand command,
        int index)
    {
        ConnectionDescriptor? descriptor = null;
        var selector = command.Target ?? command.Descriptor;
        try
        {
            descriptor = command.Descriptor is { } path
                ? await TargetDiscovery.ReadAsync(path).ConfigureAwait(false)
                : (await TargetDiscovery.ResolveAsync(command.Target!).ConfigureAwait(false)).Descriptor;
            var sessionId = command.SessionId ?? TryGetJobSession(command.Payload);
            await using var client = await ScryClient.ConnectAsync(
                descriptor,
                sessionId,
                clientName: "scry-scenario",
                ephemeralSession: sessionId is null && command.Operation != "job.start").ConfigureAwait(false);
            var request = CliContract.PrepareRequest(command.Operation, command.Payload);
            var response = await client.RequestCorrelatedAsync(
                request.Operation,
                request.Payload,
                command.CorrelationId).ConfigureAwait(false);
            response = CliContract.NormalizeResponse(command.Operation, response);
            return new(
                command.Id,
                index,
                command.Operation,
                selector,
                descriptor.Target,
                response,
                null);
        }
        catch (ScryRemoteException exception)
        {
            return new(
                command.Id,
                index,
                command.Operation,
                selector,
                descriptor?.Target,
                exception.Response,
                null);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException or
            InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            return FailedScenarioCommand(
                command,
                index,
                selector,
                descriptor?.Target,
                "target_not_found",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException or ProtocolException)
        {
            return FailedScenarioCommand(
                command,
                index,
                selector,
                descriptor?.Target,
                "connection_failed",
                exception);
        }
        catch (Exception exception)
        {
            return FailedScenarioCommand(
                command,
                index,
                selector,
                descriptor?.Target,
                "internal_error",
                exception);
        }
    }

    private static ScenarioCommandResult FailedScenarioCommand(
        ScenarioCommand command,
        int index,
        string? selector,
        TargetMetadata? target,
        string code,
        Exception exception) =>
        new(
            command.Id,
            index,
            command.Operation,
            selector,
            target,
            null,
            new(code, exception.Message));

    private static async Task<ConnectionDescriptor> ResolveDescriptorAsync(
        IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("descriptor", out var path))
        {
            return await TargetDiscovery.ReadAsync(path).ConfigureAwait(false);
        }

        if (options.TryGetValue("target", out var target))
        {
            return (await TargetDiscovery.ResolveAsync(target).ConfigureAwait(false)).Descriptor;
        }

        throw new CliUsageException("Specify exactly one of --descriptor <path> or --target <id-or-alias>.");
    }

    private static async Task<JsonElement> ReadPayloadAsync(
        IReadOnlyDictionary<string, string> options,
        string? command = null)
    {
        var isExecution = command is "evaluate" or "execute";
        if (options.TryGetValue("source", out var sourcePath))
        {
            if (!isExecution)
            {
                throw new CliUsageException("--source is only valid for evaluate and execute.");
            }

            var source = await ReadTextAsync(sourcePath, "source").ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new { source }, ScryJson.Options);
        }

        string json;
        if (options.TryGetValue("request", out var requestPath) ||
            options.TryGetValue("input", out requestPath))
        {
            json = await ReadTextAsync(requestPath, "request").ConfigureAwait(false);
        }
        else if (options.TryGetValue("json", out var inline))
        {
            if (isExecution)
            {
                throw new CliUsageException(
                    "Use --source, --request, or redirected stdin for C# execution.");
            }

            json = inline;
        }
        else if (Console.IsInputRedirected)
        {
            json = await Console.In.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                json = isExecution
                    ? throw new CliUsageException("Execution source from stdin cannot be empty.")
                    : "{}";
            }

            if (isExecution)
            {
                return JsonSerializer.SerializeToElement(new { source = json }, ScryJson.Options);
            }
        }
        else
        {
            json = "{}";
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new CliUsageException("Request JSON must be an object.");
        }

        return document.RootElement.Clone();
    }

    private static async Task<string> ReadTextAsync(string path, string kind)
    {
        try
        {
            return path == "-"
                ? await Console.In.ReadToEndAsync().ConfigureAwait(false)
                : await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new CliUsageException($"Could not read {kind} input '{path}': {exception.Message}");
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new CliUsageException($"Expected --name value, found '{argument}'.");
            }

            var name = argument[2..];
            if (!options.TryAdd(name, args[++index]))
            {
                throw new CliUsageException($"Option '--{name}' was specified more than once.");
            }
        }

        if (options.ContainsKey("descriptor") && options.ContainsKey("target"))
        {
            throw new CliUsageException("Use either --descriptor or --target, not both.");
        }

        var payloadOptions = new[] { "input", "request", "source", "json" }
            .Count(options.ContainsKey);
        if (payloadOptions > 1)
        {
            throw new CliUsageException("Use only one of --request, --input, --source, or --json.");
        }

        return options;
    }

    private static string? TryGetJobSession(JsonElement payload)
    {
        var job = payload.EnumerateObject()
            .FirstOrDefault(property =>
                string.Equals(property.Name, "job", StringComparison.OrdinalIgnoreCase))
            .Value;
        if (job.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var sessionId = job.EnumerateObject()
            .FirstOrDefault(property =>
                string.Equals(property.Name, "sessionId", StringComparison.OrdinalIgnoreCase))
            .Value;
        return sessionId.ValueKind == JsonValueKind.String ? sessionId.GetString() : null;
    }

    private static void EnsureOnly(
        IReadOnlyDictionary<string, string> options,
        params string[] names)
    {
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var unknown = options.Keys.FirstOrDefault(name => !allowed.Contains(name));
        if (unknown is not null)
        {
            throw new CliUsageException($"Unknown option '--{unknown}'.");
        }
    }

    private static void WriteJson<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, ScryJson.Options));

    private static void WriteError(string code, string message, int? nativeError = null) =>
        WriteJson(new { success = false, error = new { code, message, nativeError } });

}
