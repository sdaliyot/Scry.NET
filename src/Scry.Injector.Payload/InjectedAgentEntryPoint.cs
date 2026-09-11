using System.Runtime.InteropServices;
using System.Text;
using System.Reflection;
using Scry.Sdk;

namespace Scry.Injector.Payload;

public static class InjectedAgentEntryPoint
{
    private static AgentHost? _host;
    private static int _started;

    public static int StartForNetFramework(string argument)
    {
#if NETFRAMEWORK
        AppDomain.CurrentDomain.AssemblyResolve += ResolvePayloadAssembly;
#endif
        return Start(argument);
    }

#if NET9_0_OR_GREATER
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
            _host = AgentHost.Start(
                configure: builder => DesktopAdapterWiring.Apply(builder, configuration.Adapters),
                options: configuration.Alias is null
                    ? null
                    : new AgentHostOptions { Alias = configuration.Alias });
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
        }

        return new(alias, errorPath, adapters);
    }

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

        var directory = Path.GetDirectoryName(typeof(InjectedAgentEntryPoint).Assembly.Location);
        var path = Path.Combine(directory!, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
#endif

    private sealed record BootstrapConfiguration(string? Alias, string? ErrorPath, string? Adapters);
}
