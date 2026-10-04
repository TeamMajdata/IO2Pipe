using HidSharp;
using System.Text.Json;

namespace IO2Pipe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--help"] or ["-h"])
            {
                Console.WriteLine("IO2Pipe [--config <path>] | --init <path> | --list | --self-test");
                return 0;
            }
            if (args is ["--init", var output])
            {
                using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
                JsonSerializer.Serialize(file, new BridgeOptions(), BridgeJsonContext.Default.BridgeOptions);
                Console.WriteLine($"已创建配置: {Path.GetFullPath(output)}，请按设备修改后运行。");
                return 0;
            }
            if (args is ["--list"])
            {
                foreach (var device in DeviceList.Local.GetHidDevices())
                    Console.WriteLine($"HID VID=0x{device.VendorID:X4} ({device.VendorID}) PID=0x{device.ProductID:X4} ({device.ProductID}) Path={device.DevicePath}");
                foreach (var device in DeviceList.Local.GetSerialDevices()) Console.WriteLine($"Serial: {device}");
                return 0;
            }
            if (args is ["--self-test"])
            {
                await SelfTests.RunAsync();
                return 0;
            }
            var path = args switch
            {
                [] => Path.Combine(AppContext.BaseDirectory, "io2pipe.json"),
                ["--config", var config] => Path.GetFullPath(config),
                _ => throw new ArgumentException("无效参数，使用 --help 查看帮助。")
            };
            var options = BridgeOptions.Load(path);
            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; shutdown.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                BridgeLog.Write("IO2Pipe", $"同时启动 1P 和 2P，共六个管道；配置: {path}");
                await new IoManager(options).RunAsync(shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            finally { Console.CancelKeyPress -= handler; }
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"IO2Pipe: {e.Message}");
            return 1;
        }
    }
}

