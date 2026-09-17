using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;

namespace Scry.Contracts;

public static class TargetDiscovery
{
    /// <summary>
    /// The default rendezvous directory. Can resolve to a <em>relative</em> path - <c>Scry\targets</c>
    /// - when <see cref="Environment.SpecialFolder.LocalApplicationData"/> returns an empty string,
    /// which happens under an identity with no loaded user profile (an IIS application pool with
    /// <c>loadUserProfile="false"</c>, or a service account). Callers that need a guaranteed-rooted
    /// path should go through <see cref="ResolveDirectory"/> instead of reading this directly.
    /// </summary>
    public static string DirectoryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Scry",
            "targets");

    /// <summary>
    /// Resolves the rendezvous directory a caller should actually use: <paramref name="directory"/>
    /// made absolute when supplied, otherwise <see cref="DirectoryPath"/> validated to be rooted.
    /// This is the one place that validation happens, so every directory-dependent member below
    /// funnels through it rather than repeating the check.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="directory"/> was supplied but is not an absolute path.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No directory was supplied and <see cref="DirectoryPath"/> resolved to a relative path -
    /// meaning <see cref="Environment.SpecialFolder.LocalApplicationData"/> returned an empty
    /// string, which happens under an identity with no loaded user profile.
    /// </exception>
    public static string ResolveDirectory(string? directory)
    {
        if (directory is not null)
        {
            if (!Path.IsPathRooted(directory))
            {
                throw new ArgumentException(
                    $"The Scry rendezvous directory '{directory}' must be an absolute path.",
                    nameof(directory));
            }

            return Path.GetFullPath(directory);
        }

        var defaultPath = DirectoryPath;
        if (!Path.IsPathRooted(defaultPath))
        {
            throw new InvalidOperationException(
                $"The Scry rendezvous directory resolved to the relative path '{defaultPath}' and " +
                "must be absolute. Environment.SpecialFolder.LocalApplicationData returned an " +
                "empty path, which happens when a process runs under an identity with no loaded " +
                "user profile - an IIS application pool with loadUserProfile=\"false\", or a " +
                "service account. Set RuntimeHostOptions.TargetsDirectory " +
                "(scry attach --targets-dir <path>) to an absolute directory both the target's " +
                "identity and the tooling can use.");
        }

        return defaultPath;
    }

    public static string GetDescriptorPath(string targetId, string? directory = null) =>
        Path.Combine(ResolveDirectory(directory), $"{targetId}.json");

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
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedDirectory = ResolveDirectory(directory);
        if (!Directory.Exists(resolvedDirectory))
        {
            return Array.Empty<(ConnectionDescriptor Descriptor, string Path)>();
        }

        var results = new List<(ConnectionDescriptor Descriptor, string Path)>();
        foreach (var path in Directory.EnumerateFiles(resolvedDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Only a file whose name matches a descriptor's own naming (a bare 32-character hex
            // GUID, see RuntimeHost's targetId) is treated as rendezvous state at all. A caller-
            // chosen directory can otherwise contain unrelated *.json files - config, appsettings -
            // that must never be parsed as a descriptor or deleted as a stale one below.
            if (!IsDescriptorFileName(Path.GetFileNameWithoutExtension(path)))
            {
                continue;
            }

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
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        var matches = (await FindAsync(directory, cancellationToken).ConfigureAwait(false))
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

    /// <summary>
    /// True when <paramref name="fileNameWithoutExtension"/> has the exact shape of a descriptor's
    /// name: <c>Guid.NewGuid().ToString("N")</c> - 32 lowercase hexadecimal characters, no dashes.
    /// Anything else is left alone by <see cref="FindAsync"/>, so a caller-chosen rendezvous
    /// directory that also holds unrelated JSON is never parsed or deleted.
    /// </summary>
    private static bool IsDescriptorFileName(string fileNameWithoutExtension)
    {
        if (fileNameWithoutExtension.Length != 32)
        {
            return false;
        }

        foreach (var character in fileNameWithoutExtension)
        {
            var isLowerHex = character is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isLowerHex)
            {
                return false;
            }
        }

        return true;
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
