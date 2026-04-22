using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AndroidConnectUI
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _monitorTimer = null!;
        private bool _isConnected;
        private bool _isRefreshing;

        private long _prevCpuTotal;
        private long _prevCpuIdle;

        public MainWindow()
        {
            InitializeComponent();
            InitializeMonitoring();
        }

        private void InitializeMonitoring()
        {
            _monitorTimer = new DispatcherTimer();
            _monitorTimer.Interval = TimeSpan.FromSeconds(2);
            _monitorTimer.Tick += MonitorTimer_Tick;
            _monitorTimer.Start();

            _ = RefreshAllAsync();
        }

        private async void MonitorTimer_Tick(object? sender, EventArgs e)
        {
            await RefreshAllAsync();
        }

        private async Task RefreshAllAsync()
        {
            if (_isRefreshing) return;
            _isRefreshing = true;

            try
            {
                bool connected = await IsDeviceConnectedAsync();
                _isConnected = connected;

                Dispatcher.Invoke(() =>
                {
                    txtDeviceStatus.Text = connected ? "已连接" : "未连接";
                    statusDot.Fill = connected
                        ? new SolidColorBrush(Color.FromRgb(0, 214, 143))
                        : new SolidColorBrush(Color.FromRgb(255, 61, 113));
                });

                if (connected)
                {
                    var cpuTask = GetPhoneCpuUsageAsync();
                    var gpuTask = GetPhoneGpuUsageAsync();
                    var ramTask = GetPhoneRamUsageAsync();
                    var tempTask = GetPhoneTemperatureAsync();
                    var resTask = GetPhoneResolutionAsync();
                    var densityTask = GetPhoneDensityAsync();
                    var procTask = GetPhoneProcessesAsync();

                    await Task.WhenAll(cpuTask, gpuTask, ramTask, tempTask, resTask, densityTask, procTask);

                    Dispatcher.Invoke(() =>
                    {
                        var (cpuVal, cpuText) = cpuTask.Result;
                        pbCPU.Value = cpuVal;
                        txtCPU.Text = cpuText;

                        var (gpuVal, gpuText) = gpuTask.Result;
                        pbGPU.Value = gpuVal;
                        txtGPU.Text = gpuText;

                        var (ramVal, ramText) = ramTask.Result;
                        pbRAM.Value = ramVal;
                        txtRAM.Text = ramText;

                        txtPhoneTemp.Text = tempTask.Result;
                        txtResolution.Text = resTask.Result;
                        txtDensity.Text = densityTask.Result;
                        var procs = procTask.Result;
                        lvProcesses.ItemsSource = procs;
                        txtProcessCount.Text = $"({procs.Count})";
                    });
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        pbCPU.Value = 0; txtCPU.Text = "--%";
                        pbGPU.Value = 0; txtGPU.Text = "--%";
                        pbRAM.Value = 0; txtRAM.Text = "--%";
                        txtPhoneTemp.Text = "--°C";
                        txtResolution.Text = "--";
                        txtDensity.Text = "--";
                        lvProcesses.ItemsSource = null;
                        txtProcessCount.Text = "(0)";
                    });
                }
            }
            catch { }
            finally
            {
                _isRefreshing = false;
            }
        }

        private async Task<(float value, string text)> GetPhoneCpuUsageAsync()
        {
            try
            {
                var (output, _) = await RunAdbAsync("shell cat /proc/stat");
                if (string.IsNullOrEmpty(output)) return (0, "--%");

                var lines = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return (0, "--%");

                var parts = lines[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || parts[0] != "cpu") return (0, "--%");

                long user = long.Parse(parts[1]);
                long nice = long.Parse(parts[2]);
                long system = long.Parse(parts[3]);
                long idle = long.Parse(parts[4]);

                long total = user + nice + system + idle;
                long totalDiff = total - _prevCpuTotal;
                long idleDiff = idle - _prevCpuIdle;

                _prevCpuTotal = total;
                _prevCpuIdle = idle;

                if (totalDiff == 0) return (0, "0%");

                float usage = 100f * (1f - (float)idleDiff / totalDiff);
                return (usage, $"{usage:F1}%");
            }
            catch { }
            return (0, "--%");
        }

        private async Task<(float value, string text)> GetPhoneGpuUsageAsync()
        {
            try
            {
                var (output, _) = await RunAdbAsync(
                    "shell cat /sys/class/kgsl/kgsl-3d0/gpubusy 2>/dev/null");
                if (!string.IsNullOrEmpty(output))
                {
                    var parts = output.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[0], out long busy) && long.TryParse(parts[1], out long total))
                    {
                        if (total > 0)
                        {
                            float usage = 100f * busy / total;
                            return (usage, $"{usage:F1}%");
                        }
                    }
                }

                var (output2, _) = await RunAdbAsync(
                    "shell cat /sys/class/devfreq/*/cur_freq 2>/dev/null && cat /sys/class/devfreq/*/max_freq 2>/dev/null");
                if (!string.IsNullOrEmpty(output2)) return (0, "N/A");
            }
            catch { }
            return (0, "N/A");
        }

        private async Task<(float value, string text)> GetPhoneRamUsageAsync()
        {
            try
            {
                var (output, _) = await RunAdbAsync("shell cat /proc/meminfo");
                if (string.IsNullOrEmpty(output)) return (0, "--%");

                long memTotal = 0, memAvailable = 0;
                var lines = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.StartsWith("MemTotal:"))
                    {
                        var num = ExtractNumber(line);
                        if (num > 0) memTotal = num;
                    }
                    else if (line.StartsWith("MemAvailable:"))
                    {
                        var num = ExtractNumber(line);
                        if (num > 0) memAvailable = num;
                    }
                }

                if (memTotal > 0)
                {
                    long memUsed = memTotal - memAvailable;
                    float usage = 100f * memUsed / memTotal;
                    string usedStr = FormatSize(memUsed * 1024);
                    string totalStr = FormatSize(memTotal * 1024);
                    return (usage, $"{usage:F1}%  ({usedStr}/{totalStr})");
                }
            }
            catch { }
            return (0, "--%");
        }

        private static long ExtractNumber(string line)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out long num))
                return num;
            return 0;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB";
        }

        private async Task<bool> IsDeviceConnectedAsync()
        {
            try
            {
                var (output, _) = await RunAdbAsync("devices");
                var lines = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Contains("\tdevice"))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        private async Task<string> GetPhoneTemperatureAsync()
        {
            try
            {
                var (output, _) = await RunAdbAsync(
                    "shell cat /sys/class/thermal/thermal_zone*/temp 2>/dev/null");
                if (!string.IsNullOrEmpty(output))
                {
                    string[] temps = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (temps.Length > 0 && int.TryParse(temps[0].Trim(), out int temp))
                    {
                        return $"{temp / 1000.0:F1}°C";
                    }
                }
            }
            catch { }
            return "--°C";
        }

        private async Task<string> GetPhoneResolutionAsync()
        {
            try
            {
                var (output, _) = await RunAdbAsync("shell wm size");
                if (!string.IsNullOrEmpty(output))
                {
                    var idx = output.IndexOf("Override size:");
                    if (idx >= 0)
                    {
                        var sub = output.Substring(idx + "Override size:".Length).Trim();
                        var nl = sub.IndexOf('\n');
                        if (nl >= 0) sub = sub.Substring(0, nl).Trim();
                        return sub + " (已修改)";
                    }
                    idx = output.IndexOf("Physical size:");
                    if (idx >= 0)
                    {
                        var sub = output.Substring(idx + "Physical size:".Length).Trim();
                        var nl = sub.IndexOf('\n');
                        if (nl >= 0) sub = sub.Substring(0, nl).Trim();
                        return sub;
                    }
                }
            }
            catch { }
            return "--";
        }

        private async Task<string> GetPhoneDensityAsync()
        {
            try
            {
                var (output, _) = await RunAdbAsync("shell wm density");
                if (!string.IsNullOrEmpty(output))
                {
                    var idx = output.IndexOf("Physical density:");
                    if (idx >= 0)
                    {
                        var sub = output.Substring(idx + "Physical density:".Length).Trim();
                        var nl = sub.IndexOf('\n');
                        if (nl >= 0) sub = sub.Substring(0, nl).Trim();
                        return sub + " dpi";
                    }
                }
            }
            catch { }
            return "--";
        }

        private async Task<List<ProcessInfo>> GetPhoneProcessesAsync()
        {
            var processes = new List<ProcessInfo>();
            try
            {
                var (output, _) = await RunAdbAsync("shell ps -A 2>/dev/null");
                if (string.IsNullOrWhiteSpace(output))
                {
                    (output, _) = await RunAdbAsync("shell ps");
                }
                if (string.IsNullOrWhiteSpace(output)) return processes;

                string[] lines = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2) return processes;

                string header = lines[0];
                bool isModernFormat = header.Contains("PID") && header.Contains("NAME");

                for (int i = 1; i < lines.Length && processes.Count < 20; i++)
                {
                    string[] parts = lines[i].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    if (isModernFormat && parts.Length >= 9)
                    {
                        string user = parts[0];
                        string pid = parts[1];
                        string rss = parts[5];
                        string name = parts[8];
                        if (user == "root" || user == "system" || user.StartsWith("u0_") || user.StartsWith("u10_"))
                        {
                            processes.Add(new ProcessInfo
                            {
                                Name = name,
                                PID = pid,
                                Memory = FormatSize(long.TryParse(rss, out long r) ? r * 1024 : 0)
                            });
                        }
                    }
                    else if (!isModernFormat && parts.Length >= 9)
                    {
                        processes.Add(new ProcessInfo
                        {
                            Name = parts[8],
                            PID = parts[1],
                            Memory = FormatSize(long.TryParse(parts[5], out long r) ? r * 1024 : 0)
                        });
                    }
                }
            }
            catch { }
            return processes;
        }

        private async Task<(string output, string error)> RunAdbAsync(string arguments)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var process = new Process();
                    process.StartInfo.FileName = "adb";
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(5000);
                    return (output, error);
                }
                catch
                {
                    return ("", "");
                }
            });
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void btnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            _monitorTimer.Stop();
            Application.Current.Shutdown();
        }

        private async void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            btnRefresh.IsEnabled = false;
            try
            {
                await RefreshAllAsync();
            }
            finally
            {
                btnRefresh.IsEnabled = true;
            }
        }

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string batPath = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "start.bat"));

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{batPath}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnRestore_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string batPath = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "restore.bat"));

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{batPath}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"恢复失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public class ProcessInfo
    {
        public string Name { get; set; } = "";
        public string PID { get; set; } = "";
        public string Memory { get; set; } = "";
    }
}
