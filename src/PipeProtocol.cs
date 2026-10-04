using System.Buffers.Binary;

namespace IO2Pipe;

internal enum PacketType : byte { HeartBeat = 0, Report = 1 }

internal static class PipeProtocol
{
    public const int HeaderLength = 7;
    public const int MaxPayloadLength = 1024;

    public static byte[] Encode(PacketType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadLength) throw new ArgumentOutOfRangeException(nameof(payload));
        var packet = new byte[HeaderLength + payload.Length];
        packet[0] = 0x48;
        packet[1] = 0x04;
        packet[2] = (byte)type;
        // Version = 0，与提供的游戏端保持一致。
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5), (ushort)payload.Length);
        payload.CopyTo(packet.AsSpan(HeaderLength));
        return packet;
    }

    public static byte[] EncodeState(ulong state)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, state);
        return Encode(PacketType.Report, payload);
    }
}

internal delegate void PacketHandler(PacketType type, ushort version, ReadOnlySpan<byte> payload);

// 一个连接使用一个解码器，保留半包；非法长度时重新搜索 magic。
internal sealed class PacketDecoder
{
    private readonly byte[] _buffer = new byte[PipeProtocol.HeaderLength + PipeProtocol.MaxPayloadLength];
    private int _count;

    public void Feed(ReadOnlySpan<byte> bytes, PacketHandler handler)
    {
        foreach (var value in bytes)
        {
            _buffer[_count++] = value;
            while (_count > 0)
            {
                if (_buffer[0] != 0x48 || (_count >= 2 && _buffer[1] != 0x04))
                {
                    Skip(1);
                    continue;
                }
                if (_count < PipeProtocol.HeaderLength) break;
                var length = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(5));
                if (length > PipeProtocol.MaxPayloadLength)
                {
                    Skip(2);
                    continue;
                }
                var packetLength = PipeProtocol.HeaderLength + length;
                if (_count < packetLength) break;
                try
                {
                    handler((PacketType)_buffer[2], BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(3)),
                        _buffer.AsSpan(PipeProtocol.HeaderLength, length));
                }
                finally { Skip(packetLength); }
            }
        }
    }

    private void Skip(int length)
    {
        _buffer.AsSpan(length, _count - length).CopyTo(_buffer);
        _count -= length;
    }
}
