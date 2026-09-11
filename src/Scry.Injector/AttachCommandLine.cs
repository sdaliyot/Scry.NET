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

    public const string Usage = "<pid|process-name> [--alias <name>] [--adapters wpf|winforms|none]";

    public static AttachArguments Parse(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new AttachUsageException("The attach command requires a process ID or process name.");
        }

        string? alias = null;
        string? adapters = null;
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
                default:
                    throw new AttachUsageException($"Unknown attach option '{args[index]}'.");
            }
        }

        return new(args[0], alias, adapters);
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

public sealed record AttachArguments(string Target, string? Alias, string? Adapters);

public sealed class AttachUsageException(string message) : Exception(message);
