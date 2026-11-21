using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DualScreen.Core;

namespace DualScreen.Net;

/// <summary>Wire message types. Keep in sync with shared/PROTOCOL.md and the Android Protocol.kt.</summary>
public enum MsgType : byte
{
    Hello = 0x01,          // client -> host, JSON HelloMsg
    Welcome = 0x02,        // host -> client, JSON WelcomeMsg
    StreamConfig = 0x03,   // host -> client, JSON StreamConfigMsg
    Video = 0x04,          // host -> client, binary: flags(1) ptsUs(8) data
    Ping = 0x05,           // either way, binary: originTimestampUs(8)
    Pong = 0x06,           // echo of ping payload
    Stats = 0x07,          // client -> host, JSON ClientStatsMsg
    KeyframeRequest = 0x08,// client -> host, empty
    ClientSettings = 0x09, // client -> host, JSON ClientSettingsMsg
    Bye = 0x0A,            // either way, JSON ByeMsg
    Input = 0x0B,          // client -> host, binary input event (see InputCodec)
    Cursor = 0x0C,         // host -> client, binary cursor shape / position (clients with feature "cursor")
    Audio = 0x0D,          // host -> client, binary: rate(4) channels(1) ptsUs(8) PCM s16le
    HostState = 0x0E,      // host -> client, JSON HostStateMsg (clients with feature "hostSettings")
    HostSettings = 0x0F,   // client -> host, JSON HostSettingsMsg (partial)
}

/// <summary>Monotonic microseconds on the QPC timebase shared by capture timestamps, pings and video pts.</summary>
public static class HostClock
{
    public static long NowUs() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
}

public sealed class MonitorInfoMsg
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Primary { get; set; }
}

/// <summary>What the host is doing and what can be changed from the receiver.</summary>
public sealed class HostStateMsg
{
    public string Mode { get; set; } = "extended";          // setting: extended | mirror
    public string EffectiveMode { get; set; } = "extended"; // what is streaming now
    public bool DriverOk { get; set; }
    public string MirrorMonitor { get; set; } = "";
    public List<MonitorInfoMsg> Monitors { get; set; } = new();
    public string Resolution { get; set; } = "auto";
    public List<string> Resolutions { get; set; } = new();
    public int RefreshRate { get; set; }
    public List<int> Rates { get; set; } = new();
    public string Quality { get; set; } = "auto";            // auto maximum high balanced saver
    public string Performance { get; set; } = "lowLatency";  // lowLatency balanced quality
    public string Codec { get; set; } = "auto";               // auto h264 hevc av1
    public List<string> Codecs { get; set; } = new();
    public int MaxBitrateMbps { get; set; }
    public bool Audio { get; set; }
    public string HostName { get; set; } = "";
}

/// <summary>Partial update sent by a receiver; null fields are left unchanged. Applied to the PC's own settings.</summary>
public sealed class HostSettingsMsg
{
    public string? Mode { get; set; }
    public string? MirrorMonitor { get; set; }
    public string? Resolution { get; set; }
    public int? RefreshRate { get; set; }
    public string? Quality { get; set; }
    public string? Performance { get; set; }
    public string? Codec { get; set; }
    public int? MaxBitrateMbps { get; set; }
    public bool? Audio { get; set; }
}

[Flags]
public enum VideoFlags : byte { None = 0, Keyframe = 1, CodecConfig = 2 }

public sealed class CodecCaps
{
    /// <summary>"video/avc", "video/hevc", "video/av01".</summary>
    public string Mime { get; set; } = "video/avc";
    public bool Hardware { get; set; }
    public int MaxWidth { get; set; }
    public int MaxHeight { get; set; }
    /// <summary>Per-resolution max decodable fps, as reported by MediaCodec VideoCapabilities.</summary>
    public List<ModeCaps> Modes { get; set; } = new();
}

public sealed class ModeCaps
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int MaxFps { get; set; }
}

public sealed class DeviceCaps
{
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public int DensityDpi { get; set; }
    public List<float> RefreshRates { get; set; } = new();
    public List<CodecCaps> Codecs { get; set; } = new();
    public string Abi { get; set; } = "";
    public int SdkInt { get; set; }
    /// <summary>"android" (absent in older clients) or "windows".</summary>
    public string? Platform { get; set; }
    /// <summary>Input devices the client forwards: "touch", "pen", "mouse", "keyboard". Null = none.</summary>
    public List<string>? Input { get; set; }
    /// <summary>Optional protocol features: "cursor" (local cursor), "audio", "hostSettings", "latency".</summary>
    public List<string>? Features { get; set; }

    public bool Has(string feature) => Features != null && Features.Contains(feature);
}

public sealed class AuthBlock
{
    /// <summary>"token" for a paired device, "pair" for first-time pairing.</summary>
    public string Mode { get; set; } = "token";
    public string? Token { get; set; }
    /// <summary>Hex HMAC-SHA256(code, "DSPAIR1|fp|deviceId|nonce").</summary>
    public string? Proof { get; set; }
    public string? Nonce { get; set; }
}

public sealed class HelloMsg
{
    public int Proto { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Model { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public AuthBlock Auth { get; set; } = new();
    public DeviceCaps Caps { get; set; } = new();
    public string Quality { get; set; } = "auto";
}

public sealed class WelcomeMsg
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string HostId { get; set; } = "";
    public string HostName { get; set; } = "";
    public string HostVersion { get; set; } = "";
    /// <summary>Only sent once, right after a successful pairing.</summary>
    public string? DeviceToken { get; set; }
}

public sealed class StreamConfigMsg
{
    public string Mime { get; set; } = "video/avc";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public int BitrateKbps { get; set; }
    public string Mode { get; set; } = "extended";
    public int Rotation { get; set; }
}

public sealed class ClientStatsMsg
{
    public float Fps { get; set; }
    public float DecodeMs { get; set; }
    public float RecvKbps { get; set; }
    public int Dropped { get; set; }
    /// <summary>Measured capture-to-display latency in ms (clock offset from PING/PONG), 0 if unknown.</summary>
    public float LatencyMs { get; set; }
}

public sealed class ClientSettingsMsg
{
    public string Quality { get; set; } = "auto";
}

public sealed class ByeMsg
{
    public string Reason { get; set; } = "";
}

public readonly struct Frame
{
    public readonly MsgType Type;
    public readonly byte[] Buffer; // rented from ArrayPool when Pooled
    public readonly int Length;
    public readonly bool Pooled;

    public Frame(MsgType type, byte[] buffer, int length, bool pooled)
    {
        Type = type; Buffer = buffer; Length = length; Pooled = pooled;
    }

    public ReadOnlySpan<byte> Payload => Buffer.AsSpan(0, Length);

    public T? Json<T>() => JsonSerializer.Deserialize<T>(Payload, Core.Json.Options);

    public void Return()
    {
        if (Pooled) ArrayPool<byte>.Shared.Return(Buffer);
    }
}

/// <summary>
/// Length-prefixed framing: [type:1][length:4 big-endian][payload]. Works on any byte stream
/// (TLS over TCP for LAN, or the reassembled WebRTC data channel for remote connections).
/// </summary>
public static class FrameCodec
{
    public const int HeaderSize = 5;
    public const int MaxPayload = 16 * 1024 * 1024;

    public static byte[] Encode(MsgType type, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderSize + payload.Length];
        buf[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(1), payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static byte[] EncodeJson<T>(MsgType type, T value) =>
        Encode(type, JsonSerializer.SerializeToUtf8Bytes(value, Core.Json.Options));

    public static byte[] EncodeVideo(VideoFlags flags, long ptsUs, ReadOnlySpan<byte> data)
    {
        var buf = new byte[HeaderSize + 9 + data.Length];
        buf[0] = (byte)MsgType.Video;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(1), 9 + data.Length);
        buf[5] = (byte)flags;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(6), ptsUs);
        data.CopyTo(buf.AsSpan(HeaderSize + 9));
        return buf;
    }

    public static byte[] EncodeTimestamp(MsgType type, long value)
    {
        Span<byte> p = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(p, value);
        return Encode(type, p);
    }

    /// <summary>Blocking read for dedicated receive threads (lower overhead than the async path).</summary>
    public static Frame Read(Stream s)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        s.ReadExactly(header);
        var type = (MsgType)header[0];
        int len = BinaryPrimitives.ReadInt32BigEndian(header[1..]);
        if (len < 0 || len > MaxPayload) throw new InvalidDataException($"Invalid frame length {len}");
        byte[] buf = ArrayPool<byte>.Shared.Rent(Math.Max(len, 1));
        try { s.ReadExactly(buf, 0, len); }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }
        return new Frame(type, buf, len, true);
    }

    public static async Task<Frame> ReadAsync(Stream s, CancellationToken ct)
    {
        byte[] header = new byte[HeaderSize];
        await s.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var type = (MsgType)header[0];
        int len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (len < 0 || len > MaxPayload) throw new InvalidDataException($"Invalid frame length {len}");
        byte[] buf = ArrayPool<byte>.Shared.Rent(Math.Max(len, 1));
        try
        {
            await s.ReadExactlyAsync(buf.AsMemory(0, len), ct).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }
        return new Frame(type, buf, len, true);
    }
}
