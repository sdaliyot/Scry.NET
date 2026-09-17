using System.Runtime.InteropServices;
using System.Text;
using System.Reflection;
using Scry.Endpoint;

namespace Scry.Injector.Payload;

public static class InjectedEndpointEntryPoint
{
    private static EndpointHost? _host;
    private static int _started;

    public static int StartForNetFramework(string argument)
    {
#if NETFRAMEWORK
        AppDomain.CurrentDomain.AssemblyResolve += ResolvePayloadAssembly;
#endif
        return Start(argument);
    }

#if !NETFRAMEWORK
    [UnmanagedCallersOnly]
    public static int StartForCoreClr(nint argument, int argumentLength)
    {
        var value = argument == 0 || argumentLength <= 0
            ? string.Empty
            : Marshal.PtrToStringUTF8(argument, argumentLength) ?? string.Empty;
        return Start(value);
    }
#endif

    private static int Start(string argument)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return 2;
        }

        try
        {
            var configuration = ParseConfiguration(argument);
            string? appDomainWarning = null;

#if NETFRAMEWORK
            // A requested AppDomain hop is tried before starting locally: if it succeeds, the
            // endpoint now lives in that other domain and there is nothing more to do here. A
            // selector that could not be honoured (matched none, matched several, or lost the
            // hop itself) is not a hard failure - the native injection that got this far cannot be
            // retried without recycling the target - so it falls through to the ordinary local
            // start below, carrying the reason as a warning rather than silently succeeding.
            if (!string.IsNullOrWhiteSpace(configuration.AppDomain) &&
                AppDomainHop.TryHop(
                    configuration.AppDomain!,
                    configuration.Alias,
                    configuration.TcpPort,
                    configuration.TargetsDirectory,
                    Path.GetDirectoryName(typeof(InjectedEndpointEntryPoint).Assembly.Location)!,
                    out appDomainWarning))
            {
                return 0;
            }
#endif

            _host = EndpointHost.Start(
                configure: builder =>
                {
                    DesktopAdapterWiring.Apply(builder, configuration.Adapters);
#if NETFRAMEWORK
                    AppDomainOperationWiring.Apply(
                        builder, () => _host!.Metadata, () => _host!.DescriptorPath);
#endif
                },
                options: BuildOptions(configuration, appDomainWarning));
            return 0;
        }
        catch (Exception exception)
        {
            TryWriteFailure(ParseConfiguration(argument).ErrorPath, exception);
            Volatile.Write(ref _started, 0);
            return 1;
        }
    }

    private static BootstrapConfiguration ParseConfiguration(string argument)
    {
        string? alias = null;
        string? errorPath = null;
        string? adapters = null;
        int? tcpPort = null;
        string? targetsDirectory = null;
        string? appDomain = null;
        foreach (var line in argument.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            var name = line.Substring(0, separator);
            var value = Decode(line.Substring(separator + 1));
            if (name.Equals("alias", StringComparison.OrdinalIgnoreCase))
            {
                alias = value;
            }
            else if (name.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                errorPath = value;
            }
            else if (name.Equals("adapters", StringComparison.OrdinalIgnoreCase))
            {
                adapters = value;
            }
            else if (name.Equals("tcpPort", StringComparison.OrdinalIgnoreCase))
            {
                // This method is also called a second time from inside the catch in Start, purely
                // to recover ErrorPath for the failure diagnostic - a throw here would lose that
                // diagnostic entirely and leave the attach failing with no trace anywhere. So a
                // malformed or out-of-range value becomes null (no TCP listener) rather than an
                // exception; RuntimeHostOptions.TcpPort validates the range and reports any real
                // problem when EndpointHost.Start actually runs, where TryWriteFailure can record it.
                tcpPort = int.TryParse(value, out var parsed) ? parsed : (int?)null;
            }
            else if (name.Equals("targets", StringComparison.OrdinalIgnoreCase))
            {
                targetsDirectory = value;
            }
            else if (name.Equals("appdomain", StringComparison.OrdinalIgnoreCase))
            {
                appDomain = value;
            }
        }

        return new(alias, errorPath, adapters, tcpPort, targetsDirectory, appDomain);
    }

    private static EndpointOptions BuildOptions(
        BootstrapConfiguration configuration, string? appDomainSelectionWarning) =>
        // EndpointOptions is a plain class (no "with" support), and its Alias property already
        // supplies its own default when left unset, so the branch is only on which initializer to
        // use - every other field is threaded through unconditionally on both.
        configuration.Alias is null
            ? new EndpointOptions
            {
                TcpPort = configuration.TcpPort,
                TargetsDirectory = configuration.TargetsDirectory,
                AppDomainSelectionWarning = appDomainSelectionWarning
            }
            : new EndpointOptions
            {
                Alias = configuration.Alias,
                TcpPort = configuration.TcpPort,
                TargetsDirectory = configuration.TargetsDirectory,
                AppDomainSelectionWarning = appDomainSelectionWarning
            };

    private static string? Decode(string encoded)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void TryWriteFailure(string? path, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, exception.ToString());
        }
        catch (Exception writeException) when (
            writeException is IOException or UnauthorizedAccessException)
        {
        }
    }

#if NETFRAMEWORK
    private static Assembly? ResolvePayloadAssembly(object? sender, ResolveEventArgs arguments)
    {
        var name = new AssemblyName(arguments.Name).Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(typeof(InjectedEndpointEntryPoint).Assembly.Location);
        var path = Path.Combine(directory!, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
#endif

    private sealed record BootstrapConfiguration(
        string? Alias,
        string? ErrorPath,
        string? Adapters,
        int? TcpPort = null,
        string? TargetsDirectory = null,
        string? AppDomain = null);
}
