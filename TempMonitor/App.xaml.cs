using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TempMonitor;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the Application lifetime; OnExit deterministically disposes both fields.")]
public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\gxTempMonitor.SingleInstance";

    private readonly CancellationTokenSource _startupCancellation = new();
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            AppConfig config = ConfigStore.Load();
            int delaySeconds = ParseDelayFromArgs(e);
            if (delaySeconds < 0 && IsStartupInvocation(e.Args))
                delaySeconds = config.DelayedStartSeconds;
            else if (delaySeconds < 0)
                delaySeconds = 0;

            if (delaySeconds > 0)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Clamp(delaySeconds, 0, 60)),
                    _startupCancellation.Token);
            }

            if (_startupCancellation.IsCancellationRequested) return;

            // A delayed startup instance must not reserve the single-instance mutex:
            // a user launching the app manually should be able to start immediately.
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                SingleInstanceMutexName,
                out bool createdNew);
            _ownsSingleInstanceMutex = createdNew;

            if (!createdNew)
            {
                Shutdown();
                return;
            }

            HardwareMonitorService.Instance.Configure(
                config.SamplingIntervalSeconds,
                config.TrackTopGpuProcess);

            MainWindow = new MainWindow(config);
            MainWindow.Show();
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TryLogStartupFailure(ex);
            if (!IsStartupInvocation(e.Args))
            {
                System.Windows.MessageBox.Show(
                    "gxTempMonitor 启动失败，详细信息已写入日志。",
                    "gxTempMonitor",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            Shutdown(1);
        }
    }

    private static int ParseDelayFromArgs(StartupEventArgs e)
    {
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (!string.Equals(e.Args[i], "--delay", StringComparison.OrdinalIgnoreCase))
                continue;

            return int.TryParse(e.Args[i + 1], out int seconds)
                ? Math.Clamp(seconds, 0, 60)
                : 0;
        }

        return -1;
    }

    private static bool IsStartupInvocation(string[] args) =>
        args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));

    private static void TryLogStartupFailure(Exception exception)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            File.AppendAllText(
                AppPaths.LogPath,
                $"[{DateTimeOffset.Now:O}] startup: {exception}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupCancellation.Cancel();

        if (HardwareMonitorService.IsValueCreated)
            HardwareMonitorService.Instance.Dispose();

        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _startupCancellation.Dispose();
        base.OnExit(e);
    }
}
