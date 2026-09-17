#if !NETFRAMEWORK
using Scry.Injector;

namespace Scry.Tests;

/// <summary>
/// The .NET Framework attach path loads the payload into the target's default AppDomain, under the
/// target's own binding policy. These cover the pre-flight refusal that keeps a binding downgrade
/// from turning into a MissingMethodException inside somebody else's running application.
/// </summary>
public sealed class BindingPolicyInspectorTests : IDisposable
{
    private readonly string _payloadDirectory = Path.Combine(
        Path.GetTempPath(),
        $"scry-binding-test-{Guid.NewGuid():N}");

    public BindingPolicyInspectorTests() => Directory.CreateDirectory(_payloadDirectory);

    [Fact]
    public void Redirect_below_the_payload_version_is_a_conflict()
    {
        // Uses this test assembly's own System.Text.Json as the staged payload dependency, so the
        // version compared against is a real assembly version rather than a fabricated one.
        var payloadVersion = StagePayloadDependency("System.Text.Json");
        var configuration = WriteConfiguration(
            "System.Text.Json",
            oldVersion: $"0.0.0.0-{payloadVersion}",
            newVersion: "4.0.1.2");

        var conflict = BindingPolicyInspector.FindConflict(configuration, _payloadDirectory);

        Assert.NotNull(conflict);
        Assert.Contains("System.Text.Json", conflict);
        Assert.Contains("4.0.1.2", conflict);
        Assert.Contains(payloadVersion.ToString(), conflict);
    }

    [Fact]
    public void Redirect_whose_range_excludes_the_payload_version_is_not_a_conflict()
    {
        // The precise case that matters, and the one a real host is most likely to have: the
        // redirect targets an older generation and its range stops short of the payload's own
        // version, so .NET Framework loads both side by side and the attach is safe. Getting this
        // wrong would refuse perfectly good attaches.
        var payloadVersion = StagePayloadDependency("System.Text.Json");
        Assert.True(payloadVersion.Major > 1, "This test assumes a payload version above 1.x.");
        var configuration = WriteConfiguration(
            "System.Text.Json",
            oldVersion: "0.0.0.0-1.0.0.0",
            newVersion: "4.0.1.2");

        Assert.Null(BindingPolicyInspector.FindConflict(configuration, _payloadDirectory));
    }

    [Fact]
    public void Redirect_to_a_newer_version_is_not_a_conflict()
    {
        var payloadVersion = StagePayloadDependency("System.Text.Json");
        var configuration = WriteConfiguration(
            "System.Text.Json",
            oldVersion: $"0.0.0.0-{payloadVersion}",
            newVersion: $"{payloadVersion.Major + 1}.0.0.0");

        Assert.Null(BindingPolicyInspector.FindConflict(configuration, _payloadDirectory));
    }

    [Fact]
    public void Redirects_for_assemblies_the_payload_does_not_carry_are_ignored()
    {
        StagePayloadDependency("System.Text.Json");
        var configuration = WriteConfiguration(
            "Some.Unrelated.Assembly",
            oldVersion: "0.0.0.0-99.0.0.0",
            newVersion: "1.0.0.0");

        Assert.Null(BindingPolicyInspector.FindConflict(configuration, _payloadDirectory));
    }

    [Fact]
    public void Unreadable_or_absent_configuration_does_not_block_an_attach()
    {
        StagePayloadDependency("System.Text.Json");

        // Deliberately permissive: refusing on the basis of a file we could not parse would be
        // worse than letting the attach proceed and fail loudly.
        Assert.Null(BindingPolicyInspector.FindConflict(
            Path.Combine(_payloadDirectory, "does-not-exist.config"),
            _payloadDirectory));

        var malformed = Path.Combine(_payloadDirectory, "malformed.config");
        File.WriteAllText(malformed, "<configuration><runtime>");
        Assert.Null(BindingPolicyInspector.FindConflict(malformed, _payloadDirectory));
    }

    private Version StagePayloadDependency(string simpleName)
    {
        var source = AppDomain.CurrentDomain.GetAssemblies()
            .First(assembly => assembly.GetName().Name == simpleName);
        File.Copy(source.Location, Path.Combine(_payloadDirectory, simpleName + ".dll"), overwrite: true);
        return source.GetName().Version!;
    }

    private string WriteConfiguration(string assemblyName, string oldVersion, string newVersion)
    {
        var path = Path.Combine(_payloadDirectory, $"target-{Guid.NewGuid():N}.exe.config");
        File.WriteAllText(
            path,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <runtime>
                <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
                  <dependentAssembly>
                    <assemblyIdentity name="{assemblyName}" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
                    <bindingRedirect oldVersion="{oldVersion}" newVersion="{newVersion}" />
                  </dependentAssembly>
                </assemblyBinding>
              </runtime>
            </configuration>
            """);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_payloadDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
#endif
