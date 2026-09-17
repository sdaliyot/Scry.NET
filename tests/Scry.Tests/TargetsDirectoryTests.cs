using System.Net;
using System.Net.Sockets;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Runtime;

namespace Scry.Tests;

/// <summary>
/// Covers <see cref="RuntimeHostOptions.TargetsDirectory"/> / <see cref="EndpointOptions.TargetsDirectory"/>
/// / <see cref="TargetDiscovery.ResolveDirectory"/>, added after an attach into an IIS application
/// pool identity with no loaded user profile failed: <c>Environment.SpecialFolder.LocalApplicationData</c>
/// resolved to an empty string, so the default rendezvous directory silently became relative.
/// <para>
/// The "no loaded user profile" default-path failure itself is not exercised here: it requires
/// <c>LocalApplicationData</c> to actually resolve empty, which is a property of the process's
/// Windows identity, not something this suite can simulate. What is covered is the half these tests
/// can actually control: that a caller-*supplied* relative directory is rejected the same way, with
/// the same validation path (<see cref="TargetDiscovery.ResolveDirectory"/>) the default goes
/// through. The full message was verified by hand against the original IIS reproduction.
/// </para>
/// </summary>
[Collection("Scry integration")]
public sealed class TargetsDirectoryTests
{
    [Fact]
    public async Task Descriptor_is_published_into_the_override_directory_and_not_the_default_one()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "scry-targets-tests",
            Guid.NewGuid().ToString("N"));
        await using var host = EndpointHost.Start(
            options: new EndpointOptions
            {
                Alias = $"targets-dir-test-{Guid.NewGuid():N}",
                TargetsDirectory = directory
            });

        Assert.StartsWith(directory, host.DescriptorPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(host.DescriptorPath));

        var defaultPath = TargetDiscovery.GetDescriptorPath(host.Metadata.TargetId);
        Assert.False(File.Exists(defaultPath));

        var found = await TargetDiscovery.FindAsync(directory);
        Assert.Contains(found, item => item.Descriptor.Target.TargetId == host.Metadata.TargetId);
    }

    [Fact]
    public void ResolveDirectory_rejects_a_relative_override()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => TargetDiscovery.ResolveDirectory(@"Scry\targets"));
        Assert.Contains("absolute path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveDirectory_accepts_null_and_returns_a_rooted_default()
    {
        // Only meaningful in a test environment whose identity actually has a loaded profile,
        // which every CI and developer machine running this suite does.
        var resolved = TargetDiscovery.ResolveDirectory(null);
        Assert.True(Path.IsPathRooted(resolved));
    }

    [Fact]
    public void An_invalid_override_leaves_no_TCP_listener_bound()
    {
        // A free port, reserved by binding and releasing it, so the assertion below (rebinding the
        // exact same port) is not a flake against an unrelated process. TcpListener is not
        // IDisposable on net472, so Stop() is called explicitly rather than via `using`.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var exception = Assert.ThrowsAny<Exception>(() => RuntimeHost.Start(
            new EndpointConfiguration(),
            new RuntimeHostOptions
            {
                Alias = $"targets-dir-tcp-test-{Guid.NewGuid():N}",
                TcpPort = port,
                TargetsDirectory = @"Scry\targets"
            }));
        Assert.IsType<ArgumentException>(exception);

        // If RuntimeHost had bound the port before validating TargetsDirectory, this would fail
        // with AddressAlreadyInUse.
        var rebind = new TcpListener(IPAddress.Loopback, port);
        rebind.Start();
        rebind.Stop();
    }

    [Fact]
    public async Task FindAsync_ignores_and_preserves_files_that_are_not_shaped_like_a_descriptor()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "scry-targets-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var unrelated = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(unrelated, "{\"unrelated\":true}");

        // Same shape a real descriptor's name has (32 lowercase hex characters), but garbage
        // content - what FindAsync would ordinarily treat as a stale/invalid rendezvous file and
        // delete, if the filename-shape guard did not exist.
        var garbage = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(garbage, "not a descriptor");

        await TargetDiscovery.FindAsync(directory);

        Assert.True(File.Exists(unrelated), "A file outside the descriptor naming shape must never be touched.");
        Assert.False(File.Exists(garbage), "A garbage file shaped like a descriptor is still a stale-cleanup candidate.");
    }
}
