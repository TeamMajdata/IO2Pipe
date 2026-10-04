using System.Buffers.Binary;
using System.IO.Pipes;

namespace IO2Pipe;

// 无需硬件，验证协议向量和真实命名管道；--self-test 运行。
internal static class SelfTests
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref _checks);
        if (!condition) throw new InvalidOperationException($"自测失败: {message}");
    }

    public static async Task RunAsync()
    {
        TestPackets();
        TestIo4();
        TestTouch();
        TestLeds();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await TestStatePipeAsync(deadline.Token);
        await TestLedPipeAsync(deadline.Token);
        await DualPlayerTests.RunAsync(Check, deadline.Token);
        Console.WriteLine($"自测通过: {_checks} 项断言（协议、拆包/粘包、IO4 映射、触摸、LED、双玩家隔离、共享/独立 IO4、管道重连与取消）。");
    }

    private static void TestPackets()
    {
        var packet = PipeProtocol.EncodeState(0x0102030405060708);
        Check(packet.SequenceEqual(new byte[] { 0x48, 0x04, 1, 0, 0, 8, 0, 8, 7, 6, 5, 4, 3, 2, 1 }), "状态包字节序");
        for (var split = 0; split <= packet.Length; split++)
        {
            var decoder = new PacketDecoder();
            var received = new List<ulong>();
            void Receive(PacketType type, ushort version, ReadOnlySpan<byte> payload)
            {
                Check(type == PacketType.Report && version == 0, "包头");
                received.Add(BinaryPrimitives.ReadUInt64LittleEndian(payload));
            }
            decoder.Feed(packet.AsSpan(0, split), Receive);
            decoder.Feed(packet.AsSpan(split), Receive);
            Check(received.SequenceEqual(new ulong[] { 0x0102030405060708 }), $"拆包位置 {split}");
        }
        var parser = new PacketDecoder();
        var types = new List<PacketType>();
        byte[] invalid = [0x12, 0x48, 0x48, 0x04, 1, 0, 0, 0xFF, 0xFF];
        var heartbeat = PipeProtocol.Encode(PacketType.HeartBeat, []);
        var all = invalid.Concat(packet).Concat(heartbeat).Concat(packet).ToArray();
        foreach (var b in all) parser.Feed(new[] { b }, (type, _, _) => types.Add(type));
        Check(types.SequenceEqual(new[] { PacketType.Report, PacketType.HeartBeat, PacketType.Report }), "噪声、非法长度、逐字节解析");
        types.Clear();
        new PacketDecoder().Feed(packet.Concat(heartbeat).ToArray(), (type, _, _) => types.Add(type));
        Check(types.Count == 2, "管道粘包");
    }

    private static void TestIo4()
    {
        for (var player = 1; player <= 2; player++)
        {
            var first = player == 1 ? 28 : 30;
            int[] indices = [first, first, first, first + 1, first + 1, first + 1, first + 1, first + 1, 29, 28, 25, 28];
            byte[] masks = [4, 8, 1, 128, 64, 32, 16, 8, 2, 2, 1, 64];
            var neutral = new byte[64];
            neutral[29] = neutral[31] = 0x0D;
            neutral[30] = neutral[32] = 0xF8;
            Check(Io4Protocol.Decode(neutral, player) == 0, $"{player}P 空闲");
            for (var button = 0; button < 12; button++)
            {
                var data = neutral.ToArray();
                data[indices[button] + 1] ^= masks[button];
                Check(Io4Protocol.Decode(data, player) == 1UL << button, $"{player}P 按键 {button}");
            }
        }
        try { Io4Protocol.Decode(new byte[32], 1); Check(false, "短 HID 报告"); }
        catch (InvalidDataException) { Check(true, "拒绝短 HID 报告"); }
    }

    private static void TestTouch()
    {
        byte[] pressed = [(byte)'(', 1, 0, 0, 0, 0, 0, 16, (byte)')'];
        byte[] released = [(byte)'(', 0, 0, 0, 0, 0, 0, 0, (byte)')'];
        for (var split = 0; split <= pressed.Length; split++)
        {
            var decoder = new TouchDecoder();
            var states = new List<ulong>();
            decoder.Feed(pressed.AsSpan(0, split), states.Add);
            decoder.Feed(pressed.AsSpan(split), states.Add);
            decoder.Feed(released, states.Add);
            Check(states.SequenceEqual(new[] { 1UL | (1UL << 34), 0UL }), "触摸半包/释放/第35位");
        }
        var result = new List<ulong>();
        new TouchDecoder().Feed(new byte[] { 0xAA, (byte)'(', 0, 0 }.Concat(pressed).Concat(released).ToArray(), result.Add);
        Check(result.SequenceEqual(new[] { 1UL | (1UL << 34), 0UL }), "触摸损坏包后重新同步且保留连续边沿");
        Check(HardwareDevices.Sensitivity(0, 0) == 0x28 && HardwareDevices.Sensitivity(4, 5) == 1, "触摸灵敏度");
    }

    private static void TestLeds()
    {
        var leds = new LedState();
        leds.Receive(PacketType.Report, 0, new byte[] { 0, 255, 128, 64, 7, 1, 2, 3 });
        var colors = new byte[24];
        leds.CopyTo(colors);
        Check(colors[0] == 255 && colors[1] == 128 && colors[23] == 3, "LED RGB/索引");
        leds.Receive(PacketType.HeartBeat, 0, []);
        leds.Receive(PacketType.Report, 1, new byte[] { 0, 0, 0, 0 });
        leds.Receive(PacketType.Report, 0, new byte[] { 0, 0, 0, 0, 8, 0, 0, 0 });
        leds.Receive(PacketType.Report, 0, new byte[] { 0, 0, 0 });
        leds.CopyTo(colors);
        Check(colors[0] == 255, "忽略心跳/不支持版本/非法索引/非法长度");
        var packet = SerialLedProtocol.SetColor(2, new byte[] { 255, 128, 64 }, 0.5);
        Check(packet.SequenceEqual(new byte[] { 0xE0, 0x11, 1, 5, 0x31, 2, 127, 64, 32, 0x29 }), "串口 LED 亮度和校验和");
        leds.Clear();
        leds.CopyTo(colors);
        Check(colors.All(c => c == 0), "LED 断开清空");
    }

    private static async Task TestStatePipeAsync(CancellationToken token)
    {
        var name = $"IO2Pipe.Tests.State.{Guid.NewGuid():N}";
        var state = new StatePipe();
        state.Publish(7);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var server = state.RunAsync(name, 10, stop.Token);
        try
        {
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(token);
                Check(await ReadStateAsync(client, token) == 7, "管道初始快照");
                for (var i = 0; i < 100; i++) { state.Publish(1); state.Publish(0); }
                for (var i = 0; i < 200; i++)
                    Check(await ReadStateAsync(client, token) == (i % 2 == 0 ? 1UL : 0UL), "快速按下释放顺序");
            }
            state.Publish(42);
            using var next = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await next.ConnectAsync(token);
            Check(await ReadStateAsync(next, token) == 42, "重连后最新快照");
        }
        finally
        {
            await stop.CancelAsync();
            try { await server; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        Check(server.IsCompleted, "状态服务停止");
    }

    private static async Task<ulong> ReadStateAsync(Stream stream, CancellationToken token)
    {
        var packet = new byte[15];
        await stream.ReadExactlyAsync(packet, token);
        Check(packet[0] == 0x48 && packet[1] == 4 && packet[2] == 1 && packet[5] == 8, "真实管道包头");
        return BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(7));
    }

    private static async Task TestLedPipeAsync(CancellationToken token)
    {
        var name = $"IO2Pipe.Tests.Led.{Guid.NewGuid():N}";
        var leds = new LedState();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var server = PipeServer.RunAsync(name, 10, leds.ServeAsync, stop.Token);
        try
        {
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(token);
                var report = PipeProtocol.Encode(PacketType.Report, new byte[] { 3, 10, 20, 30 });
                await client.WriteAsync(report.AsMemory(0, 4), token);
                await client.WriteAsync(report.AsMemory(4), token);
                await client.WriteAsync(PipeProtocol.Encode(PacketType.HeartBeat, []), token);
                await WaitColorAsync(leds, 10, token);
                Check(true, "真实 LED 管道拆包");
            }
            await WaitColorAsync(leds, 0, token);
            Check(true, "LED EOF 后熄灯");
        }
        finally
        {
            await stop.CancelAsync();
            try { await server; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        Check(server.IsCompleted, "LED 服务停止");
    }

    private static async Task WaitColorAsync(LedState leds, byte expected, CancellationToken token)
    {
        var colors = new byte[24];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            leds.CopyTo(colors);
            if (colors[9] == expected) return;
            await Task.Delay(10, token);
        }
    }
}
