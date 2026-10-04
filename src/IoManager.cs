namespace IO2Pipe;

internal sealed class PlayerIo(PlayerOptions options)
{
    public PlayerOptions Options { get; } = options;
    public StatePipe Buttons { get; } = new();
    public StatePipe Touch { get; } = new();
    public LedState Leds { get; } = new();
}

internal sealed class IoManager
{
    private readonly BridgeOptions _options;
    internal IReadOnlyList<PlayerIo> Players { get; }

    public IoManager(BridgeOptions options)
    {
        options.Validate();
        _options = options;
        Players = options.Players.OrderBy(p => p.PlayerIndex).Select(p => new PlayerIo(p)).ToArray();
    }

    public async Task RunAsync(CancellationToken token, string pipePrefix = "MajdataPlay.IO")
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = lifetime.Token;
        var retryMs = _options.ReconnectIntervalMs;
        var tasks = new List<Task>();
        foreach (var player in Players)
        {
            var config = player.Options;
            var suffix = $"{config.PlayerIndex}P";
            tasks.Add(player.Buttons.RunAsync($"{pipePrefix}.ButtonRing.{suffix}", retryMs, ct));
            tasks.Add(player.Touch.RunAsync($"{pipePrefix}.TouchPanel.{suffix}", retryMs, ct));
            tasks.Add(PipeServer.RunAsync($"{pipePrefix}.Led.{suffix}", retryMs, player.Leds.ServeAsync, ct));
            var hardware = new HardwareDevices(config, retryMs, player.Touch, player.Leds);
            if (config.TouchPanel.Enabled) tasks.Add(hardware.RunTouchAsync(ct));
            if (config.Led.Enabled) tasks.Add(hardware.RunLedAsync(ct));
        }
        var readers = Io4Reader.Create(Players.Select(p =>
            new ButtonRoute(p.Options.ButtonRing, p.Options.PlayerIndex, p.Buttons)), retryMs);
        tasks.AddRange(readers.Select(reader => reader.RunAsync(ct)));
        try
        {
            // 任一后台任务意外退出时停止整个管理器，避免部分故障被隐藏。
            var completed = await Task.WhenAny(tasks);
            await completed;
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }
}
