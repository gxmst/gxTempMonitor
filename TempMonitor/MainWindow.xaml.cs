using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor
{
    public class AppConfig
    {
        public double Top { get; set; } = 100;
        public double Left { get; set; } = 100;
        public bool IsDockedRight { get; set; } = false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MemoryStatusEx() { this.dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)); }
    }

    public class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    public partial class MainWindow : Window
    {
        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

        private LibreHardwareMonitor.Hardware.Computer _computer;
        private DispatcherTimer _timer;
        private DispatcherTimer _idleTimer; // 新增闲置计时器
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _recvCounter;
        private PerformanceCounter _sentCounter;
        private string _interfaceName;
        private UpdateVisitor _updateVisitor = new UpdateVisitor();

        private int _zeroTrafficSeconds = 0;
        private Dictionary<string, float> _maxValues = new Dictionary<string, float>
        {
            { "CPU", 0 }, { "GPU", 0 }, { "RAM", 0 }, { "VRAM", 0 }, { "UP", 0 }, { "DOWN", 0 }
        };

        private bool _isDockedRight = false;
        private const double BaseWidth = 135;
        private const double FullWidth = 205;
        private const double AnimDurationMs = 200;
        private string _configPath;

        public MainWindow()
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            var exeDir = Path.GetDirectoryName(exePath);
            Directory.SetCurrentDirectory(exeDir);
            _configPath = Path.Combine(exeDir, "config.json");
            try { File.WriteAllText(Path.Combine(exeDir, "debug_boot.txt"), $"Booted at {DateTime.Now}"); } catch { }

            InitializeComponent();
            LoadConfig();
            try
            {
                _computer = new LibreHardwareMonitor.Hardware.Computer { IsGpuEnabled = true };
                _computer.Open();
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                FindActiveNetworkInterface();
                UpdateStartupMenuItem();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"初始化失败: {ex.Message}");
                System.Windows.Application.Current.Shutdown();
            }

            // 主刷新计时器
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            // 闲置计时器初始化
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; // 5秒闲置即淡出
            _idleTimer.Tick += IdleTimer_Tick;
            _idleTimer.Start();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) { this.Topmost = true; EnsureWindowIsVisible(); }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath));
                    if (config != null) { this.Top = config.Top; this.Left = config.Left; _isDockedRight = config.IsDockedRight; }
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try { File.WriteAllText(_configPath, JsonSerializer.Serialize(new AppConfig { Top = this.Top, Left = this.Left, IsDockedRight = _isDockedRight })); }
            catch { }
        }

        private void EnsureWindowIsVisible()
        {
            double w = SystemParameters.PrimaryScreenWidth;
            double h = SystemParameters.PrimaryScreenHeight;
            if (this.Left < 0 || this.Left > w || this.Top < 0 || this.Top > h)
            { this.Top = 100; this.Left = 100; _isDockedRight = false; }
        }

        private void FindActiveNetworkInterface()
        {
            try
            {
                var category = new PerformanceCounterCategory("Network Interface");
                string[] instances = category.GetInstanceNames();
                string bestInstance = null; float maxTraffic = -1;
                foreach (var instance in instances)
                {
                    if (instance.Contains("Loopback") || instance.Contains("VMware") || instance.Contains("Virtual") || instance.Contains("Teredo") || instance.Contains("Pseudo")) continue;
                    using (var counter = new PerformanceCounter("Network Interface", "Bytes Total/sec", instance))
                    {
                        counter.NextValue(); System.Threading.Thread.Sleep(50); float val = counter.NextValue();
                        if (val > maxTraffic) { maxTraffic = val; bestInstance = instance; }
                    }
                }
                if (bestInstance != null)
                {
                    _interfaceName = bestInstance; _recvCounter?.Dispose(); _sentCounter?.Dispose();
                    _recvCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", _interfaceName);
                    _sentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", _interfaceName);
                }
            }
            catch { }
        }

        private void Timer_Tick(object sender, EventArgs e) => UpdateMetrics();

        private void UpdateMetrics()
        {
            try { float cpu = _cpuCounter.NextValue(); UpdateMax("CPU", cpu); CpuUsageText.Text = $"{cpu:0.0} %"; CpuMaxText.Text = $"{_maxValues["CPU"]:0.0} %"; CpuUsageText.Foreground = GetAlertBrush(cpu); } catch { }
            try { 
                MemoryStatusEx msex = new MemoryStatusEx();
                if (GlobalMemoryStatusEx(msex)) {
                    double used = msex.ullTotalPhys - msex.ullAvailPhys;
                    float usedGB = (float)(used / (1024.0 * 1024.0 * 1024.0));
                    float usage = (float)msex.dwMemoryLoad;
                    UpdateMax("RAM", usedGB); RamUsedText.Text = $"{usedGB:F1} GB"; RamMaxText.Text = $"{_maxValues["RAM"]:F1} GB"; RamUsedText.Foreground = GetAlertBrush(usage);
                }
            } catch { }
            try
            {
                _computer.Accept(_updateVisitor);
                float? gpuTemp = null; float? vramGB = null;
                foreach (IHardware hardware in _computer.Hardware)
                {
                    if (hardware.HardwareType.ToString().Contains("Gpu"))
                    {
                        var temp = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("GPU Core") || s.Name.Contains("Core")) && s.Value > 0);
                        if (temp != null) gpuTemp = temp.Value;
                        foreach (var sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.SmallData && sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                            {
                                if (sensor.Name.Contains("Shared") || sensor.Name.Contains("Total")) continue;
                                if (!sensor.Name.Contains("Dedicated") && sensor.Value > 24000) continue;
                                if (sensor.Value.HasValue && sensor.Value.Value > 0) { vramGB = sensor.Value.Value / 1024f; break; }
                            }
                        }
                    }
                }
                if (gpuTemp.HasValue) { UpdateMax("GPU", gpuTemp.Value); GpuTempText.Text = $"{gpuTemp.Value:0.0} °C"; GpuMaxText.Text = $"{_maxValues["GPU"]:0.0} °C"; GpuTempText.Foreground = GetAlertBrush(gpuTemp.Value); }
                if (vramGB.HasValue) { UpdateMax("VRAM", vramGB.Value); VramUsedText.Text = $"{vramGB.Value:F1} GB"; VramMaxText.Text = $"{_maxValues["VRAM"]:F1} GB"; }
            }
            catch { }
            if (_recvCounter != null && _sentCounter != null)
            {
                try
                {
                    float up = _sentCounter.NextValue(); float down = _recvCounter.NextValue();
                    if (up <= 0 && down <= 0) _zeroTrafficSeconds++; else _zeroTrafficSeconds = 0;
                    if (_zeroTrafficSeconds >= 5) { _zeroTrafficSeconds = 0; FindActiveNetworkInterface(); }
                    UpdateMax("UP", up); UpdateMax("DOWN", down);
                    NetUpText.Text = FormatSpeed(up); NetUpMaxText.Text = FormatSpeed(_maxValues["UP"]);
                    NetDownText.Text = FormatSpeed(down); NetDownMaxText.Text = FormatSpeed(_maxValues["DOWN"]);
                }
                catch { FindActiveNetworkInterface(); }
            }
        }

        private System.Windows.Media.Brush GetAlertBrush(float value)
        {
            if (value >= 90) return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF4444"));
            if (value >= 80) return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFA500"));
            return System.Windows.Media.Brushes.White;
        }

        private void UpdateMax(string key, float current) { if (current > _maxValues[key]) _maxValues[key] = current; }
        private string FormatSpeed(float bytesPerSec)
        {
            if (bytesPerSec < 1024) return $"{bytesPerSec:0.0} B/s";
            float kb = bytesPerSec / 1024;
            if (kb < 1024) return $"{kb:0.0} KB/s";
            return $"{(kb / 1024.0):0.1} MB/s";
        }

        private void CpuRow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) { try { Process.Start("taskmgr.exe"); } catch { } e.Handled = true; }
        }

        private string GetStartupShortcutPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "TempMonitor.lnk");
        private void UpdateStartupMenuItem() => StartupMenuItem.Header = File.Exists(GetStartupShortcutPath()) ? "√ 开机自启" : "开机自启";
        private void Startup_Click(object sender, RoutedEventArgs e)
        {
            string path = GetStartupShortcutPath();
            if (File.Exists(path)) File.Delete(path);
            else
            {
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    Type t = Type.GetTypeFromProgID("WScript.Shell"); dynamic s = Activator.CreateInstance(t);
                    var lnk = s.CreateShortcut(path); lnk.TargetPath = exe; lnk.WorkingDirectory = Path.GetDirectoryName(exe); lnk.Save();
                }
                catch (Exception ex) { System.Windows.MessageBox.Show("失败: " + ex.Message); }
            }
            UpdateStartupMenuItem();
        }

        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { SnapToEdge(); SaveConfig(); }
        private void SnapToEdge()
        {
            double workWidth = SystemParameters.WorkArea.Width; double left = this.Left; double target = left;
            if (left < 50) { target = 0; _isDockedRight = false; }
            else if (workWidth - (left + this.ActualWidth) < 50) { target = workWidth - this.ActualWidth; _isDockedRight = true; }
            else _isDockedRight = false;
            if (target != left) this.BeginAnimation(Window.LeftProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase() });
        }

        // --- 闲置自动半透明逻辑 ---
        private void IdleTimer_Tick(object sender, EventArgs e)
        {
            // 执行淡出动画
            DoubleAnimation fadeOut = new DoubleAnimation(0.1, TimeSpan.FromSeconds(1));
            this.BeginAnimation(OpacityProperty, fadeOut);
            _idleTimer.Stop();
        }

        private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _idleTimer.Stop();
            // 立即唤醒动画
            DoubleAnimation fadeIn = new DoubleAnimation(1.0, TimeSpan.FromSeconds(0.2));
            this.BeginAnimation(OpacityProperty, fadeIn);

            double dur = AnimDurationMs; double right = this.Left + this.ActualWidth;
            if (_isDockedRight)
            {
                this.BeginAnimation(Window.WidthProperty, new DoubleAnimation(FullWidth, TimeSpan.FromMilliseconds(dur)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
                this.BeginAnimation(Window.LeftProperty, new DoubleAnimation(right - FullWidth, TimeSpan.FromMilliseconds(dur)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
            }
            else (Resources["ExpandAnim"] as Storyboard)?.Begin(this);
            AnimateMaxContainer(60, dur); AnimateOpacity(HeaderGrid, 1, dur);
        }

        private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _idleTimer.Start();

            double dur = AnimDurationMs; double right = this.Left + this.ActualWidth;
            if (_isDockedRight)
            {
                this.BeginAnimation(Window.WidthProperty, new DoubleAnimation(BaseWidth, TimeSpan.FromMilliseconds(dur)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
                this.BeginAnimation(Window.LeftProperty, new DoubleAnimation(right - BaseWidth, TimeSpan.FromMilliseconds(dur)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
            }
            else (Resources["CollapseAnim"] as Storyboard)?.Begin(this);
            AnimateMaxContainer(0, dur); AnimateOpacity(HeaderGrid, 0, dur);
        }

        private void AnimateMaxContainer(double t, double ms) => MaxContainer.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(t, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
        private void AnimateOpacity(UIElement e, double t, double ms) => e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(t, TimeSpan.FromMilliseconds(ms)));
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e) => System.Windows.Application.Current.Shutdown();
        private void ResetMax_Click(object sender, RoutedEventArgs e) { foreach (var k in _maxValues.Keys.ToList()) _maxValues[k] = 0; }
        private void Exit_Click(object sender, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown();
        protected override void OnClosed(EventArgs e) { SaveConfig(); _timer?.Stop(); _idleTimer?.Stop(); try { _computer?.Close(); } catch { } _cpuCounter?.Dispose(); _recvCounter?.Dispose(); _sentCounter?.Dispose(); base.OnClosed(e); }
    }
}
