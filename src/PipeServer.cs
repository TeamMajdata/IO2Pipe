using System.IO.Pipes;
using System.Threading.Channels;

namespace IO2Pipe;

internal static class BridgeLog
{
    public static void Write(string source, string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{source}] {message}");
}

internal static class PipeServer
{
    public static async Task RunAsync(string name, int retryMs,
        Func<NamedPipeServerStream, CancellationToken, Task> serve, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192);
                BridgeLog.Write(name, "等待游戏连接");
                await pipe.WaitForConnectionAsync(token);
                BridgeLog.Write(name, "已连接");
                await serve(pipe, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                BridgeLog.Write(name, $"连接结束: {e.Message}");
            }
            await Task.Delay(retryMs, token);
        }
    }
}

internal sealed class StatePipe
{
    private readonly object _gate = new();
    private ulong _latest;
    private Channel<ulong>? _reports;
    private CancellationTokenSource? _connection;

    public void Publish(ulong state)
    {
        lock (_gate)
        {
            _latest = state;
            // 保留每次采样的顺序。游戏长期不读时断开，重连发送最新快照。
            if (_reports is not null && !_reports.Writer.TryWrite(state))
                _connection!.Cancel();
        }
    }

    public Task RunAsync(string name, int retryMs, CancellationToken token) =>
        PipeServer.RunAsync(name, retryMs, ServeAsync, token);

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var reports = Channel.CreateBounded<ulong>(new BoundedChannelOptions(8192)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_gate)
        {
            reports.Writer.TryWrite(_latest);
            _reports = reports;
            _connection = connection;
        }
        try
        {
            var available = reports.Reader.WaitToReadAsync(connection.Token).AsTask();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            var tick = timer.WaitForNextTickAsync(connection.Token).AsTask();
            while (true)
            {
                await Task.WhenAny(available, tick);
                connection.Token.ThrowIfCancellationRequested();
                if (available.IsCompleted)
                {
                    if (!await available) break;
                    while (reports.Reader.TryRead(out var state))
                        await WriteAsync(pipe, PipeProtocol.EncodeState(state), connection.Token);
                    available = reports.Reader.WaitToReadAsync(connection.Token).AsTask();
                }
                if (tick.IsCompleted)
                {
                    if (!await tick) break;
                    // 与硬件报告一起入队，避免快照越过尚未发送的状态变化。
                    lock (_gate)
                    {
                        if (!reports.Writer.TryWrite(_latest)) connection.Cancel();
                    }
                    tick = timer.WaitForNextTickAsync(connection.Token).AsTask();
                }
            }
        }
        finally
        {
            lock (_gate) { _reports = null; _connection = null; }
            connection.Cancel();
            reports.Writer.TryComplete();
        }
    }

    private static async Task WriteAsync(Stream pipe, byte[] packet, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(2000);
        await pipe.WriteAsync(packet, timeout.Token);
    }
}

internal sealed class LedState
{
    private readonly object _gate = new();
    private readonly byte[] _colors = new byte[24];

    public void Receive(PacketType type, ushort version, ReadOnlySpan<byte> payload)
    {
        if (version != 0 || type != PacketType.Report || payload.Length % 4 != 0) return;
        // 整包验证后再应用，避免无效指令只更新一部分灯。
        for (var i = 0; i < payload.Length; i += 4)
            if (payload[i] >= 8) return;
        lock (_gate)
            for (var i = 0; i < payload.Length; i += 4)
                payload.Slice(i + 1, 3).CopyTo(_colors.AsSpan(payload[i] * 3, 3));
    }

    public void CopyTo(Span<byte> destination)
    {
        lock (_gate) _colors.CopyTo(destination);
    }

    public void Clear()
    {
        lock (_gate) Array.Clear(_colors);
    }

    public async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var decoder = new PacketDecoder();
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(3000);
                var read = await pipe.ReadAsync(buffer, timeout.Token);
                if (read == 0) return;
                decoder.Feed(buffer.AsSpan(0, read), Receive);
            }
        }
        finally { Clear(); }
    }
}
