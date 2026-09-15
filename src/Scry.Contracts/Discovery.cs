using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;

namespace Scry.Contracts;

public static class TargetDiscovery
{
    public static string DirectoryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Scry",
            "targets");

    public static string GetDescriptorPath(string targetId) =>
        Path.Combine(DirectoryPath, $"{targetId}.json");

    public static async Task<ConnectionDescriptor> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(Path.GetFullPath(path));
        var descriptor = await JsonSerializer.DeserializeAsync<ConnectionDescriptor>(
            stream, ScryJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Descriptor '{path}' is empty or invalid.");
        ValidateDescriptor(descriptor, path);
        return descriptor;
    }

    public static async Task<IReadOnlyList<(ConnectionDescriptor Descriptor, string Path)>> FindAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return Array.Empty<(ConnectionDescriptor Descriptor, string Path)>();
        }

        var results = new List<(ConnectionDescriptor Descriptor, string Path)>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var descriptor = await ReadAsync(path, cancellationToken).ConfigureAwait(false);

                // A descriptor carried here from another machine (a remote endpoint's file
                // copied into the local targets directory by mistake) must be left alone rather
                // than treated as a stale local rendezvous file and deleted. A null MachineName
                // is a descriptor written before this field existed; treat it exactly as before -
                // as local - so old descriptors keep today's behaviour unchanged.
                if (descriptor.MachineName is not null &&
                    !string.Equals(descriptor.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var process = Process.GetProcessById(descriptor.Target.ProcessId);
                if (!process.HasExited &&
                    process.StartTime.ToUniversalTime() == descriptor.Target.StartedAt.UtcDateTime)
                {
                    results.Add((descriptor, path));
                    continue;
                }
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Invalid and stale rendezvous files are best-effort cleanup candidates.
            }

            TryDelete(path);
        }

        return results
            .OrderBy(item => item.Descriptor.Target.Alias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Descriptor.Target.TargetId, StringComparer.Ordinal)
            .ToArray();
    }

    public static async Task<(ConnectionDescriptor Descriptor, string Path)> ResolveAsync(
        string identityOrAlias,
        CancellationToken cancellationToken = default)
    {
        var matches = (await FindAsync(cancellationToken).ConfigureAwait(false))
            .Where(item =>
                string.Equals(item.Descriptor.Target.TargetId, identityOrAlias, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Descriptor.Target.Alias, identityOrAlias, StringComparison.OrdinalIgnoreCase) ||
                item.Descriptor.Target.Aliases?.Contains(
                    identityOrAlias,
                    StringComparer.OrdinalIgnoreCase) == true)
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException($"No live Scry target matches '{identityOrAlias}'."),
            _ => throw new InvalidOperationException(
                $"Alias '{identityOrAlias}' matches multiple targets; use a target ID.")
        };
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateDescriptor(ConnectionDescriptor descriptor, string path)
    {
        if (descriptor.ProtocolVersion < 1 ||
            descriptor.Target is null ||
            string.IsNullOrWhiteSpace(descriptor.Target.TargetId) ||
            string.IsNullOrWhiteSpace(descriptor.Target.Alias) ||
            descriptor.Target.Aliases?.Any(string.IsNullOrWhiteSpace) == true ||
            descriptor.Target.ProcessId <= 0 ||
            string.IsNullOrWhiteSpace(descriptor.PipeName) ||
            string.IsNullOrWhiteSpace(descriptor.CapabilityToken))
        {
            throw new InvalidDataException($"Descriptor '{path}' is structurally invalid.");
        }

        // A half-written TCP descriptor must fail here, at read, rather than surfacing as a
        // confusing connect-time failure: the address and port are both-or-neither, and the
        // published port must be an actually-bound one (0 is only ever an input option).
        if ((descriptor.TcpAddress is null) != (descriptor.TcpPort is null))
        {
            throw new InvalidDataException(
                $"Descriptor '{path}' has a TCP address without a port, or vice versa.");
        }

        if (descriptor.TcpPort is { } port && port is < 1 or > 65535)
        {
            throw new InvalidDataException(
                $"Descriptor '{path}' has an out-of-range TCP port {port}.");
        }
    }
}
