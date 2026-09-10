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
        using var stream = new MemoryStream();

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
        using var stream = new MemoryStream(new byte[] { 0xff, 0xff, 0xff, 0x7f });

        await Assert.ThrowsAsync<ProtocolException>(async () =>
            await FrameCodec.ReadAsync<ProtocolRequest>(stream));
    }

    [Fact]
    public async Task FrameCodec_handles_partial_reads_without_changing_framing()
    {
        var expected = new ProtocolRequest(
            ProtocolConstants.Version,
            "partial",
            "capabilities",
            JsonSerializer.SerializeToElement(new { }, ScryJson.Options));
        using var encoded = new MemoryStream();
        await FrameCodec.WriteAsync(encoded, expected);
        using var stream = new ChunkedReadStream(encoded.ToArray(), 1);

        var actual = await FrameCodec.ReadAsync<ProtocolRequest>(stream);

        Assert.NotNull(actual);
        Assert.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.Operation, actual.Operation);
        Assert.Equal(expected.Payload.GetRawText(), actual.Payload.GetRawText());
    }

    [Fact]
    public async Task FrameCodec_distinguishes_clean_eof_from_truncated_frames()
    {
        using var empty = new MemoryStream();
        Assert.Null(await FrameCodec.ReadAsync<ProtocolRequest>(empty));

        using var truncatedHeader = new ChunkedReadStream(new byte[] { 1, 0 }, 1);
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await FrameCodec.ReadAsync<ProtocolRequest>(truncatedHeader));

        using var truncatedPayload = new ChunkedReadStream(
            new byte[] { 4, 0, 0, 0, (byte)'{', (byte)'}' },
            1);
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await FrameCodec.ReadAsync<ProtocolRequest>(truncatedPayload));
    }

    private sealed class ChunkedReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _maximumRead;

        public ChunkedReadStream(byte[] content, int maximumRead)
        {
            _inner = new MemoryStream(content);
            _maximumRead = maximumRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, _maximumRead));

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, Math.Min(count, _maximumRead), cancellationToken);

#if !NET48
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, _maximumRead)], cancellationToken);
#endif

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
