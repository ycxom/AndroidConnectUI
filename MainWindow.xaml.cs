using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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

        private int _refreshCounter;
        private string _cachedResolution = "--";
        private string _cachedDensity = "--";
        private List<ProcessInfo> _cachedProcesses = new();
        private bool _cachedSleepStateInitialized;

        private CancellationTokenSource _cts = new();
        private StringBuilder _logBuffer = new();
        private const int MAX_LOG_LINES = 500;

        public MainWindow()
        {
            InitializeComponent();
            InitializeMonitoring();
        }

        private void InitializeMonitoring()
        {
            _monitorTimer = new DispatcherTimer();
            _monitorTimer.Interval = TimeSpan.FromSeconds(1);
            _monitorTimer.Tick += MonitorTimer_Tick;
            _monitorTimer.Start();

            _ = RefreshAllAsync(CancellationToken.None);
        }

        private void AddLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string logLine = $"[{timestamp}] {message}";
            
            Debug.WriteLine(logLine);
            
            Dispatcher.BeginInvoke(() =>
            {
                _logBuffer.AppendLine(logLine);
                
                while (_logBuffer.Length > 50000)
                {
                    int firstLine = _logBuffer.ToString().IndexOf('\n');
                    if (firstLine >= 0)
                        _logBuffer.Remove(0, firstLine + 1);
                    else
                        break;
                }
                
                txtLog.Text = _logBuffer.ToString();
                
                try
                {
                    txtLog.ScrollToEnd();
                }
                catch { }
            });
        }

        private void btnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logBuffer.Clear();
            txtLog.Text = "";
        }

        private async void MonitorTimer_Tick(object? sender, EventArgs e)
        {
            if (_isRefreshing) return;

            var oldCts = _cts;
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch { }
            }

            _cts = new CancellationTokenSource();
            await RefreshAllAsync(_cts.Token);
        }

        private async Task RefreshAllAsync(CancellationToken ct)
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
            _refreshCounter++;

            try
            {
                bool connected = await IsDeviceConnectedAsync();
                _isConnected = connected;

                _ = Dispatcher.BeginInvoke(() =>
                {
                    txtDeviceStatus.Text = connected ? "已连接" : "未连接";
                    statusDot.Fill = connected
                        ? new SolidColorBrush(Color.FromRgb(0, 214, 143))
                        : new SolidColorBrush(Color.FromRgb(255, 61, 113));
                });

                if (!connected)
                {
                    _cachedResolution = "--";
                    _cachedDensity = "--";
                    _cachedProcesses = new();
                    _cachedSleepStateInitialized = false;

                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        chartCPU.AddDataPoint(0);
                        txtCPU.Text = "--%";
                        chartGPU.AddDataPoint(0);
                        txtGPU.Text = "--%";
                        chartFPS.AddDataPoint(0);
                        txtFPS.Text = "-- FPS";
                        chartRAM.AddDataPoint(0);
                        txtRAM.Text = "--%";
                        txtPhoneTemp.Text = "--°C";
                        txtResolution.Text = "--";
                        txtDensity.Text = "--";
                        txtBatteryLevel.Text = "--%";
                        txtBatteryStatus.Text = "--";
                        txtChargePower.Text = "--";
                        txtBatteryTemp.Text = "--°C";
                        txtSleepStatus.Text = "设备未连接";
                        toggleKeepAwake.IsChecked = false;
                        txtAppCount.Text = "(0)";
                        lvApps.ItemsSource = new List<AppInfo>();
                    });
                    return;
                }

                ct.ThrowIfCancellationRequested();

                bool needFullRefresh = _refreshCounter % 10 == 0;

                var cpuTask = GetPhoneCpuUsageAsync();
                var gpuTask = GetPhoneGpuUsageAsync();
                var fpsTask = GetPhoneFpsAsync();
                var ramTask = GetPhoneRamUsageAsync();
                var tempTask = GetPhoneTemperatureAsync();
                var batteryTask = GetPhoneBatteryAsync();

                Task<string>? resTask = null;
                Task<string>? densityTask = null;
                Task<List<ProcessInfo>>? procTask = null;
                Task<(bool, string)>? sleepTask = null;

                if (needFullRefresh)
                {
                    resTask = GetPhoneResolutionAsync();
                    densityTask = GetPhoneDensityAsync();
                    procTask = GetPhoneProcessesAsync();
                    sleepTask = GetPhoneSleepStateAsync();
                }

                await Task.WhenAll(
                    cpuTask, gpuTask, fpsTask, ramTask, tempTask, batteryTask
                );

                if (needFullRefresh && resTask != null && densityTask != null && procTask != null && sleepTask != null)
                {
                    await Task.WhenAll(resTask, densityTask, procTask, sleepTask);
                    _cachedResolution = resTask.Result;
                    _cachedDensity = densityTask.Result;
                    _cachedProcesses = procTask.Result;
                    _cachedSleepStateInitialized = true;
                }

                ct.ThrowIfCancellationRequested();

                _ = Dispatcher.BeginInvoke(() =>
                {
                    var (cpuVal, cpuText) = cpuTask.Result;
                    chartCPU.AddDataPoint(cpuVal);
                    txtCPU.Text = cpuText;

                    var (gpuVal, gpuText) = gpuTask.Result;
                    chartGPU.AddDataPoint(gpuVal);
                    txtGPU.Text = gpuText;

                    var (fpsVal, fpsText) = fpsTask.Result;
                    chartFPS.AddDataPoint(fpsVal);
                    txtFPS.Text = fpsText;

                    var (ramVal, ramText) = ramTask.Result;
                    chartRAM.AddDataPoint(ramVal);
                    txtRAM.Text = ramText;

                    txtPhoneTemp.Text = tempTask.Result;

                    var (level, status, power, temp) = batteryTask.Result;
                    txtBatteryLevel.Text = level;
                    txtBatteryStatus.Text = status;
                    txtChargePower.Text = power;
                    txtBatteryTemp.Text = temp;

                    txtResolution.Text = _cachedResolution;
                    txtDensity.Text = _cachedDensity;

                    if (needFullRefresh && procTask != null)
                    {
                        lvApps.ItemsSource = procTask.Result;
                        txtAppCount.Text = $"({procTask.Result.Count})";
                    }

                    if (needFullRefresh && sleepTask != null)
                    {
                        var (sleepEnabled, sleepText) = sleepTask.Result;
                        txtSleepStatus.Text = sleepText;
                        if (!_cachedSleepStateInitialized)
                        {
                            toggleKeepAwake.IsChecked = sleepEnabled;
                            _cachedSleepStateInitialized = true;
                        }
                    }
                });
            }
            catch (OperationCanceledException) 
            {
                Debug.WriteLine("[Refresh] 被取消");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Refresh] 异常: {ex.Message}");
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private async Task<(float value, string text)> GetPhoneCpuUsageAsync()
        {
            try
            {
                var (output, error) = await RunAdbAsync("shell cat /proc/stat");
                if (!string.IsNullOrEmpty(error) && !error.StartsWith("超时")) return (0, "--%");
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
            catch (Exception ex) 
            {
                Debug.WriteLine($"[CPU] 获取失败: {ex.Message}");
            }
            return (0, "--%");
        }

        private async Task<(float value, string text)> GetPhoneGpuUsageAsync()
        {
            try
            {
                var (output, error) = await RunAdbAsync(
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

                var (output2, error2) = await RunAdbAsync(
                    "shell cat /sys/class/devfreq/*/cur_freq 2>/dev/null && cat /sys/class/devfreq/*/max_freq 2>/dev/null");
                if (!string.IsNullOrEmpty(output2)) return (0, "N/A");
            }
            catch (Exception ex) 
            {
                Debug.WriteLine($"[GPU] 获取失败: {ex.Message}");
            }
            return (0, "N/A");
        }

        private async Task<(float value, string text)> GetPhoneFpsAsync()
        {
            try
            {
                await RunAdbAsync("shell dumpsys SurfaceFlinger --latency-clear");

                var (latencyOutput, _) = await RunAdbAsync("shell dumpsys SurfaceFlinger --latency");
                if (!string.IsNullOrEmpty(latencyOutput))
                {
                    var lines = latencyOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    int frameCount = 0;
                    
                    foreach (var line in lines)
                    {
                        var parts = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            if (long.TryParse(parts[0], out long start) && 
                                long.TryParse(parts[1], out long vsync) &&
                                start > 0 && vsync > 0)
                            {
                                frameCount++;
                            }
                        }
                    }

                    if (frameCount > 1)
                    {
                        float fps = Math.Min(frameCount * 10, 120);
                        return (fps, $"{fps:F0} FPS");
                    }
                }

                var (gfxOutput, _) = await RunAdbAsync("shell dumpsys gfxinfo");
                if (!string.IsNullOrEmpty(gfxOutput) && gfxOutput.Contains("Total frames rendered"))
                {
                    var lines = gfxOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.Contains("Total frames rendered"))
                        {
                            var parts = line.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int totalFrames))
                            {
                                float estimatedFps = Math.Min(totalFrames / 2, 120);
                                return (estimatedFps, $"{estimatedFps:F0} FPS");
                            }
                        }
                    }
                }
            }
            catch { }
            return (0, "N/A");
        }

        private async Task<(float value, string text)> GetPhoneRamUsageAsync()
        {
            try
            {
                var (output, error) = await RunAdbAsync("shell cat /proc/meminfo");
                if (!string.IsNullOrEmpty(error) && !error.StartsWith("超时")) return (0, "--%");
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
            catch (Exception ex) 
            {
                Debug.WriteLine($"[RAM] 获取失败: {ex.Message}");
            }
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
                var (output, error) = await RunAdbAsync("devices");
                if (!string.IsNullOrEmpty(error) && !error.StartsWith("超时"))
                {
                    Debug.WriteLine($"[Device] 检查连接错误: {error}");
                    return false;
                }
                if (string.IsNullOrEmpty(output)) return false;
                
                var lines = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Contains("\tdevice"))
                        return true;
                }
                return false;
            }
            catch (Exception ex) 
            { 
                Debug.WriteLine($"[Device] 检查连接异常: {ex.Message}");
                return false; 
            }
        }

        private async Task<string> GetPhoneTemperatureAsync()
        {
            try
            {
                var (output, error) = await RunAdbAsync(
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
            catch (Exception ex) 
            {
                Debug.WriteLine($"[温度] 获取失败: {ex.Message}");
            }
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
                Debug.WriteLine($"DPI 原始输出: {output}");
                
                if (!string.IsNullOrEmpty(output))
                {
                    var overrideIdx = output.IndexOf("Override density:");
                    if (overrideIdx >= 0)
                    {
                        var sub = output.Substring(overrideIdx + "Override density:".Length).Trim();
                        var nl = sub.IndexOf('\n');
                        if (nl >= 0) sub = sub.Substring(0, nl).Trim();
                        Debug.WriteLine($"DPI (Override): {sub}");
                        return sub + " dpi";
                    }
                    
                    var physicalIdx = output.IndexOf("Physical density:");
                    if (physicalIdx >= 0)
                    {
                        var sub = output.Substring(physicalIdx + "Physical density:".Length).Trim();
                        var nl = sub.IndexOf('\n');
                        if (nl >= 0) sub = sub.Substring(0, nl).Trim();
                        Debug.WriteLine($"DPI (Physical): {sub}");
                        return sub + " dpi";
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取 DPI 失败: {ex.Message}");
            }
            return "--";
        }

        private async Task<(string level, string status, string power, string temp)> GetPhoneBatteryAsync()
        {
            try
            {
                var (output, error) = await RunAdbAsync("shell dumpsys battery");
                Debug.WriteLine($"[电池] dumpsys battery 输出: {output}");
                if (!string.IsNullOrEmpty(error))
                {
                    Debug.WriteLine($"[电池] dumpsys battery 错误: {error}");
                }
                
                if (!string.IsNullOrEmpty(error) && !error.StartsWith("超时")) return ("--%", "--", "--", "--°C");
                if (string.IsNullOrEmpty(output)) return ("--%", "--", "--", "--°C");

                string level = "--%";
                string status = "--";
                string power = "--";
                string temp = "--°C";

                var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                Debug.WriteLine($"[电池] 共 {lines.Length} 行输出");
                
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    Debug.WriteLine($"[电池] 解析行: {trimmed}");
                    
                    if (trimmed.StartsWith("level:"))
                    {
                        var val = trimmed.Substring("level:".Length).Trim();
                        Debug.WriteLine($"[电池] 解析到电量值: {val}");
                        level = $"{val}%";
                    }
                    else if (trimmed.StartsWith("status:"))
                    {
                        var val = trimmed.Substring("status:".Length).Trim();
                        Debug.WriteLine($"[电池] 解析到充电状态: {val}");
                        status = val switch
                        {
                            "1" => "未知",
                            "2" => "充电中",
                            "3" => "放电中",
                            "4" => "未充电",
                            "5" => "已充满",
                            _ => val
                        };
                    }
                    else if (trimmed.StartsWith("temperature:"))
                    {
                        var val = trimmed.Substring("temperature:".Length).Trim();
                        if (int.TryParse(val, out int t))
                        {
                            Debug.WriteLine($"[电池] 解析到温度值: {t}");
                            temp = $"{t / 10.0:F1}°C";
                        }
                    }
                }

                Debug.WriteLine($"[电池] 解析结果: level={level}, status={status}, temp={temp}");

                var currentTask = RunAdbAsync("shell cat /sys/class/power_supply/battery/current_now 2>/dev/null");
                var voltageTask = RunAdbAsync("shell cat /sys/class/power_supply/battery/voltage_now 2>/dev/null");
                await Task.WhenAll(currentTask, voltageTask);

                var (currentOutput, currentError) = currentTask.Result;
                var (voltageOutput, voltageError) = voltageTask.Result;
                
                Debug.WriteLine($"[电池] current_now 输出: '{currentOutput}', 错误: '{currentError}'");
                Debug.WriteLine($"[电池] voltage_now 输出: '{voltageOutput}', 错误: '{voltageError}'");

                if (long.TryParse(currentOutput?.Trim(), out long current) &&
                    long.TryParse(voltageOutput?.Trim(), out long voltage))
                {
                    double currentA = Math.Abs(current) / 1000000.0;
                    double voltageV = voltage / 1000000.0;
                    double watt = currentA * voltageV;
                    power = $"{watt:F2} W";
                    Debug.WriteLine($"[电池] 计算功率: {watt:F2} W");
                }

                return (level, status, power, temp);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[电池] 获取失败: {ex.Message}");
            }
            return ("--%", "--", "--", "--°C");
        }

        private async Task<(bool enabled, string text)> GetPhoneSleepStateAsync()
        {
            try
            {
                var (output, _) = await RunAdbAsync("shell settings get global stay_on_while_plugged_in");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    var value = output.Trim();
                    if (int.TryParse(value, out int stayOn))
                    {
                        if (stayOn == 0)
                        {
                            return (false, "默认休眠");
                        }
                        else if (stayOn == 3)
                        {
                            return (true, "充电/USB/无线均不休眠");
                        }
                        else if (stayOn == 2)
                        {
                            return (true, "无线充电不休眠");
                        }
                        else if (stayOn == 1)
                        {
                            return (true, "USB连接不休眠");
                        }
                        return (stayOn != 0, $"自定义: {value}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取不休眠状态失败: {ex.Message}");
            }
            return (false, "获取失败");
        }

        private async void toggleKeepAwake_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                var connected = await IsDeviceConnectedAsync();
                if (!connected)
                {
                    MessageBox.Show("未检测到 Android 设备，请确保设备已通过 USB 连接并启用了 USB 调试。",
                        "设备未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    toggleKeepAwake.IsChecked = false;
                    return;
                }

                var (_, error) = await RunAdbAsync("shell settings put global stay_on_while_plugged_in 3");
                if (!string.IsNullOrEmpty(error) && !error.Contains("Warning"))
                {
                    Debug.WriteLine($"设置不休眠失败: {error}");
                    MessageBox.Show($"设置不休眠失败: {error}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    toggleKeepAwake.IsChecked = false;
                    return;
                }

                txtSleepStatus.Text = "充电/USB/无线均不休眠";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置不休眠异常: {ex.Message}");
                MessageBox.Show($"设置不休眠失败: {ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                toggleKeepAwake.IsChecked = false;
            }
        }

        private async void toggleKeepAwake_Unchecked(object sender, RoutedEventArgs e)
        {
            try
            {
                var connected = await IsDeviceConnectedAsync();
                if (!connected)
                {
                    MessageBox.Show("未检测到 Android 设备，请确保设备已通过 USB 连接并启用了 USB 调试。",
                        "设备未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    toggleKeepAwake.IsChecked = true;
                    return;
                }

                var (_, error) = await RunAdbAsync("shell settings put global stay_on_while_plugged_in 0");
                if (!string.IsNullOrEmpty(error) && !error.Contains("Warning"))
                {
                    Debug.WriteLine($"关闭不休眠失败: {error}");
                    MessageBox.Show($"关闭不休眠失败: {error}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    toggleKeepAwake.IsChecked = true;
                    return;
                }

                txtSleepStatus.Text = "默认休眠";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"关闭不休眠异常: {ex.Message}");
                MessageBox.Show($"关闭不休眠失败: {ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                toggleKeepAwake.IsChecked = true;
            }
        }

        private async Task<List<ProcessInfo>> GetPhoneProcessesAsync()
        {
            var processes = new List<ProcessInfo>();
            try
            {
                var (output, _) = await RunAdbAsync("shell ps -e 2>/dev/null");
                if (string.IsNullOrWhiteSpace(output))
                {
                    (output, _) = await RunAdbAsync("shell ps");
                }
                if (string.IsNullOrWhiteSpace(output)) return processes;

                string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2) return processes;

                string header = lines[0].Trim();
                bool hasPidColumn = header.Contains("PID");
                bool hasNameColumn = header.Contains("NAME") || header.Contains("PROCESS");

                int pidIndex = -1;
                int nameIndex = -1;
                int rssIndex = -1;

                if (hasPidColumn && hasNameColumn)
                {
                    string[] headerParts = header.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < headerParts.Length; i++)
                    {
                        if (headerParts[i] == "PID") pidIndex = i;
                        else if (headerParts[i] == "NAME" || headerParts[i] == "PROCESS") nameIndex = i;
                        else if (headerParts[i] == "RSS") rssIndex = i;
                    }
                }

                for (int i = 1; i < lines.Length && processes.Count < 20; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                    if (pidIndex >= 0 && nameIndex >= 0 && parts.Length > Math.Max(pidIndex, nameIndex))
                    {
                        string pid = parts[pidIndex];
                        string name = parts[nameIndex];
                        string rss = rssIndex >= 0 && parts.Length > rssIndex ? parts[rssIndex] : "0";

                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(pid))
                        {
                            processes.Add(new ProcessInfo
                            {
                                Name = name,
                                PID = pid,
                                Memory = FormatSize(long.TryParse(rss, out long r) ? r * 1024 : 0)
                            });
                        }
                    }
                    else if (parts.Length >= 9)
                    {
                        string pid = parts[1];
                        string rss = parts[5];
                        string name = parts[parts.Length - 1];

                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(pid))
                        {
                            processes.Add(new ProcessInfo
                            {
                                Name = name,
                                PID = pid,
                                Memory = FormatSize(long.TryParse(rss, out long r) ? r * 1024 : 0)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"进程获取失败: {ex.Message}");
            }
            return processes;
        }

        private async Task<(string output, string error)> RunAdbAsync(string arguments, int timeoutMs = 2000)
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
                    process.WaitForExit(timeoutMs);
                    
                    if (!process.HasExited)
                    {
                        try { process.Kill(); } catch { }
                        return ("", $"超时({timeoutMs}ms)");
                    }
                    
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    Debug.WriteLine($"[ADB] {arguments} => exit={process.ExitCode}, stdout='{output.Substring(0, Math.Min(100, output.Length))}', stderr='{error.Substring(0, Math.Min(100, error.Length))}'");
                    return (output, error);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ADB] 执行失败: {arguments} => {ex.Message}");
                    return ("", ex.Message);
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

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog();
        }

        private async void btnWirelessAdb_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("无线ADB按钮被点击");
            btnWirelessAdb.IsEnabled = false;

            try
            {
                var connected = await IsDeviceConnectedAsync();
                if (!connected)
                {
                    MessageBox.Show("未检测到 Android 设备，请确保设备已通过 USB 连接并启用了 USB 调试。",
                        "设备未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    btnWirelessAdb.IsEnabled = true;
                    return;
                }

                var (output, error) = await RunAdbAsync("tcpip 5555");
                Debug.WriteLine($"无线ADB输出: {output}");
                Debug.WriteLine($"无线ADB错误: {error}");

                if (!string.IsNullOrEmpty(error) && !error.Contains("Warning"))
                {
                    MessageBox.Show($"启动无线ADB失败: {error}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    MessageBox.Show("无线ADB已启动，端口: 5555\n请使用 adb connect <设备IP>:5555 连接",
                        "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"无线ADB失败: {ex.Message}");
                MessageBox.Show($"无线ADB失败: {ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnWirelessAdb.IsEnabled = true;
            }
        }

        private async void btnKeyPower_Click(object sender, RoutedEventArgs e)
        {
            await SendKeyEvent("26");
        }

        private async void btnKeyVolUp_Click(object sender, RoutedEventArgs e)
        {
            await SendKeyEvent("24");
        }

        private async void btnKeyVolDown_Click(object sender, RoutedEventArgs e)
        {
            await SendKeyEvent("25");
        }

        private async Task SendKeyEvent(string keyCode)
        {
            try
            {
                var connected = await IsDeviceConnectedAsync();
                if (!connected)
                {
                    MessageBox.Show("未检测到 Android 设备，请确保设备已通过 USB 连接并启用了 USB 调试。",
                        "设备未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await RunAdbAsync($"shell input keyevent {keyCode}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"发送按键事件失败: {ex.Message}");
                MessageBox.Show($"发送按键事件失败: {ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("启动连接按钮被点击");
            btnStart.IsEnabled = false;

            try
            {
                var connected = await IsDeviceConnectedAsync();
                if (!connected)
                {
                    MessageBox.Show("未检测到 Android 设备，请确保设备已通过 USB 连接并启用了 USB 调试。",
                        "设备未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    btnStart.IsEnabled = true;
                    return;
                }

                Debug.WriteLine("正在设置 USB 链接不休眠...");
                var (_, error1) = await RunAdbAsync("shell settings put global stay_on_while_plugged_in 3");
                if (!string.IsNullOrEmpty(error1))
                {
                    Debug.WriteLine($"设置不休眠警告: {error1}");
                }

                Debug.WriteLine("正在启动 scrcpy 并关闭屏幕...");
                var scrcpyStartInfo = new ProcessStartInfo
                {
                    FileName = "scrcpy",
                    Arguments = "--turn-screen-off --stay-awake",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                Process.Start(scrcpyStartInfo);

                MessageBox.Show("已成功启动 scrcpy 并设置设备不休眠。", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启动失败: {ex.Message}");
                MessageBox.Show($"启动失败: {ex.Message}\n\n请确保已安装 scrcpy 并将其添加到系统 PATH 中。",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnStart.IsEnabled = true;
            }
        }

        private async void btnRestore_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("恢复分辨率按钮被点击");
            btnRestore.IsEnabled = false;

            try
            {
                var connected = await IsDeviceConnectedAsync();
                if (!connected)
                {
                    MessageBox.Show("未检测到 Android 设备，请确保设备已通过 USB 连接并启用了 USB 调试。",
                        "设备未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    btnRestore.IsEnabled = true;
                    return;
                }

                Debug.WriteLine("正在恢复分辨率...");
                var (_, error1) = await RunAdbAsync("shell wm size reset");
                if (!string.IsNullOrEmpty(error1))
                {
                    Debug.WriteLine($"恢复分辨率警告: {error1}");
                }

                Debug.WriteLine("正在恢复密度...");
                var (_, error2) = await RunAdbAsync("shell wm density reset");
                if (!string.IsNullOrEmpty(error2))
                {
                    Debug.WriteLine($"恢复密度警告: {error2}");
                }

                Debug.WriteLine("正在恢复休眠设置...");
                var (_, error3) = await RunAdbAsync("shell settings put global stay_on_while_plugged_in 0");
                if (!string.IsNullOrEmpty(error3))
                {
                    Debug.WriteLine($"恢复休眠设置警告: {error3}");
                }

                _refreshCounter = 9;
                await RefreshAllAsync(CancellationToken.None);

                MessageBox.Show("已成功恢复设备默认设置。", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复失败: {ex.Message}");
                MessageBox.Show($"恢复失败: {ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnRestore.IsEnabled = true;
            }
        }

        private async void btnSetResolution_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("设置分辨率按钮被点击");
            btnSetResolution.IsEnabled = false;

            try
            {
                var connected = await IsDeviceConnectedAsync();
                if (!connected)
                {
                    MessageBox.Show("未检测到 Android 设备，请确保设备已通过 USB 连接并启用了 USB 调试。",
                        "设备未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    btnSetResolution.IsEnabled = true;
                    return;
                }

                var inputDialog = new ResolutionInputDialog();
                if (inputDialog.ShowDialog() == true)
                {
                    string resolution = inputDialog.Resolution;
                    string density = inputDialog.Density;
                    bool hasResolution = !string.IsNullOrWhiteSpace(resolution);
                    bool hasDensity = !string.IsNullOrWhiteSpace(density);

                    if (!hasResolution && !hasDensity)
                    {
                        btnSetResolution.IsEnabled = true;
                        return;
                    }

                    if (hasResolution)
                    {
                        Debug.WriteLine($"正在设置分辨率: {resolution}");
                        var (output, error) = await RunAdbAsync($"shell wm size {resolution}");
                        if (!string.IsNullOrEmpty(error))
                        {
                            MessageBox.Show($"设置分辨率失败: {error}", "错误",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                            btnSetResolution.IsEnabled = true;
                            return;
                        }
                    }

                    if (hasDensity)
                    {
                        Debug.WriteLine($"正在设置 DPI: {density}");
                        var (output, error) = await RunAdbAsync($"shell wm density {density}");
                        Debug.WriteLine($"DPI 设置输出: {output}");
                        Debug.WriteLine($"DPI 设置错误: {error}");
                        
                        bool isRealError = !string.IsNullOrEmpty(error) && 
                                          !error.Contains("Warning") && 
                                          !error.Contains("WARNING");
                        
                        if (isRealError)
                        {
                            MessageBox.Show($"设置 DPI 失败: {error}", "错误",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                            btnSetResolution.IsEnabled = true;
                            return;
                        }
                    }

                    _refreshCounter = 9;
                    await RefreshAllAsync(CancellationToken.None);

                    string message = "";
                    if (hasResolution) message += $"分辨率已设置为: {resolution}";
                    if (hasResolution && hasDensity) message += "\n";
                    if (hasDensity) message += $"DPI 已设置为: {density}";

                    MessageBox.Show(message, "成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置失败: {ex.Message}");
                MessageBox.Show($"设置失败: {ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnSetResolution.IsEnabled = true;
            }
        }

        private async void btnLoadApps_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("加载应用列表按钮被点击");
            btnLoadApps.IsEnabled = false;

            try
            {
                var connected = await IsDeviceConnectedAsync();
                if (!connected)
                {
                    MessageBox.Show("未检测到 Android 设备，请确保设备已通过 USB 连接并启用了 USB 调试。",
                        "设备未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    btnLoadApps.IsEnabled = true;
                    return;
                }

                var apps = await GetInstalledAppsAsync();
                lvApps.ItemsSource = apps;
                txtAppCount.Text = $"({apps.Count})";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载应用列表失败: {ex.Message}");
                MessageBox.Show($"加载应用列表失败: {ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnLoadApps.IsEnabled = true;
            }
        }

        private async void lvApps_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lvApps.SelectedItem is AppInfo app)
            {
                Debug.WriteLine($"双击启动应用: {app.Name} ({app.PackageName})");
                try
                {
                    var connected = await IsDeviceConnectedAsync();
                    if (!connected)
                    {
                        MessageBox.Show("未检测到 Android 设备，请确保设备已通过 USB 连接并启用了 USB 调试。",
                            "设备未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var (output, error) = await RunAdbAsync($"shell monkey -p {app.PackageName} -c android.intent.category.LAUNCHER 1");
                    Debug.WriteLine($"monkey 输出: {output}");
                    Debug.WriteLine($"monkey 错误: {error}");
                    
                    bool isRealError = output.Contains("** ERROR") || 
                                       output.Contains("IllegalArgument") ||
                                       output.Contains("Exception") ||
                                       output.Contains("No activities found");
                    
                    if (isRealError)
                    {
                        string errorMsg = output.Contains("No activities found") 
                            ? "该应用没有可启动的 Activity" 
                            : $"启动应用失败: {output}";
                        MessageBox.Show(errorMsg, "错误",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"启动应用失败: {ex.Message}");
                    MessageBox.Show($"启动应用失败: {ex.Message}",
                        "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task<List<AppInfo>> GetInstalledAppsAsync()
        {
            var apps = new List<AppInfo>();
            try
            {
                var (output, error) = await RunAdbAsync("shell pm list packages -3");
                Debug.WriteLine($"应用列表输出: {output}");
                Debug.WriteLine($"应用列表错误: {error}");
                
                if (string.IsNullOrWhiteSpace(output)) 
                {
                    Debug.WriteLine("应用列表输出为空");
                    return apps;
                }

                string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                Debug.WriteLine($"应用列表行数: {lines.Length}");
                
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    Debug.WriteLine($"处理行: {trimmed}");
                    
                    if (trimmed.StartsWith("package:"))
                    {
                        string packageName = trimmed.Substring("package:".Length).Trim();
                        Debug.WriteLine($"找到包名: {packageName}");
                        
                        string appName = await GetAppLabelAsync(packageName);
                        
                        apps.Add(new AppInfo
                        {
                            Name = appName,
                            PackageName = packageName
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取应用列表失败: {ex.Message}");
            }
            return apps;
        }

        private async Task<string> GetAppLabelAsync(string packageName)
        {
            try
            {
                var (output, _) = await RunAdbAsync($"shell dumpsys package {packageName}");
                if (string.IsNullOrEmpty(output)) return packageName;

                var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                bool foundApplicationInfo = false;
                
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    
                    if (trimmed.StartsWith("ApplicationInfo"))
                    {
                        foundApplicationInfo = true;
                    }
                    
                    if (foundApplicationInfo && trimmed.StartsWith("label="))
                    {
                        var label = trimmed.Substring("label=".Length).Trim();
                        if (!string.IsNullOrEmpty(label))
                        {
                            Debug.WriteLine($"应用 {packageName} 名称: {label}");
                            return label;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取 {packageName} 名称失败: {ex.Message}");
            }
            return packageName;
        }
    }

    public class ProcessInfo
    {
        public string Name { get; set; } = "";
        public string PID { get; set; } = "";
        public string Memory { get; set; } = "";
    }

    public class AppInfo
    {
        public string Name { get; set; } = "";
        public string PackageName { get; set; } = "";
    }
}
