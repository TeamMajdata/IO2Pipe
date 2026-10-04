using HidSharp;

namespace IO2Pipe;

internal sealed record ButtonRoute(Io4Options Options, int PlayerIndex, StatePipe Output);

// 同一个物理 IO4 只打开一次，每个报告分发到需要它的玩家。
internal sealed class Io4Reader(ButtonRoute[] routes, int retryMs)
{
    internal static Io4Reader[] Create(IEnumerable<ButtonRoute> routes, int retryMs) =>
        routes.Where(r => r.Options.Enabled).GroupBy(r => r.Options.DeviceKey)
            .Select(group => new Io4Reader(group.ToArray(), retryMs)).ToArray();

    internal void Publish(ReadOnlySpan<byte> report)
    {
        foreach (var route in routes)
            route.Output.Publish(Io4Protocol.Decode(report, route.Options.InputPlayerIndex ?? route.PlayerIndex));
    }

    public async Task RunAsync(CancellationToken token)
    {
        var name = $"IO4/{string.Join('+', routes.Select(r => $"{r.PlayerIndex}P"))}";
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Factory.StartNew(() => Read(name, token), token,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
            {
                BridgeLog.Write(name, $"设备不可用，稍后重试: {e.Message}");
            }
            finally
            {
                foreach (var route in routes) route.Output.Publish(0);
            }
            await Task.Delay(retryMs, token);
        }
    }

    private void Read(string name, CancellationToken token)
    {
        var config = routes[0].Options;
        var devices = DeviceList.Local.GetHidDevices(config.VendorId, config.ProductId)
            .Where(d => string.IsNullOrEmpty(config.DevicePath) ||
                string.Equals(d.DevicePath, config.DevicePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (devices.Length == 0) throw new IOException("未找到指定 IO4，请检查 VID/PID 和 DevicePath。");
        if (devices.Length > 1) throw new IOException("找到多个匹配 IO4，请用 --list 查看并为每位玩家配置 DevicePath。");
        var device = devices[0];
        using var stream = device.Open();
        stream.ReadTimeout = 500;
        var buffer = new byte[device.GetMaxInputReportLength()];
        if (buffer.Length < 33) throw new IOException("设备的 HID 输入报告过短，不符合 IO4 格式。");
        BridgeLog.Write(name, $"已打开 {device.DevicePath}");
        while (!token.IsCancellationRequested)
        {
            int read;
            try { read = stream.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { continue; }
            if (read == 0) throw new EndOfStreamException("IO4 已断开。");
            if (read >= 33) Publish(buffer.AsSpan(0, read));
        }
    }
}
