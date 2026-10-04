# IO2Pipe

English | [Simplified Chinese](README_zh-CN.md)

IO2Pipe is a dual-player external I/O bridge for MajdataPlay. Built with .NET 10, it connects IO4 HID buttons, serial touch panels, and serial LED controllers to MajdataPlay's Pipe device backend.

## Features

- Serves player 1 and player 2 from one process, with independent button, touch, and LED named pipes for each player.
- Supports either one shared IO4 or one dedicated IO4 per player.
- Button, touch, and LED hardware can be enabled independently and reconnect automatically after failures.
- Preserves the order of input samples and sends the latest state immediately after the game reconnects.
- Supports LED brightness scaling, changed-color throttling, and clearing LEDs after disconnects or timeouts.
- Includes hardware-free self-tests for framing, dual-player isolation, and reconnection behavior.

## How it works

IO2Pipe implements the [MajdataPlay External I/O Manager Pipe protocol](https://docs.majdata.net/majdataplay/development/external-io-manager). MajdataPlay is the named-pipe client and IO2Pipe is the server. Each player uses three pipes:

| Pipe name | Direction | Payload |
| --- | --- | --- |
| `MajdataPlay.IO.ButtonRing.{PlayerIndex}P` | IO2Pipe → game | 8-byte little-endian `UInt64`; bits 0–7 are BA1–BA8 and bits 8–11 are TEST/P1/SERVICE/P2 |
| `MajdataPlay.IO.TouchPanel.{PlayerIndex}P` | IO2Pipe → game | 8-byte little-endian `UInt64`; bits 0–33 map to the A/B/C/D/E areas and bit 34 is implementation-reserved |
| `MajdataPlay.IO.Led.{PlayerIndex}P` | game → IO2Pipe | Zero or more four-byte `[index, R, G, B]` records; index is 0–7 |

`{PlayerIndex}` is `1` or `2`. Every frame has a 7-byte header: `48 04 type versionLE16 payloadLengthLE16`. The current protocol version is `0`. A disconnect or reconnect for one player does not interrupt the other player.

Touch bits are mapped as follows: 0–7 = A1–A8, 8–15 = B1–B8, 16–17 = C1–C2, 18–25 = D1–D8, and 26–33 = E1–E8. The current hardware parser preserves serial-state bit 34, which is not part of the public area mapping.

## Requirements

- Windows (the MajdataPlay Pipe backend and this project currently target Windows named pipes)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), unless you use a self-contained published build
- A compatible IO4 HID device, serial touch panel, and/or serial LED controller
- A MajdataPlay version with Pipe input support

## Quick start

Run these commands from the solution root.

1. List HID and serial devices to find the IO4 VID/PID, device path, and COM ports:

   ```powershell
   dotnet run --project src -c Release -- --list
   ```

2. Edit [`src/io2pipe.json`](src/io2pipe.json), then start the bridge:

   ```powershell
   dotnet run --project src -c Release -- --config src/io2pipe.json
   ```

3. In both MajdataPlay instances, set IO Manufacturer to `Pipe` and enable ButtonRing, TouchPanel, and LED as needed. Set `PlayerIndex = 1` for the 1P game and `PlayerIndex = 2` for the 2P game. Run only one IO2Pipe process.

> [!IMPORTANT]
> The game-side reference code retained with this project has integration defects. Read [GAME_INTEGRATION.md](GAME_INTEGRATION.md) before hardware testing, especially the notes about starting the LED Pipe loop, retaining partial input frames, handling EOF, and forcing a complete LED state after reconnecting.

Without `--config`, IO2Pipe loads `io2pipe.json` from the executable directory. The sample file is copied during build and publish. You can also create a fresh configuration; the command refuses to overwrite an existing file:

```powershell
dotnet run --project src -c Release -- --init my-io.json
```

Press `Ctrl+C` to close pipes and devices cleanly. If a game disconnects its LED pipe normally, or sends no LED data for more than three seconds, the running manager clears that player's LED state. Physical LEDs are not guaranteed to turn off when the manager process itself exits.

## Configuration

The top level contains a global `ReconnectIntervalMs` and a `Players` array. `Players` must contain exactly two entries with `PlayerIndex` values `1` and `2`.

| Setting | Description |
| --- | --- |
| `ReconnectIntervalMs` | Retry delay after a device or game disconnects; minimum 100 ms |
| `PlayerIndex` | Target game number, 1 or 2; selects the pipe suffix and touch-panel L/R initialization |
| `ButtonRing.Enabled` | Enables IO4 input |
| `ButtonRing.VendorId` / `ProductId` | Decimal values in JSON; the sample `3235` / `33` is `0x0CA3` / `0x0021` |
| `ButtonRing.DevicePath` | `null` by default; copy the exact path from `--list` when multiple HID devices share a VID/PID |
| `ButtonRing.InputPlayerIndex` | Physical IO4 input group 1 or 2; `null` follows the target `PlayerIndex` |
| `TouchPanel.Enabled` | Enables touch-panel serial access |
| `TouchPanel.PortName` / `BaudRate` | Sample: `COM3` for 1P and `COM4` for 2P, both at 9600; change these for your hardware |
| `TouchPanel.OverrideSensitivity` | Sends sensitivity override commands during initialization |
| `TouchPanel.Sensitivities` | Five A/B/C/D/E sensitivity values, each from -5 to 5 |
| `Led.Enabled` | Enables LED serial access |
| `Led.PortName` / `BaudRate` | Sample: `COM21` for 1P and `COM22` for 2P, both at 115200; change these for your hardware |
| `Led.RefreshRateMs` | LED serial update period; minimum 1 ms; slower controllers may need 100 ms |
| `Led.Brightness` | Value from 0 to 1 used to scale RGB before serial output |
| `Led.Throttler` | Writes only changed colors; all eight LEDs are still forced after reopening the serial port |

All enabled touch and LED COM ports must be unique across both players. All six Pipe endpoints are always created. A disabled input reports zero; a disabled LED device does not write to serial. The Pipe protocol has no physical-device status field, so the game's `IsConnected` only means it is connected to IO2Pipe. Check the IO2Pipe console log for hardware status.

Two IO4 layouts are supported:

- **One shared IO4:** Configure the same VID/PID and DevicePath for both ButtonRing entries. Both paths may be `null` when exactly one device matches. Set `InputPlayerIndex` to 1 and 2 respectively. Function buttons are shared board-level inputs, so both players receive the same TEST/P1/SERVICE/P2 state.
- **One IO4 per player:** Configure the two DevicePath values explicitly. If both button sets use input group 1 on their respective boards, set both `InputPlayerIndex` values to 1. The game-side `PlayerIndex` values remain 1 and 2.

When two boards have the same VID/PID, do not mix automatic selection on one player with an explicit path on the other; validation rejects this ambiguous setup.

## Command line

```text
IO2Pipe [--config <path>] | --init <path> | --list | --self-test
```

| Option | Purpose |
| --- | --- |
| No option | Loads `io2pipe.json` from the executable directory and starts the bridge |
| `--config <path>` | Starts with the specified configuration |
| `--init <path>` | Creates a default configuration without overwriting an existing file |
| `--list` | Lists available HID and serial devices |
| `--self-test` | Runs built-in tests without physical hardware |
| `--help` / `-h` | Shows help |

## Build, test, and publish

```powershell
dotnet build IO2Pipe.slnx -c Release
dotnet run --project src -c Release --no-build -- --self-test
```

The self-tests cover byte order, fragmented/coalesced frames, invalid-frame recovery, button and touch mappings, LED checksums and brightness, player isolation, shared and independent IO4 layouts, rapid input ordering, reconnects, configuration validation, and cancellation. Tests use simulated HID/serial reports and never access physical devices. Real hardware, drivers, and the game-side integration still require on-device testing.

Publish a self-contained Windows x64 build:

```powershell
dotnet publish src/IO2Pipe.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

Edit `publish/win-x64/io2pipe.json`, then run `IO2Pipe.exe`.

## Project layout

| Path | Purpose |
| --- | --- |
| `src/Program.cs` | CLI entry point and process lifetime |
| `src/IoManager.cs` | Dual-player task orchestration and pipe creation |
| `src/Io4Reader.cs` | Shared IO4 reads and input routing |
| `src/HardwareDevices.cs` | Touch and LED serial access |
| `src/DeviceProtocols.cs` | IO4, touch, and LED hardware mappings |
| `src/PipeProtocol.cs` | Pipe frame encoding and decoding |
| `src/PipeServer.cs` | Named-pipe servers and state management |
| `src/SelfTests.cs`, `src/DualPlayerTests.cs` | Hardware-free self-tests |
| `src/Dummy/` | MajdataPlay reference sources, excluded from compilation |

The implementation uses HidSharp for HID input and `System.IO.Ports` for serial devices. The touch port uses 8N1 with DTR/RTS enabled. Input changes are published immediately, with a latest-state snapshot every 500 ms while idle. A client that fills the 8,192-item queue is disconnected; after reconnecting it receives the latest state, not the input history accumulated while disconnected.
