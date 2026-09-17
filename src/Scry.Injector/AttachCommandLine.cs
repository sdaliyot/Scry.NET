namespace Scry.Injector;

/// <summary>
/// Parses the attach command's arguments. Shared by the <c>scry attach</c> subcommand and the
/// standalone <c>scry-injector</c> executable so both accept exactly the same syntax and reject the
/// same mistakes.
/// </summary>
public static class AttachCommandLine
{
    /// <summary>Adapter selections accepted by <c>--adapters</c>.</summary>
    public static readonly string[] AdapterValues = ["wpf", "winforms", "none"];

    public const string Usage =
        "<pid|process-name> [--alias <name>] [--adapters wpf|winforms|none] [--tcp-port <port|0>] " +
        "[--targets-dir <path>] [--appdomain <id|name|auto>]";

    public static AttachArguments Parse(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new AttachUsageException("The attach command requires a process ID or process name.");
        }

        string? alias = null;
        string? adapters = null;
        int? tcpPort = null;
        string? targetsDirectory = null;
        string? appDomain = null;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--alias":
                    alias = RequireValue(args, ref index, "--alias");
                    break;
                case "--adapters":
                    adapters = RequireValue(args, ref index, "--adapters");
                    if (!AdapterValues.Contains(adapters, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new AttachUsageException(
                            $"Unknown --adapters value '{adapters}'. Expected one of: " +
                            string.Join(", ", AdapterValues) + ".");
                    }

                    break;
                case "--tcp-port":
                    var rawPort = RequireValue(args, ref index, "--tcp-port");
                    if (!int.TryParse(rawPort, out var parsedPort) || parsedPort is < 0 or > 65535)
                    {
                        throw new AttachUsageException(
                            $"--tcp-port expects 0-65535; found '{rawPort}'.");
                    }

                    tcpPort = parsedPort;
                    break;
                case "--targets-dir":
                    var rawDirectory = RequireValue(args, ref index, "--targets-dir");

                    // Rejected here, at parse time, rather than left to surface 15 seconds later
                    // as a startup timeout once the target has already failed to publish.
                    if (!Path.IsPathRooted(rawDirectory))
                    {
                        throw new AttachUsageException(
                            $"--targets-dir requires an absolute path; found '{rawDirectory}'.");
                    }

                    targetsDirectory = rawDirectory;
                    break;
                case "--appdomain":
                    appDomain = RequireValue(args, ref index, "--appdomain");
                    if (string.IsNullOrWhiteSpace(appDomain))
                    {
                        throw new AttachUsageException("--appdomain requires a non-empty value.");
                    }

                    break;
                default:
                    throw new AttachUsageException($"Unknown attach option '{args[index]}'.");
            }
        }

        return new(args[0], alias, adapters, tcpPort, targetsDirectory, appDomain);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new AttachUsageException($"{option} requires a value.");
        }

        return args[++index];
    }
}

public sealed record AttachArguments(
    string Target,
    string? Alias,
    string? Adapters,
    int? TcpPort = null,
    string? TargetsDirectory = null,
    string? AppDomain = null);

public sealed class AttachUsageException(string message) : Exception(message);
