using System.Text.Json;

namespace IO2Pipe;

internal sealed class BridgeOptions
{
    public int ReconnectIntervalMs { get; set; } = 1000;
    public PlayerOptions[] Players { get; set; } =
    [
        new()
        {
            PlayerIndex = 1,
            TouchPanel = new() { PortName = "COM3" },
            Led = new() { PortName = "COM21" }
        },
        new()
        {
            PlayerIndex = 2,
            TouchPanel = new() { PortName = "COM4" },
            Led = new() { PortName = "COM22" }
        }
    ];

    public static BridgeOptions Load(string path)
    {
        var options = JsonSerializer.Deserialize(File.ReadAllText(path), BridgeJsonContext.Default.BridgeOptions)
            ?? throw new InvalidDataException("配置不能为空。");
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (ReconnectIntervalMs < 100) throw new InvalidDataException("ReconnectIntervalMs 必须 >= 100。");
        if (Players is not { Length: 2 } || Players.Any(p => p is null) ||
            !Players.Select(p => p.PlayerIndex).Order().SequenceEqual(new[] { 1, 2 }))
            throw new InvalidDataException("Players 必须分别配置 1P 和 2P，且编号不可重复。");
        foreach (var player in Players) player.Validate();
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in Players)
        {
            foreach (var serial in new SerialOptions[] { player.TouchPanel, player.Led })
                if (serial.Enabled && !ports.Add(serial.PortName.Trim()))
                    throw new InvalidDataException($"串口 {serial.PortName} 被多个设备重复配置。");
        }
        var buttons = Players.Select(p => p.ButtonRing).Where(b => b.Enabled).ToArray();
        if (buttons.Length == 2 && buttons[0].VendorId == buttons[1].VendorId &&
            buttons[0].ProductId == buttons[1].ProductId &&
            string.IsNullOrEmpty(buttons[0].DevicePath) != string.IsNullOrEmpty(buttons[1].DevicePath))
            throw new InvalidDataException("相同 VID/PID 的 IO4 不能混用自动选择和指定路径；共用设备时配置相同路径，独立设备时分别指定路径。");
    }
}

internal sealed class PlayerOptions
{
    public int PlayerIndex { get; set; } = 1;
    public Io4Options ButtonRing { get; set; } = new();
    public TouchOptions TouchPanel { get; set; } = new();
    public LedOptions Led { get; set; } = new();

    public void Validate()
    {
        if (PlayerIndex is not (1 or 2)) throw new InvalidDataException("PlayerIndex 必须为 1 或 2。");
        if (ButtonRing is null || TouchPanel is null || Led is null)
            throw new InvalidDataException("设备配置不能为 null。");
        if (ButtonRing.VendorId is < 1 or > 65535 || ButtonRing.ProductId is < 0 or > 65535)
            throw new InvalidDataException("IO4 VID/PID 超出范围（JSON 中使用十进制）。");
        if (ButtonRing.InputPlayerIndex is not (null or 1 or 2))
            throw new InvalidDataException("ButtonRing.InputPlayerIndex 必须为 null、1 或 2。");
        TouchPanel.Validate();
        Led.Validate();
        if (TouchPanel.Enabled && Led.Enabled && string.Equals(TouchPanel.PortName, Led.PortName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("触摸屏和 LED 不能占用同一个串口。");
        if (TouchPanel.Sensitivities is not { Length: 5 } || TouchPanel.Sensitivities.Any(s => s is < -5 or > 5))
            throw new InvalidDataException("Sensitivities 必须为 A/B/C/D/E 五个 -5 到 5 的整数。");
        if (Led.RefreshRateMs < 1 || !double.IsFinite(Led.Brightness) || Led.Brightness is < 0 or > 1)
            throw new InvalidDataException("LED RefreshRateMs 必须 > 0，Brightness 必须为 0 到 1。");
    }
}

internal sealed class Io4Options
{
    public bool Enabled { get; set; } = true;
    public int VendorId { get; set; } = 0x0CA3;
    public int ProductId { get; set; } = 0x0021;
    // 可从 --list 得到，用于同 VID/PID 多设备的精确选择。
    public string? DevicePath { get; set; }
    // null 表示使用目标玩家编号；独立 IO4 可将两边都设为 1。
    public int? InputPlayerIndex { get; set; }

    internal (int VendorId, int ProductId, string Path) DeviceKey =>
        (VendorId, ProductId, (DevicePath ?? string.Empty).ToUpperInvariant());
}

internal class SerialOptions
{
    public bool Enabled { get; set; } = true;
    public string PortName { get; set; } = "COM3";
    public int BaudRate { get; set; } = 9600;
    public void Validate()
    {
        if (Enabled && (string.IsNullOrWhiteSpace(PortName) || BaudRate <= 0))
            throw new InvalidDataException("启用的串口需要有效的 PortName 和 BaudRate。");
    }
}

internal sealed class TouchOptions : SerialOptions
{
    public bool OverrideSensitivity { get; set; } = true;
    public int[] Sensitivities { get; set; } = [0, 0, 0, 0, 0];
}

internal sealed class LedOptions : SerialOptions
{
    public LedOptions() { PortName = "COM21"; BaudRate = 115200; }
    public int RefreshRateMs { get; set; } = 16;
    public double Brightness { get; set; } = 1;
    public bool Throttler { get; set; } = true;
}
