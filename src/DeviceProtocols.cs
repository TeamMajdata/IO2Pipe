namespace IO2Pipe;

internal static class Io4Protocol
{
    // HidSharp 的输入包含 report ID；索引与游戏 GeneralHIDDevice 一致。
    public static ulong Decode(ReadOnlySpan<byte> report, int player)
    {
        if (player is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(player));
        if (report.Length < 33) throw new InvalidDataException("IO4 输入报告不足 33 字节。");
        var data = report[1..];
        var first = player == 1 ? 28 : 30;
        ReadOnlySpan<int> indices = [first, first, first, first + 1, first + 1, first + 1, first + 1, first + 1, 29, 28, 25, 28];
        ReadOnlySpan<byte> masks = [4, 8, 1, 128, 64, 32, 16, 8, 2, 2, 1, 64];
        ulong state = 0;
        for (var i = 0; i < 12; i++)
        {
            var pressed = (data[indices[i]] & masks[i]) != 0;
            if (i < 8) pressed = !pressed;
            if (pressed) state |= 1UL << i;
        }
        return state;
    }
}

internal sealed class TouchDecoder
{
    private readonly byte[] _packet = new byte[9];
    private int _count;

    public void Feed(ReadOnlySpan<byte> bytes, Action<ulong> publish)
    {
        foreach (var value in bytes)
        {
            if (_count == 0 && value != '(') continue;
            _packet[_count++] = value;
            if (_count < 9) continue;
            if (_packet[8] == ')')
            {
                ulong state = 0;
                for (var i = 0; i < 7; i++) state |= (ulong)(_packet[i + 1] & 0x1F) << (i * 5);
                _count = 0;
                publish(state);
            }
            else
            {
                var next = _packet.AsSpan(1).IndexOf((byte)'(');
                if (next < 0) _count = 0;
                else
                {
                    var skip = next + 1;
                    _packet.AsSpan(skip).CopyTo(_packet);
                    _count -= skip;
                }
            }
        }
    }
}

internal static class SerialLedProtocol
{
    public static readonly byte[] UpdatePacket = [0xE0, 0x11, 0x01, 0x01, 0x3C, 0x4F];

    public static byte[] SetColor(int index, ReadOnlySpan<byte> rgb, double brightness)
    {
        if (index is < 0 or > 7 || rgb.Length < 3) throw new ArgumentOutOfRangeException(nameof(index));
        byte[] packet = [0xE0, 0x11, 0x01, 0x05, 0x31, (byte)index,
            (byte)(rgb[0] * brightness), (byte)(rgb[1] * brightness), (byte)(rgb[2] * brightness), 0];
        for (var i = 1; i < 9; i++) packet[9] = unchecked((byte)(packet[9] + packet[i]));
        return packet;
    }
}
