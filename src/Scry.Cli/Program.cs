using System.Text.Json;
using Scry.Contracts;
using Scry.Sdk;

return await Cli.RunAsync(args);

internal static class Cli
{
    private const int Success = 0;
    private const int UsageError = 2;
    private const int TargetError = 3;
    private const int ConnectionError = 4;
    private const int OperationError = 5;
    private const int InternalError = 70;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            WriteUsage();
            return args.Length == 0 ? UsageError : Success;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args[1..]);
            if (command == "discover")
            {
                if (options.Count != 0)
                {
                    throw new CliUsageException("The discover command does not accept options.");
                }

                return await DiscoverAsync().ConfigureAwait(false);
            }

            if (!ProtocolConstants.CoreCapabilities.Contains(command, StringComparer.Ordinal) ||
                command == "capabilities" && options.ContainsKey("input"))
            {
                throw new CliUsageException($"Unknown or invalid command '{command}'.");
            }

            var payload = await ReadPayloadAsync(command, options).ConfigureAwait(false);
            var descriptor = await ResolveDescriptorAsync(options).ConfigureAwait(false);
            options.TryGetValue("session", out var sessionId);
            await using var client = await ScryClient.ConnectAsync(
                descriptor,
                sessionId,
                clientName: "scry",
                ephemeralSession: sessionId is null).ConfigureAwait(false);
            var response = await client.RequestAsync(command, payload).ConfigureAwait(false);
            WriteJson(response);
            return response.Success ? Success : OperationError;
        }
        catch (CliUsageException exception)
        {
            WriteError("usage_error", exception.Message);
            WriteUsage();
            return UsageError;
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
        string command,
        IReadOnlyDictionary<string, string> options)
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
            if (name is not ("descriptor" or "target" or "session" or "input" or "request" or "source" or "json"))
            {
                throw new CliUsageException($"Unknown option '--{name}'.");
            }

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

    private static void WriteJson<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, ScryJson.Options));

    private static void WriteError(string code, string message) =>
        WriteJson(new { success = false, error = new { code, message } });

    private static void WriteUsage()
    {
        Console.Error.WriteLine(
            """
            Usage:
              scry discover
              scry <capabilities|roots|inspect|get|set|invoke|enumerate|release|
                    evaluate|execute|load-assembly|list-assemblies|find-types|describe-type>
                   (--descriptor <path> | --target <id-or-alias>)
                   [--session <id>] [--request <file|-> | --source <file|->]

            Capability tokens are read from local descriptor files and are never accepted as arguments.
            Redirected stdin is C# source for evaluate/execute and request JSON for other operations.
            """);
    }

    private sealed class CliUsageException(string message) : Exception(message);
}
