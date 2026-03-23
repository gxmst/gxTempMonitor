using System;
using System.Threading;
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

        _ = HardwareMonitorService.Instance;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        HardwareMonitorService.Instance.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
