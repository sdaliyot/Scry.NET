using System.Text;
using System.Text.Json;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Client;
using Scry.Runtime;

namespace Scry.Tests;

/// <summary>
/// Covers the audit log: before this existed there was no record anywhere of a connection, a
/// rejected authentication, or an operation - not even in memory, let alone on disk. A failed
/// capability-token check in particular left no trace at all.
///
/// <para>
/// Every test here points <see cref="EndpointOptions.AuditDirectory"/> at a private temporary
/// directory, so none of them ever touches the real <c>%LOCALAPPDATA%\Scry\audit</c> a live host
/// would use.
/// </para>
/// </summary>
[Collection("Scry integration")]
public sealed class AuditLogTests
{
    [Fact]
    public async Task Neither_the_real_token_nor_a_rejected_token_ever_appears_in_the_log()
    {
        await using var fixture = AuditTestHost.Start();

        // A successful connection, so the real token has every opportunity to leak.
        await using (await ScryClient.ConnectAsync(fixture.Host.DescriptorPath))
        {
        }

        // And a rejected one, with a token that is wrong but distinctive enough to grep for.
        var descriptor = await TargetDiscovery.ReadAsync(fixture.Host.DescriptorPath);
        var bogusToken = Convert.ToBase64String(Encoding.UTF8.GetBytes("bogus-sentinel-token-value"));
        var rejected = descriptor with { CapabilityToken = bogusToken };
        await Assert.ThrowsAsync<ScryRemoteException>(async () => await ScryClient.ConnectAsync(rejected));

        var text = await fixture.ReadLogTextAsync();
        Assert.DoesNotContain(descriptor.CapabilityToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(bogusToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain("bogus-sentinel-token-value", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_handshake_is_recorded_as_denied()
    {
        await using var fixture = AuditTestHost.Start();
        var descriptor = await TargetDiscovery.ReadAsync(fixture.Host.DescriptorPath);
        var rejected = descriptor with { CapabilityToken = Convert.ToBase64String(new byte[32]) };

        await Assert.ThrowsAsync<ScryRemoteException>(async () => await ScryClient.ConnectAsync(rejected));

        var records = await fixture.ReadRecordsAsync();
        var denied = Assert.Single(records, record => record.Kind == AuditEventKinds.Handshake &&
            record.Outcome == AuditOutcomes.Denied);
        Assert.Equal("authentication_failed", denied.ErrorCode);
    }

    [Fact]
    public async Task An_operation_record_carries_identity_and_outcome_for_success_and_failure()
    {
        await using var fixture = AuditTestHost.Start();
        await using var client = await ScryClient.ConnectAsync(
            fixture.Host.DescriptorPath,
            clientName: "audit-test-client");

        var ok = await client.RequestAsync("capabilities");
        var failed = await client.RequestAsync("get", new { root = "does-not-exist", member = "x" });
        Assert.False(failed.Success);

        var records = await fixture.ReadRecordsAsync();

        var succeeded = Assert.Single(records, record =>
            record.Kind == AuditEventKinds.Operation && record.Operation == "capabilities");
        Assert.Equal(AuditOutcomes.Succeeded, succeeded.Outcome);
        Assert.Equal("audit-test-client", succeeded.ClientName);
        Assert.Equal(client.SessionId, succeeded.SessionId);
        Assert.Equal(ok.OperationId, succeeded.OperationId);
        Assert.Equal(ok.CorrelationId, succeeded.CorrelationId);
        Assert.True(succeeded.ElapsedMilliseconds >= 0);
        Assert.Null(succeeded.ErrorCode);

        var failure = Assert.Single(records, record =>
            record.Kind == AuditEventKinds.Operation && record.Operation == "get");
        Assert.Equal(AuditOutcomes.Failed, failure.Outcome);
        Assert.Equal(failed.Error?.Code, failure.ErrorCode);
    }

    [Fact]
    public async Task A_set_value_is_summarised_but_never_recorded()
    {
        await using var fixture = AuditTestHost.Start();
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);

        const string secret = "p@ssw0rd-sentinel-value";
        var response = await client.RequestAsync(
            "set",
            new { root = "value", member = "Text", value = secret });
        Assert.True(response.Success, response.Error?.Message);

        var records = await fixture.ReadRecordsAsync();
        var setRecord = Assert.Single(records, record =>
            record.Kind == AuditEventKinds.Operation && record.Operation == "set");
        Assert.Contains("member=Text", setRecord.Detail);
        Assert.Contains("valueKind=String", setRecord.Detail);

        var text = await fixture.ReadLogTextAsync();
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_file_rotates_rather_than_growing_without_bound()
    {
        // Exercises the sink directly rather than through a host, so a tiny cap can force several
        // rotations from a handful of records instead of needing megabytes of real traffic to
        // prove the file does not grow forever. AuditLog is internal; Scry.Runtime already grants
        // Scry.Tests visibility for exactly this kind of test.
        var directory = Path.Combine(Path.GetTempPath(), "scry-audit-tests", Guid.NewGuid().ToString("N"));
        const long cap = 512;
        var log = new AuditLog(enabled: true, directory, callback: null, processId: 4242, maximumFileBytes: cap);
        for (var i = 0; i < 20; i++)
        {
            log.Write(new(AuditEventKinds.Operation, DateTimeOffset.UtcNow, "target", AuditOutcomes.Succeeded)
            {
                Operation = "capabilities",
                Detail = $"padding-to-make-each-line-substantial-{i:D4}"
            });
        }

        await log.DisposeAsync();

        var files = Directory.GetFiles(directory, "*.jsonl");
        Assert.True(files.Length > 1, "Expected more than one file once the size cap was exceeded.");
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            // A record is never split across files, so a file may exceed the cap by at most one
            // record rather than staying strictly under it.
            Assert.True(
                info.Length <= cap + 512,
                $"{file} was {info.Length} bytes, far more than one record over the {cap}-byte cap.");
        }
    }

    [Fact]
    public async Task Concurrent_writers_never_produce_a_torn_line()
    {
        await using var fixture = AuditTestHost.Start();
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);

        await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => client.RequestAsync("capabilities", new { }, default)));

        await fixture.DisposeAsync();

        foreach (var file in fixture.LogFiles())
        {
            foreach (var line in File.ReadAllLines(file))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                // Deserializing every line is the actual property under test: a torn or
                // interleaved write would fail to parse rather than merely look wrong.
                var record = JsonSerializer.Deserialize<AuditRecord>(line, AuditJson.Options);
                Assert.NotNull(record);
            }
        }
    }

    private sealed class AuditTestHost : IAsyncDisposable
    {
        private AuditTestHost(EndpointHost host, string directory)
        {
            Host = host;
            Directory = directory;
        }

        public EndpointHost Host { get; }

        private string Directory { get; }

        public static AuditTestHost Start()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "scry-audit-tests",
                Guid.NewGuid().ToString("N"));
            var testValue = new TestValueHolder();
            var host = EndpointHost.Start(
                builder => builder.RegisterRoot("value", () => testValue, "Test value holder."),
                new EndpointOptions
                {
                    Alias = $"audit-test-{Guid.NewGuid():N}",
                    AuditDirectory = directory
                });
            return new(host, directory);
        }

        public string[] LogFiles() =>
            System.IO.Directory.Exists(Directory)
                ? System.IO.Directory.GetFiles(Directory, "*.jsonl")
                : Array.Empty<string>();

        public async Task<string> ReadLogTextAsync()
        {
            await Host.DisposeAsync();
            return string.Join("\n", LogFiles().Select(File.ReadAllText));
        }

        public async Task<IReadOnlyList<AuditRecord>> ReadRecordsAsync()
        {
            await Host.DisposeAsync();
            return LogFiles()
                .SelectMany(File.ReadAllLines)
                .Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<AuditRecord>(line, AuditJson.Options)!)
                .ToList();
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private sealed class TestValueHolder
    {
        public string Text { get; set; } = string.Empty;
    }
}
