using Scry.Injector;

namespace Scry.Tests;

/// <summary>
/// <see cref="AttachCommandLine"/> had no tests at all before <c>--targets-dir</c> was added
/// alongside it - the parser was exercised only indirectly, through <c>AttachIntegrationTests</c>
/// actually attaching. This covers the parser in isolation, including the option it did not have
/// before.
/// </summary>
public sealed class AttachCommandLineTests
{
    [Fact]
    public void Parses_target_alone()
    {
        var result = AttachCommandLine.Parse(["1234"]);
        Assert.Equal("1234", result.Target);
        Assert.Null(result.Alias);
        Assert.Null(result.Adapters);
        Assert.Null(result.TcpPort);
        Assert.Null(result.TargetsDirectory);
    }

    [Fact]
    public void Parses_alias_adapters_and_tcp_port()
    {
        var result = AttachCommandLine.Parse(
            ["MyApp", "--alias", "app", "--adapters", "wpf", "--tcp-port", "0"]);
        Assert.Equal("MyApp", result.Target);
        Assert.Equal("app", result.Alias);
        Assert.Equal("wpf", result.Adapters);
        Assert.Equal(0, result.TcpPort);
    }

    [Fact]
    public void Rejects_an_unknown_adapters_value()
    {
        var exception = Assert.Throws<AttachUsageException>(
            () => AttachCommandLine.Parse(["1234", "--adapters", "electron"]));
        Assert.Contains("--adapters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_an_out_of_range_tcp_port()
    {
        var exception = Assert.Throws<AttachUsageException>(
            () => AttachCommandLine.Parse(["1234", "--tcp-port", "70000"]));
        Assert.Contains("--tcp-port", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_missing_option_value()
    {
        var exception = Assert.Throws<AttachUsageException>(
            () => AttachCommandLine.Parse(["1234", "--alias"]));
        Assert.Contains("requires a value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_an_unknown_option()
    {
        var exception = Assert.Throws<AttachUsageException>(
            () => AttachCommandLine.Parse(["1234", "--bogus", "x"]));
        Assert.Contains("Unknown attach option", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_an_absolute_targets_dir()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "scry-attach-cli-tests");
        var result = AttachCommandLine.Parse(["1234", "--targets-dir", absolute]);
        Assert.Equal(absolute, result.TargetsDirectory);
    }

    [Fact]
    public void Rejects_a_relative_targets_dir_at_parse_time()
    {
        // Deliberately at parse time, not left to surface as a startup timeout once the target
        // has already tried and failed to publish under a bad directory.
        var exception = Assert.Throws<AttachUsageException>(
            () => AttachCommandLine.Parse(["1234", "--targets-dir", @"Scry\targets"]));
        Assert.Contains("--targets-dir", exception.Message, StringComparison.Ordinal);
        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Requires_a_process_id_or_name()
    {
        var exception = Assert.Throws<AttachUsageException>(() => AttachCommandLine.Parse([]));
        Assert.Contains("requires a process ID or process name", exception.Message, StringComparison.Ordinal);
    }
}
