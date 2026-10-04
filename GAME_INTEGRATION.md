# 游戏端接入修正

本项目实现外部管理器；`Dummy/` 是不参与编译的游戏参考源码。下面的修改需应用到实际 Unity 游戏工程。只修改本项目 Dummy 文件不会改变已安装游戏。

## 1. 启动 LED 的管道线程

提供的 `OutputManager.LedDevice.cs` 虽有 `PipeUpdateLoop`，但 `Init()` 的 manufacturer switch 没有 Pipe 分支。增加：

```csharp OutputManager.LedDevice.cs
case DeviceManufacturerOption.Pipe:
    _ledDeviceUpdateLoop = Task.Factory.StartNew(
        PipeUpdateLoop, TaskCreationOptions.LongRunning);
    break;
```

否则即使外部管理器正常运行，游戏也不会连接 LED 管道。

## 2. 修正 PipePacket.Parse 的半包死循环

提供的解析器在收到完整包头、但 payload 尚未收齐时，既不消费数据也不返回，会一直占用 IO 线程。计算并验证 `packetLen` 后，在完整包处理之前增加：

```csharp PipePacket.cs
if (data.Length < packetLen)
{
    return; // 保留半包，等待下一次 Read 补齐。
}
```

## 3. 保留输入管道的半包，并处理 EOF

ButtonRing 和 TouchPanel 的 `PipeUpdateLoop` 都需要：

- 从每轮读循环的 `finally` 移除 `buffer.Clear()`。解析器会 Skip 已消费的数据，剩余半包必须保留；建立新连接时才新建/清空累积缓冲区。
- `Read` 返回 0 时退出本次连接并进入重连；否则游戏会在已关闭的管道上不断空读。
- 在连接的 `finally` 中将 `IsConnected` 设为 false，并在锁内释放全部实时状态、将 HadOff 标志设为 true，避免管理器停止后卡键。
- TouchPanel 的 pollingRate 应读取 `_sensorPollingRateMs`。
- 两个 stackalloc 缓冲区应移到重连循环之前分配，每次连接重新构造 SpanBuffer，避免持续重连累积栈空间。

读取部分的目标逻辑：

```csharp InputManager.ButtonRing.cs
var read = pipeClientStream.Read(rawBuffer);
if (read == 0)
{
    IsConnected = false;
    break;
}
buffer.Write(rawBuffer.Slice(0, read));
PipePacket.Parse(ref buffer, 1024, callback);
// 不清空 buffer，保留尚未组成完整包的数据。
```

命名管道是字节流，服务端一次 Write 并不保证客户端一次 Read 得到整个包。外部管理器逐包写入也无法代替这些修正。

## 4. 移除不支持的 PipeStream 超时属性

参考的三个 `NamedPipeClientStream` 构造后都设置了 `ReadTimeout/WriteTimeout`。标准 .NET PipeStream 不支持这些属性，赋值可能在连接前就抛出 `InvalidOperationException`。使用：

```csharp OutputManager.LedDevice.cs
var pipeClientStream = new NamedPipeClientStream(
    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
```

`Connect(2000)` 的连接超时可以保留。读写需要独立的取消机制，例如使用带 CancellationToken 的异步 API，或全局取消时 Dispose 当前 pipe 以中断同步 Read，并按取消状态处理相应异常。仅在同步 Read 之前检查 token 无法停止已经阻塞的读取。

外部管理器已使用异步管道 API 和可取消的超时，不设置上述属性。

## 5. LED 每次连接后发送完整颜色

原 LED `latestReports` 缓存在重连后仍保留；启用 Throttler 时，新启动的管理器可能只收到心跳，直到颜色变化才收到部分灯色。每次 Connect 成功后设置 `forceUpdate = true`，使第一轮发出全部八个颜色；只有完整 Write 成功后才设为 false。

```csharp OutputManager.LedDevice.cs
// 每次连接成功后，进入发送循环之前：
var forceUpdate = true;

// 八灯循环内的跳过条件：
if (!forceUpdate && latestReport.Color == color && _isThrottlerEnabled)
{
    continue;
}

// report 完整写入成功后：
forceUpdate = false;
```

## 6. 初始化防重入

三个参考类的 Init 使用了反向的 CompareExchange 参数。正确的“从 0 置为 1，已经初始化则返回”写法为：

```csharp InputManager.ButtonRing.cs
if (Interlocked.CompareExchange(ref _isInited, 1, 0) != 0)
{
    return;
}
```

上述改动也适用于 TouchPanel 和 LedDevice。它不影响本管理器的启动，但可防止游戏重复调用 Init 时启动多条 IO 线程。
