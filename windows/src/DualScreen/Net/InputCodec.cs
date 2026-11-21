using System;
using System.Buffers.Binary;

namespace DualScreen.Net;

public enum InputKind : byte { Touch = 1, Pen = 2, MouseMove = 3, MouseButton = 4, Wheel = 5, Key = 6, ReleaseAll = 7 }
public enum ContactState : byte { Down = 0, Move = 1, Up = 2, Cancel = 3 }
public enum PenState : byte { Hover = 0, Down = 1, Move = 2, Up = 3, Leave = 4 }
public enum MouseButtonId : byte { Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4 }

[Flags]
public enum PenButtons : byte { None = 0, Barrel = 1, Inverted = 2, Eraser = 4 }

/// <summary>
/// INPUT (0x0B) payloads, all big-endian. Positions are normalized to 0..65535 over the video frame, so they
/// do not depend on the receiver's window size. See shared/PROTOCOL.md.
/// <code>
/// Touch       [1][id:u8][state:u8][x:u16][y:u16][pressure:u16 0..1024]
/// Pen         [2][state:u8][x:u16][y:u16][pressure:u16][buttons:u8]
/// MouseMove   [3][x:u16][y:u16]
/// MouseButton [4][button:u8][down:u8][x:u16][y:u16]
/// Wheel       [5][horizontal:u8][delta:i16]
/// Key         [6][scanCode:u16][flags:u8 bit0=up bit1=extended]
/// ReleaseAll  [7]
/// </code>
/// </summary>
public static class InputCodec
{
    public static byte[] Touch(byte id, ContactState state, ushort x, ushort y, ushort pressure)
    {
        Span<byte> p = stackalloc byte[9];
        p[0] = (byte)InputKind.Touch; p[1] = id; p[2] = (byte)state;
        BinaryPrimitives.WriteUInt16BigEndian(p[3..], x);
        BinaryPrimitives.WriteUInt16BigEndian(p[5..], y);
        BinaryPrimitives.WriteUInt16BigEndian(p[7..], pressure);
        return FrameCodec.Encode(MsgType.Input, p);
    }

    public static byte[] Pen(PenState state, ushort x, ushort y, ushort pressure, PenButtons buttons)
    {
        Span<byte> p = stackalloc byte[9];
        p[0] = (byte)InputKind.Pen; p[1] = (byte)state;
        BinaryPrimitives.WriteUInt16BigEndian(p[2..], x);
        BinaryPrimitives.WriteUInt16BigEndian(p[4..], y);
        BinaryPrimitives.WriteUInt16BigEndian(p[6..], pressure);
        p[8] = (byte)buttons;
        return FrameCodec.Encode(MsgType.Input, p);
    }

    public static byte[] MouseMove(ushort x, ushort y)
    {
        Span<byte> p = stackalloc byte[5];
        p[0] = (byte)InputKind.MouseMove;
        BinaryPrimitives.WriteUInt16BigEndian(p[1..], x);
        BinaryPrimitives.WriteUInt16BigEndian(p[3..], y);
        return FrameCodec.Encode(MsgType.Input, p);
    }

    public static byte[] MouseButton(MouseButtonId button, bool down, ushort x, ushort y)
    {
        Span<byte> p = stackalloc byte[7];
        p[0] = (byte)InputKind.MouseButton; p[1] = (byte)button; p[2] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(p[3..], x);
        BinaryPrimitives.WriteUInt16BigEndian(p[5..], y);
        return FrameCodec.Encode(MsgType.Input, p);
    }

    public static byte[] Wheel(bool horizontal, short delta)
    {
        Span<byte> p = stackalloc byte[4];
        p[0] = (byte)InputKind.Wheel; p[1] = (byte)(horizontal ? 1 : 0);
        BinaryPrimitives.WriteInt16BigEndian(p[2..], delta);
        return FrameCodec.Encode(MsgType.Input, p);
    }

    public static byte[] Key(ushort scanCode, bool up, bool extended)
    {
        Span<byte> p = stackalloc byte[4];
        p[0] = (byte)InputKind.Key;
        BinaryPrimitives.WriteUInt16BigEndian(p[1..], scanCode);
        p[3] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
        return FrameCodec.Encode(MsgType.Input, p);
    }

    public static byte[] ReleaseAll() => FrameCodec.Encode(MsgType.Input, stackalloc byte[] { (byte)InputKind.ReleaseAll });
}
