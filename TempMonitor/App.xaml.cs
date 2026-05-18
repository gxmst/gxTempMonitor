using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TempMonitor;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Global\gxTempMonitor.SingleInstance";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("gxTempMonitor 已经在运行。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        int delaySeconds = ParseDelayFromArgs(e);
        if (delaySeconds <= 0)
            delaySeconds = ReadDelayFromConfig();

        if (delaySeconds > 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(delaySeconds * 1000);
                Dispatcher.Invoke(() =>
                {
                    _ = HardwareMonitorService.Instance;
                    MainWindow = new MainWindow();
                    MainWindow.Show();
                });
            });
        }
        else
        {
            _ = HardwareMonitorService.Instance;
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
    }

    private static int ParseDelayFromArgs(StartupEventArgs e)
    {
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (string.Equals(e.Args[i], "--delay", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(e.Args[i + 1], out int seconds))
                    return Math.Clamp(seconds, 0, 60);
            }
        }
        return -1;
    }

    private static int ReadDelayFromConfig()
    {
        try
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? Environment.ProcessPath
                ?? AppContext.BaseDirectory;
            string exeDir = System.IO.Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            string configPath = System.IO.Path.Combine(exeDir, "config.json");

            if (!System.IO.File.Exists(configPath)) return 0;

            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            var config = JsonSerializer.Deserialize<AppConfig>(System.IO.File.ReadAllText(configPath), options);
            return config?.DelayedStartSeconds ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        HardwareMonitorService.Instance.Dispose();

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (AbandonedMutexException)
        {
        }
        catch (ApplicationException)
        {
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
