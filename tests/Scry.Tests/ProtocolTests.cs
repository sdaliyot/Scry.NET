using System.Text.Json;
using Scry.Contracts;

namespace Scry.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task FrameCodec_round_trips_a_length_prefixed_request()
    {
        var expected = new ProtocolRequest(
            ProtocolConstants.Version,
            "request-1",
            "inspect",
            JsonSerializer.SerializeToElement(new { root = "app" }, ScryJson.Options));
        await using var stream = new MemoryStream();

        await FrameCodec.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await FrameCodec.ReadAsync<ProtocolRequest>(stream);

        Assert.NotNull(actual);
        Assert.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.Operation, actual.Operation);
        Assert.Equal("app", actual.Payload.GetProperty("root").GetString());
    }
    [Fact]
    public async Task FrameCodec_rejects_an_invalid_length()
    {
        await using var stream = new MemoryStream([0xff, 0xff, 0xff, 0x7f]);

        await Assert.ThrowsAsync<ProtocolException>(async () =>
            await FrameCodec.ReadAsync<ProtocolRequest>(stream));
    }
}
