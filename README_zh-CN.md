# IO2Pipe

[English](README.md) | 简体中文

IO2Pipe 是一个面向 MajdataPlay 的双玩家外部 IO 桥接器。它使用 .NET 10，将 IO4 HID 按键、串口触摸屏和串口 LED 控制器接入 MajdataPlay 的 Pipe 设备后端。

## 功能

- 一个进程同时服务 1P 和 2P，并为每位玩家创建独立的按键、触摸和 LED 命名管道。
- 支持两位玩家共用一块 IO4，也支持每位玩家使用独立 IO4。
- IO4、触摸屏和 LED 可分别启用或禁用，硬件断线后自动重连。
- 保留输入状态变化的顺序；游戏重连后立即发送最新状态。
- 支持 LED 亮度缩放、仅发送变化颜色，以及断开或超时后的熄灯策略。
- 内置无需物理硬件的协议、双玩家隔离和重连自测。

## 工作方式

IO2Pipe 实现 [MajdataPlay 外部 IO 管理器 Pipe 协议](https://docs.majdata.net/majdataplay/development/external-io-manager)。MajdataPlay 作为命名管道客户端，IO2Pipe 作为服务端。每位玩家使用三个管道：

| 管道名称 | 方向 | Payload |
| --- | --- | --- |
| `MajdataPlay.IO.ButtonRing.{PlayerIndex}P` | IO2Pipe → 游戏 | 8 字节小端 `UInt64`；bit 0–7 为 BA1–BA8，bit 8–11 为 TEST/P1/SERVICE/P2 |
| `MajdataPlay.IO.TouchPanel.{PlayerIndex}P` | IO2Pipe → 游戏 | 8 字节小端 `UInt64`；bit 0–33 为 A/B/C/D/E 区，bit 34 为实现保留位 |
| `MajdataPlay.IO.Led.{PlayerIndex}P` | 游戏 → IO2Pipe | 若干 `[index, R, G, B]` 四字节记录；index 为 0–7 |

`{PlayerIndex}` 为 `1` 或 `2`。所有帧使用固定 7 字节包头：`48 04 type versionLE16 payloadLengthLE16`，当前协议版本为 `0`。1P 断开或重连不会中断 2P，反之亦然。

触摸位映射为 bit 0–7 = A1–A8、8–15 = B1–B8、16–17 = C1–C2、18–25 = D1–D8、26–33 = E1–E8。当前硬件解析器会保留串口状态的 bit 34；公开区域映射不使用该位。

## 环境要求

- Windows（MajdataPlay Pipe 后端和本项目当前面向 Windows 命名管道）
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)；使用已发布的自包含版本时无需单独安装运行时
- 兼容的 IO4 HID、串口触摸屏和/或串口 LED 控制器
- 支持 Pipe 输入的 MajdataPlay 版本

## 快速开始

在解决方案根目录执行以下命令。

1. 列出 HID 与串口设备，确认 IO4 的 VID/PID、设备路径和串口名：

   ```powershell
   dotnet run --project src -c Release -- --list
   ```

2. 修改 [`src/io2pipe.json`](src/io2pipe.json)，然后启动桥接器：

   ```powershell
   dotnet run --project src -c Release -- --config src/io2pipe.json
   ```

3. 在两份 MajdataPlay 中将 IO Manufacturer 设为 `Pipe`，按需启用 ButtonRing、TouchPanel 和 LED。1P 设置 `PlayerIndex = 1`，2P 设置 `PlayerIndex = 2`。两份游戏共用一个 IO2Pipe 进程。

> [!IMPORTANT]
> 随项目保留的游戏参考代码存在接入缺陷。联调前请阅读 [GAME_INTEGRATION.md](GAME_INTEGRATION.md)，尤其注意 LED Pipe 线程初始化、输入半包/粘包处理、EOF 处理和重连后的完整 LED 状态。

未传入 `--config` 时，程序读取可执行文件目录下的 `io2pipe.json`。构建和发布会自动复制示例配置。也可创建一份新配置；为避免误覆盖，目标文件必须不存在：

```powershell
dotnet run --project src -c Release -- --init my-io.json
```

按 `Ctrl+C` 可正常关闭管道和设备。游戏正常断开 LED 管道，或超过 3 秒未发送 LED 数据时，运行中的管理器会清空该玩家的灯色。管理器本身退出时不保证物理 LED 已熄灭。

## 配置

配置顶层包含全局 `ReconnectIntervalMs` 和 `Players` 数组。`Players` 必须恰好包含 `PlayerIndex` 为 `1` 和 `2` 的两项。

| 配置项 | 说明 |
| --- | --- |
| `ReconnectIntervalMs` | 设备或游戏断线后的重试间隔，最小 100 ms |
| `PlayerIndex` | 目标游戏编号 1 或 2；决定管道后缀和触摸初始化的 L/R |
| `ButtonRing.Enabled` | 是否读取 IO4 |
| `ButtonRing.VendorId` / `ProductId` | JSON 中使用十进制；示例 `3235` / `33` 对应 `0x0CA3` / `0x0021` |
| `ButtonRing.DevicePath` | 默认 `null`；同 VID/PID 存在多个 HID 时，必须从 `--list` 复制精确路径 |
| `ButtonRing.InputPlayerIndex` | IO4 的物理输入组 1 或 2；`null` 时跟随目标 `PlayerIndex` |
| `TouchPanel.Enabled` | 是否访问触摸串口 |
| `TouchPanel.PortName` / `BaudRate` | 示例为 1P `COM3`、2P `COM4`，波特率均为 9600；请按实际设备修改 |
| `TouchPanel.OverrideSensitivity` | 是否在初始化时发送灵敏度覆盖指令 |
| `TouchPanel.Sensitivities` | A/B/C/D/E 五组灵敏度，每项范围为 -5 到 5 |
| `Led.Enabled` | 是否访问 LED 串口 |
| `Led.PortName` / `BaudRate` | 示例为 1P `COM21`、2P `COM22`，波特率均为 115200；请按实际设备修改 |
| `Led.RefreshRateMs` | LED 串口更新周期，最小 1 ms；较慢的控制板可设为 100 ms |
| `Led.Brightness` | 0 到 1；写入串口前缩放 RGB |
| `Led.Throttler` | 仅写入变化的颜色；串口重新打开后仍会强制发送全部八灯 |

两位玩家启用的触摸和 LED 串口必须互不重复。六个 Pipe 管道始终创建；禁用硬件后，对应输入保持为零，或不向 LED 串口输出。Pipe 协议没有硬件连接状态字段，因此游戏中的 `IsConnected` 只表示已连接 IO2Pipe；请从 IO2Pipe 控制台日志确认物理硬件状态。

IO4 有两种常见接法：

- **共用一块 IO4：** 两项 ButtonRing 配置相同的 VID/PID 和 DevicePath（只有一块匹配设备时可均为 `null`）。`InputPlayerIndex` 分别设为 1 和 2。功能键是共享的板级输入，因此两位玩家会收到相同的 TEST/P1/SERVICE/P2 状态。
- **每位玩家一块 IO4：** 分别配置两块板的 DevicePath。如果两边按钮均接在各自板的 1P 输入组，可将两个 `InputPlayerIndex` 都设为 1；游戏端 `PlayerIndex` 仍分别为 1 和 2。

同 VID/PID 的两块 IO4 不允许一边自动选择、另一边指定路径，以避免两个读取器误用同一设备。

## 命令行

```text
IO2Pipe [--config <path>] | --init <path> | --list | --self-test
```

| 参数 | 用途 |
| --- | --- |
| 无参数 | 读取可执行文件目录中的 `io2pipe.json` 并启动 |
| `--config <path>` | 使用指定配置启动 |
| `--init <path>` | 创建默认配置，不覆盖已有文件 |
| `--list` | 列出可用 HID 与串口设备 |
| `--self-test` | 运行不访问物理硬件的内置测试 |
| `--help` / `-h` | 显示帮助 |

## 构建、测试与发布

```powershell
dotnet build IO2Pipe.slnx -c Release
dotnet run --project src -c Release --no-build -- --self-test
```

自测覆盖协议字节序、拆包/粘包和错误帧恢复、按键与触摸位映射、LED 校验和与亮度、双玩家隔离、共享/独立 IO4、快速输入顺序、重连、配置校验和正常取消。测试使用模拟 HID/串口报告，不会访问真实设备；物理硬件、驱动和游戏端仍需实际联调。

发布 Windows x64 自包含版本：

```powershell
dotnet publish src/IO2Pipe.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

发布后编辑 `publish/win-x64/io2pipe.json`，再运行 `IO2Pipe.exe`。

## 项目结构

| 路径 | 内容 |
| --- | --- |
| `src/Program.cs` | 命令行入口和生命周期管理 |
| `src/IoManager.cs` | 双玩家任务组织和管道创建 |
| `src/Io4Reader.cs` | IO4 共享读取与输入分发 |
| `src/HardwareDevices.cs` | 触摸与 LED 串口访问 |
| `src/DeviceProtocols.cs` | IO4、触摸和 LED 硬件协议映射 |
| `src/PipeProtocol.cs` | Pipe 帧编解码 |
| `src/PipeServer.cs` | 命名管道服务与状态管理 |
| `src/SelfTests.cs`, `src/DualPlayerTests.cs` | 无硬件自测 |
| `src/Dummy/` | 不参与编译的 MajdataPlay 参考源码 |

实现使用 HidSharp 读取 HID，使用 `System.IO.Ports` 访问串口。触摸串口采用 8N1 并启用 DTR/RTS；输入状态在变化时立即发布，空闲时每 500 ms 发送最新快照。慢客户端导致 8192 项队列写满时会被断开，重连后从最新状态恢复，断线期间的历史输入不会回放。
