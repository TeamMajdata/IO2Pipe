using System.Diagnostics;
using System.IO.Ports;

namespace IO2Pipe;

internal sealed class HardwareDevices(PlayerOptions options, int retryMs, StatePipe touch, LedState leds)
{
    public Task RunTouchAsync(CancellationToken token) => RunDeviceAsync($"TouchPanel/{options.PlayerIndex}P", () => ReadTouch(token), () => touch.Publish(0), token);
    public Task RunLedAsync(CancellationToken token) => RunDeviceAsync($"LED/{options.PlayerIndex}P", () => WriteLeds(token), null, token);

    private async Task RunDeviceAsync(string name, Action session, Action? disconnected, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Factory.StartNew(session, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
            {
                BridgeLog.Write(name, $"设备不可用，稍后重试: {e.Message}");
            }
            finally { disconnected?.Invoke(); }
            await Task.Delay(retryMs, token);
        }
    }

    private static SerialPort OpenSerial(SerialOptions config)
    {
        var stream = new SerialPort(config.PortName);
        try
        {
            stream.BaudRate = config.BaudRate;
            stream.DataBits = 8;
            stream.Parity = Parity.None;
            stream.StopBits = StopBits.One;
            stream.DtrEnable = true;
            stream.RtsEnable = true;
            stream.ReadTimeout = 2000;
            stream.WriteTimeout = 2000;
            stream.Open();
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private void ReadTouch(CancellationToken token)
    {
        using var stream = OpenSerial(options.TouchPanel);
        InitTouch(stream, token);
        var decoder = new TouchDecoder();
        var buffer = new byte[1024];
        BridgeLog.Write($"TouchPanel/{options.PlayerIndex}P", $"已初始化 {options.TouchPanel.PortName}");
        while (!token.IsCancellationRequested)
        {
            // 连续两秒无数据视为连接异常，清空状态并重新初始化。
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) throw new EndOfStreamException("触摸屏已断开。");
            decoder.Feed(buffer.AsSpan(0, read), touch.Publish);
        }
    }

    private void InitTouch(SerialPort stream, CancellationToken token)
    {
        stream.Write("{RSET}");
        if (OperatingSystem.IsWindows())
        {
            // 参考游戏端：Windows 下读复位响应，避免 Sleep 导致部分驱动挂起。
            var response = new byte[10];
            try
            {
                var offset = 0;
                while (offset < response.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var read = stream.Read(response, offset, response.Length - offset);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
            }
            catch (TimeoutException) { BridgeLog.Write($"TouchPanel/{options.PlayerIndex}P", "复位响应超时，继续初始化"); }
        }
        else { Wait(4000, token); }
        token.ThrowIfCancellationRequested();
        stream.Write("{HALT}");
        var side = options.PlayerIndex == 1 ? 'L' : 'R';
        for (var sensor = 0x41; sensor <= 0x62; sensor++)
        {
            token.ThrowIfCancellationRequested();
            stream.Write($"{{{side}{(char)sensor}r2}}");
        }
        if (options.TouchPanel.OverrideSensitivity)
        {
            try
            {
                for (var sensor = 0; sensor < 34; sensor++)
                {
                    token.ThrowIfCancellationRequested();
                    var group = sensor < 8 ? 0 : sensor < 16 ? 1 : sensor < 18 ? 2 : sensor < 26 ? 3 : 4;
                    var value = Sensitivity(group, options.TouchPanel.Sensitivities[group]);
                    stream.Write($"{{{side}{(char)(sensor + 0x41)}k{(char)value}}}");
                }
            }
            catch (TimeoutException) { BridgeLog.Write($"TouchPanel/{options.PlayerIndex}P", "灵敏度写入超时，继续启动扫描"); }
        }
        stream.Write("{STAT}");
    }

    internal static byte Sensitivity(int group, int value)
    {
        ReadOnlySpan<byte> a = [0x5A, 0x50, 0x46, 0x3C, 0x32, 0x28, 0x1E, 0x1A, 0x17, 0x14, 0x0A];
        ReadOnlySpan<byte> other = [0x46, 0x3C, 0x32, 0x28, 0x1E, 0x14, 0x0F, 0x0A, 0x05, 0x01, 0x01];
        return (group == 0 ? a : other)[value + 5];
    }

    private void WriteLeds(CancellationToken token)
    {
        using var stream = OpenSerial(options.Led);
        BridgeLog.Write($"LED/{options.PlayerIndex}P", $"已打开 {options.Led.PortName}");
        var colors = new byte[24];
        var sent = new byte[24];
        var forceUpdate = true;
        while (!token.IsCancellationRequested)
        {
            var start = Stopwatch.GetTimestamp();
            leds.CopyTo(colors);
            var changed = false;
            for (var i = 0; i < 8; i++)
            {
                var color = colors.AsSpan(i * 3, 3);
                if (!forceUpdate && options.Led.Throttler && color.SequenceEqual(sent.AsSpan(i * 3, 3))) continue;
                var packet = SerialLedProtocol.SetColor(i, color, options.Led.Brightness);
                stream.Write(packet, 0, packet.Length);
                changed = true;
            }
            if (changed)
            {
                stream.Write(SerialLedProtocol.UpdatePacket, 0, SerialLedProtocol.UpdatePacket.Length);
                colors.CopyTo(sent, 0);
                forceUpdate = false;
            }
            var remaining = options.Led.RefreshRateMs - Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (remaining > 0) Wait((int)Math.Ceiling(remaining), token);
        }
    }

    private static void Wait(int milliseconds, CancellationToken token)
    {
        if (token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested();
    }
}
