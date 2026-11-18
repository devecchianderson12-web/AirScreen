using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace DualScreen.Remote;

/// <summary>
/// Exposes an ordered, reliable WebRTC data channel as a byte <see cref="Stream"/>, so the exact same framing
/// and session code is used for LAN and remote connections. Writes are chunked (16 KiB, the interoperable SCTP
/// message size) and throttled on bufferedAmount, which gives the session's queue-delay signal real meaning.
/// </summary>
public sealed class DataChannelStream : Stream
{
    private const int ChunkSize = 16 * 1024;
    private const ulong HighWater = 1 * 1024 * 1024;
    private readonly RTCDataChannel _dc;
    private readonly RTCPeerConnection _pc;
    private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 8 * 1024 * 1024, resumeWriterThreshold: 4 * 1024 * 1024));
    private readonly Stream _reader;
    private volatile bool _closed;

    public DataChannelStream(RTCPeerConnection pc, RTCDataChannel dc)
    {
        _pc = pc;
        _dc = dc;
        _reader = _pipe.Reader.AsStream();
        _dc.onmessage += (_, _, data) =>
        {
            if (_closed || data == null) return;
            var span = _pipe.Writer.GetSpan(data.Length);
            data.CopyTo(span);
            _pipe.Writer.Advance(data.Length);
            _ = _pipe.Writer.FlushAsync();
        };
        _dc.onclose += () => Complete();
        _pc.onconnectionstatechange += st =>
        {
            if (st is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected) Complete();
        };
    }

    private void Complete()
    {
        if (_closed) return;
        _closed = true;
        _pipe.Writer.Complete();
    }

    public override int Read(byte[] buffer, int offset, int count) => _reader.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _reader.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _reader.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        int pos = 0;
        while (pos < buffer.Length)
        {
            if (_closed || _dc.readyState != RTCDataChannelState.open) throw new IOException("Data channel closed");
            while (_dc.bufferedAmount > HighWater)
            {
                await Task.Delay(1, ct).ConfigureAwait(false);
                if (_closed) throw new IOException("Data channel closed");
            }
            int n = Math.Min(ChunkSize, buffer.Length - pos);
            byte[] chunk = buffer.Slice(pos, n).ToArray();
            _dc.send(chunk, 0, n);
            pos += n;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Complete();
            try { _dc.close(); } catch { }
            try { _pc.close(); } catch { }
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
