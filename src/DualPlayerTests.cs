using System.Buffers.Binary;
using System.IO.Pipes;

namespace IO2Pipe;

internal static class DualPlayerTests
{
    public static async Task RunAsync(Action<bool, string> check, CancellationToken token)
    {
        TestConfiguration(check);
        var options = new BridgeOptions { ReconnectIntervalMs = 100 };
        foreach (var player in options.Players)
        {
            player.ButtonRing.Enabled = false;
            player.TouchPanel.Enabled = false;
            player.Led.Enabled = false;
        }
        // 运行真实 IoManager 的六个管道，只用模拟报告替代物理设备。
        var manager = new IoManager(options);
        var p1 = manager.Players[0];
        var p2 = manager.Players[1];
        var prefix = $"IO2Pipe.Tests.Dual.{Guid.NewGuid():N}";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = manager.RunAsync(stop.Token, prefix);
        var clients = new List<NamedPipeClientStream>();
        async Task<NamedPipeClientStream> Connect(string device, int player)
        {
            var client = new NamedPipeClientStream(".", $"{prefix}.{device}.{player}P", PipeDirection.InOut, PipeOptions.Asynchronous);
            clients.Add(client);
            await client.ConnectAsync(token);
            return client;
        }
        try
        {
            var b1 = await Connect("ButtonRing", 1);
            var b2 = await Connect("ButtonRing", 2);
            var t1 = await Connect("TouchPanel", 1);
            var t2 = await Connect("TouchPanel", 2);
            var l1 = await Connect("Led", 1);
            var l2 = await Connect("Led", 2);
            foreach (var client in new[] { b1, b2, t1, t2 })
                check(await ReadStateAsync(client, token) == 0, "双玩家四个输入管道初始释放");

            var shared = Io4Reader.Create(
            [
                new(new Io4Options(), 1, p1.Buttons),
                new(new Io4Options(), 2, p2.Buttons)
            ], 100);
            check(shared.Length == 1, "两玩家共用 IO4 只创建一个读取器");
            var neutral = NeutralReport();
            var pressed = neutral.ToArray();
            pressed[29] ^= 4; // IO4 1P A1
            pressed[31] ^= 8; // IO4 2P A2
            for (var i = 0; i < 50; i++)
            {
                shared[0].Publish(pressed);
                shared[0].Publish(neutral);
            }
            await Task.WhenAll(
                CheckEdgesAsync(b1, 1, 50, "1P", check, token),
                CheckEdgesAsync(b2, 2, 50, "2P", check, token));

            // 两块独立 IO4：都接本板的 1P 输入，分别路由给两个游戏。
            var separate = Io4Reader.Create(
            [
                new(new Io4Options { DevicePath = "board-left", InputPlayerIndex = 1 }, 1, p1.Buttons),
                new(new Io4Options { DevicePath = "board-right", InputPlayerIndex = 1 }, 2, p2.Buttons)
            ], 100);
            check(separate.Length == 2, "独立 IO4 分别创建读取器");
            var left = neutral.ToArray();
            var right = neutral.ToArray();
            left[29] ^= 1; // A3
            right[30] ^= 128; // A4
            separate[0].Publish(left);
            separate[1].Publish(right);
            check(await NextChangeAsync(b1, 0, token) == 4, "独立 IO4 只更新 1P");
            check(await NextChangeAsync(b2, 0, token) == 8, "2P 可使用独立 IO4 的 1P 输入组");

            // 交错送入两个串口的半包，验证解析缓冲区和传感器状态隔离。
            var touch1 = new TouchDecoder();
            var touch2 = new TouchDecoder();
            byte[] frame1 = [(byte)'(', 0, 0, 0, 2, 0, 0, 0, (byte)')']; // C1, bit 16
            byte[] frame2 = [(byte)'(', 0, 0, 0, 0, 0, 0, 8, (byte)')']; // E8, bit 33
            touch1.Feed(frame1.AsSpan(0, 4), p1.Touch.Publish);
            touch2.Feed(frame2.AsSpan(0, 6), p2.Touch.Publish);
            touch1.Feed(frame1.AsSpan(4), p1.Touch.Publish);
            touch2.Feed(frame2.AsSpan(6), p2.Touch.Publish);
            check(await NextChangeAsync(t1, 0, token) == 1UL << 16, "1P 触摸状态及半包独立");
            check(await NextChangeAsync(t2, 0, token) == 1UL << 33, "2P 触摸状态及半包独立");

            await SendColorAsync(l1, 10, token);
            await SendColorAsync(l2, 90, token);
            await WaitColorAsync(p1.Leds, 10, token);
            await WaitColorAsync(p2.Leds, 90, token);
            check(Color(p1.Leds) == 10 && Color(p2.Leds) == 90, "相同灯编号的 1P/2P LED 颜色独立");

            // 模拟 1P 游戏退出：关闭该玩家三个管道，2P 继续通信。
            b1.Dispose();
            t1.Dispose();
            l1.Dispose();
            await WaitColorAsync(p1.Leds, 0, token);
            p2.Buttons.Publish(16);
            p2.Touch.Publish(1UL << 26);
            await SendColorAsync(l2, 100, token);
            check(await NextChangeAsync(b2, 8, token) == 16, "1P 断开后 2P 按键继续工作");
            check(await NextChangeAsync(t2, 1UL << 33, token) == 1UL << 26, "1P 断开后 2P 触摸继续工作");
            await WaitColorAsync(p2.Leds, 100, token);
            check(Color(p1.Leds) == 0 && Color(p2.Leds) == 100, "1P 断开只清空自身 LED");

            p1.Buttons.Publish(32);
            p1.Touch.Publish(1UL << 18);
            b1 = await Connect("ButtonRing", 1);
            t1 = await Connect("TouchPanel", 1);
            l1 = await Connect("Led", 1);
            check(await ReadStateAsync(b1, token) == 32, "1P 重连收到自身最新按键快照");
            check(await ReadStateAsync(t1, token) == 1UL << 18, "1P 重连收到自身最新触摸快照");
            await SendColorAsync(l1, 30, token);
            await WaitColorAsync(p1.Leds, 30, token);
            check(Color(p2.Leds) == 100, "1P LED 重连不改变 2P 颜色");
            p2.Buttons.Publish(64);
            check(await NextChangeAsync(b2, 16, token) == 64, "1P 重连期间 2P 原连接仍可使用");
        }
        finally
        {
            // 客户端保持连接时取消服务，验证六个等待中的循环全部退出。
            await stop.CancelAsync();
            try { await running.WaitAsync(token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && !token.IsCancellationRequested) { }
            finally { foreach (var client in clients) client.Dispose(); }
        }
        check(running.IsCompleted, "双玩家管理器取消后全部管道退出");
    }

    private static void TestConfiguration(Action<bool, string> check)
    {
        new BridgeOptions().Validate();
        var sample = BridgeOptions.Load(Path.Combine(AppContext.BaseDirectory, "io2pipe.json"));
        check(sample.Players.Length == 2, "随程序发布的双玩家配置可读取");
        void Reject(Action<BridgeOptions> mutate, string message)
        {
            var options = new BridgeOptions();
            mutate(options);
            try { options.Validate(); }
            catch (InvalidDataException) { check(true, message); return; }
            check(false, message);
        }
        Reject(o => o.Players[1].PlayerIndex = 1, "拒绝重复玩家编号");
        Reject(o => o.Players = [o.Players[0]], "拒绝缺少玩家配置");
        Reject(o => o.Players[1].TouchPanel.PortName = "com3", "拒绝跨玩家占用同一个串口");
        Reject(o => o.Players[1].Led.PortName = "COM3", "拒绝跨玩家 LED/触摸串口冲突");
        Reject(o => o.Players[1].ButtonRing.InputPlayerIndex = 3, "拒绝无效 IO4 输入组");
        Reject(o => o.Players[1].ButtonRing.DevicePath = "board-right", "拒绝可能重复打开 IO4 的混合自动/精确选择");
        var sharedPath = Io4Reader.Create(
        [
            new(new Io4Options { DevicePath = "BOARD" }, 1, new()),
            new(new Io4Options { DevicePath = "board" }, 2, new())
        ], 100);
        check(sharedPath.Length == 1, "相同 HID 路径忽略大小写并共用读取器");
    }

    private static byte[] NeutralReport()
    {
        var report = new byte[64];
        report[29] = report[31] = 0x0D;
        report[30] = report[32] = 0xF8;
        return report;
    }

    private static async Task CheckEdgesAsync(Stream stream, ulong pressed, int count, string player,
        Action<bool, string> check, CancellationToken token)
    {
        ulong previous = 0;
        for (var i = 0; i < count * 2; i++)
        {
            var next = await NextChangeAsync(stream, previous, token);
            check(next == (i % 2 == 0 ? pressed : 0), $"{player} 快速按下/释放顺序，不混入另一玩家状态");
            previous = next;
        }
    }

    private static async Task<ulong> NextChangeAsync(Stream stream, ulong previous, CancellationToken token)
    {
        // 允许周期性重复快照；不跳过任何状态变化。
        while (true)
        {
            var state = await ReadStateAsync(stream, token);
            if (state != previous) return state;
        }
    }

    private static async Task<ulong> ReadStateAsync(Stream stream, CancellationToken token)
    {
        var packet = new byte[15];
        await stream.ReadExactlyAsync(packet, token);
        if (!packet.AsSpan(0, 7).SequenceEqual(new byte[] { 0x48, 4, 1, 0, 0, 8, 0 }))
            throw new InvalidDataException("双玩家测试收到非法管道包头。");
        return BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(7));
    }

    private static async Task SendColorAsync(Stream stream, byte red, CancellationToken token)
    {
        var packet = PipeProtocol.Encode(PacketType.Report, new byte[] { 3, red, 20, 30 });
        await stream.WriteAsync(packet.AsMemory(0, 5), token);
        await stream.WriteAsync(packet.AsMemory(5), token);
    }

    private static byte Color(LedState leds)
    {
        Span<byte> colors = stackalloc byte[24];
        leds.CopyTo(colors);
        return colors[9];
    }

    private static async Task WaitColorAsync(LedState leds, byte expected, CancellationToken token)
    {
        while (Color(leds) != expected) await Task.Delay(10, token);
    }
}
