using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AndroidConnectUI
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _monitorTimer = null!;
        private bool _isConnected;
        private bool _isRefreshing;
        private bool _isUpdatingDeviceList;
        private string? _selectedDeviceSerial;
        private ScrcpyWindow? _scrcpyWindow;

        private long _prevCpuTotal;
        private long _prevCpuIdle;

        private int _refreshCounter;
        private string _cachedResolution = "--";
        private string _cachedDensity = "--";
        private List<ProcessInfo> _cachedProcesses = new();

        private CancellationTokenSource _cts = new();
        private StringBuilder _logBuffer = new();
        private const int MAX_LOG_LINES = 500;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
        private const uint DWMWCP_DONOTROUND = 1;
        private const uint DWMWCP_ROUND = 2;
        private Point _tileDragStart;
        private Border? _draggedTile;
        private static readonly string TileLayoutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AndroidConnect", "tile-layout.txt");

        public MainWindow()
        {
            InitializeComponent();
            RestoreTileLayout();
            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref uint attributeValue, int attributeSize);

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            SourceInitialized -= MainWindow_SourceInitialized;
            ApplyDwmWindowAppearance();
        }

        private void ApplyDwmWindowAppearance()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            uint borderColor = DWMWA_COLOR_NONE;
            _ = DwmSetWindowAttribute(
                hwnd,
                DWMWA_BORDER_COLOR,
                ref borderColor,
                Marshal.SizeOf<uint>());

            uint cornerPreference = WindowState == WindowState.Maximized
                ? DWMWCP_DONOTROUND
                : DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(
                hwnd,
                DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference,
                Marshal.SizeOf<uint>());
        }

        private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _tileDragStart = e.GetPosition(this);
            _draggedTile = sender as Border;
        }

        private void Tile_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || sender is not Border tile || _draggedTile != tile)
                return;

            Point current = e.GetPosition(this);
            if (Math.Abs(current.X - _tileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _tileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            tile.Opacity = 0.55;
            try
            {
                DragDrop.DoDragDrop(tile, new DataObject(typeof(Border), tile), DragDropEffects.Move);
            }
            finally
            {
                tile.Opacity = 1;
                _draggedTile = null;
            }
        }

        private void Tile_DragEnter(object sender, DragEventArgs e)
        {
            if (sender is Border target && e.Data.GetDataPresent(typeof(Border)))
            {
                target.BorderThickness = new Thickness(2);
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
        }

        private void Tile_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border target)
                target.BorderThickness = new Thickness(1);
        }

        private void Tile_Drop(object sender, DragEventArgs e)
        {
            if (sender is not Border target || e.Data.GetData(typeof(Border)) is not Border source)
                return;

            target.BorderThickness = new Thickness(1);
            if (source == target)
                return;

            int sourceRow = Grid.GetRow(source);
            int sourceColumn = Grid.GetColumn(source);
            Grid.SetRow(source, Grid.GetRow(target));
            Grid.SetColumn(source, Grid.GetColumn(target));
            Grid.SetRow(target, sourceRow);
            Grid.SetColumn(target, sourceColumn);

            UpdateTileMargins();
            SaveTileLayout();
            e.Handled = true;
        }

        private IEnumerable<Border> ResourceTiles()
        {
            yield return tileCPU;
            yield return tileGPU;
            yield return tileFPS;
            yield return tileRAM;
        }

        private void UpdateTileMargins()
        {
            foreach (Border tile in ResourceTiles())
            {
                int row = Grid.GetRow(tile);
                int column = Grid.GetColumn(tile);
                tile.Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 0 ? 6 : 0, row == 1 ? 10 : 0);
            }
        }

        private void SaveTileLayout()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TileLayoutPath)!);
                var layout = ResourceTiles()
                    .OrderBy(Grid.GetRow)
                    .ThenBy(Grid.GetColumn)
                    .Select(tile => tile.Tag?.ToString() ?? string.Empty);
                File.WriteAllText(TileLayoutPath, string.Join(",", layout));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to save tile layout: {ex.Message}");
            }
        }

        private void RestoreTileLayout()
        {
            try
            {
                if (!File.Exists(TileLayoutPath))
                    return;

                var tiles = ResourceTiles().ToDictionary(tile => tile.Tag?.ToString() ?? string.Empty);
                string[] order = File.ReadAllText(TileLayoutPath).Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (order.Length != tiles.Count || order.Any(key => !tiles.ContainsKey(key)))
                    return;

                for (int index = 0; index < order.Length; index++)
                {
                    Grid.SetRow(tiles[order[index]], index / 2 + 1);
                    Grid.SetColumn(tiles[order[index]], index % 2);
                }
                UpdateTileMargins();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to restore tile layout: {ex.Message}");
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            try
            {
                Title = "Android Connect - 正在检查 ADB 与 scrcpy...";
                await ToolManager.EnsureToolsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"自动准备 ADB 与 scrcpy 失败：{ex.Message}\n\n程序将继续尝试使用系统 PATH 中的工具。",
                    "工具下载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Title = "Android Connect";
                await RefreshAdbDevicesAsync();
                InitializeMonitoring();
            }
        }

        private async Task RefreshAdbDevicesAsync()
        {
            if (_isUpdatingDeviceList)
                return;

            _isUpdatingDeviceList = true;
            btnRefreshDevices.IsEnabled = false;
            try
            {
                var (output, error) = await RunAdbAsync("devices -l", 5000, useSelectedDevice: false);
                if (!string.IsNullOrWhiteSpace(error))
                    Debug.WriteLine($"[Device] Unable to enumerate devices: {error}");

                var devices = ParseAdbDevices(output);
                string? previousSerial = _selectedDeviceSerial;
                AdbDeviceInfo? selected = devices.FirstOrDefault(device =>
                    device.IsOnline && device.Serial == previousSerial)
                    ?? devices.FirstOrDefault(device => device.IsOnline);

                if (devices.Count == 0)
                    devices.Add(AdbDeviceInfo.Placeholder("未检测到 ADB 设备"));

                cmbAdbDevices.ItemsSource = devices;
                cmbAdbDevices.SelectedItem = selected ?? devices[0];
                _selectedDeviceSerial = selected?.Serial;
                if (_selectedDeviceSerial != previousSerial)
                    ResetDeviceScopedState();
            }
            finally
            {
                btnRefreshDevices.IsEnabled = true;
                _isUpdatingDeviceList = false;
            }
        }

        private static List<AdbDeviceInfo> ParseAdbDevices(string output)
        {
            var devices = new List<AdbDeviceInfo>();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("* daemon", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;

                string serial = parts[0];
                string state = parts[1];
                string? model = parts.Skip(2)
                    .FirstOrDefault(part => part.StartsWith("model:", StringComparison.OrdinalIgnoreCase))?
                    .Substring("model:".Length)
                    .Replace('_', ' ');
                devices.Add(new AdbDeviceInfo(serial, state, model));
            }
            return devices;
        }

        private async void btnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAdbDevicesAsync();
            _refreshCounter = 9;
            await RefreshAllAsync(CancellationToken.None);
        }

        private async void cmbAdbDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingDeviceList)
                return;

            string? selectedSerial = (cmbAdbDevices.SelectedItem as AdbDeviceInfo) is { IsOnline: true } device
                ? device.Serial
                : null;
            if (selectedSerial == _selectedDeviceSerial)
                return;

            _selectedDeviceSerial = selectedSerial;
            try { _cts.Cancel(); } catch { }
            StopScrcpy();
            ResetDeviceScopedState();
            _refreshCounter = 9;
            await RefreshAllAsync(CancellationToken.None);
        }

        private void ResetDeviceScopedState()
        {
            _prevCpuTotal = 0;
            _prevCpuIdle = 0;
            _cachedResolution = "--";
            _cachedDensity = "--";
            _cachedProcesses.Clear();
            chartCPU.Clear();
            chartGPU.Clear();
            chartFPS.Clear();
            chartRAM.Clear();
            lvApps.ItemsSource = Array.Empty<AppInfo>();
            txtAppCount.Text = "(0)";
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
                if (_refreshCounter % 5 == 0)
                    await RefreshAdbDevicesAsync();

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
                        var (_, sleepText) = sleepTask.Result;
                        txtSleepStatus.Text = sleepText;
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
                if (string.IsNullOrWhiteSpace(_selectedDeviceSerial))
                    return false;

                var (output, error) = await RunAdbAsync("get-state");
                if (!string.IsNullOrEmpty(error))
                {
                    Debug.WriteLine($"[Device] Selected device check failed: {error}");
                    return false;
                }
                return output.Trim().Equals("device", StringComparison.OrdinalIgnoreCase);
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
                        else if (stayOn == 2)
                        {
                            return (true, "USB 连接时保持唤醒");
                        }
                        else if (stayOn == 1)
                        {
                            return (true, "交流电充电时保持唤醒");
                        }
                        else if (stayOn == 4)
                        {
                            return (true, "无线充电时保持唤醒");
                        }
                        else if (stayOn == 7)
                        {
                            return (true, "接通电源时保持唤醒");
                        }
                        else if (stayOn == 3)
                        {
                            return (true, "交流电或 USB 时保持唤醒");
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

        private async Task<(string output, string error)> RunAdbAsync(
            string arguments,
            int timeoutMs = 2000,
            bool useSelectedDevice = true)
        {
            string? selectedSerial = _selectedDeviceSerial;
            string effectiveArguments = useSelectedDevice && !string.IsNullOrWhiteSpace(selectedSerial)
                ? $"-s \"{selectedSerial.Replace("\"", "\\\"")}\" {arguments}"
                : arguments;

            return await Task.Run(() =>
            {
                try
                {
                    using var process = new Process();
                    process.StartInfo.FileName = ToolManager.AdbPath;
                    process.StartInfo.Arguments = effectiveArguments;
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
                    Debug.WriteLine($"[ADB] {effectiveArguments} => exit={process.ExitCode}, stdout='{output.Substring(0, Math.Min(100, output.Length))}', stderr='{error.Substring(0, Math.Min(100, error.Length))}'");
                    return (output, error);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ADB] 执行失败: {effectiveArguments} => {ex.Message}");
                    return ("", ex.Message);
                }
            });
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (e.ClickCount == 2 && ResizeMode == ResizeMode.CanResizeWithGrip)
            {
                ToggleMaximize();
                return;
            }

            if (WindowState == WindowState.Maximized)
            {
                Point mousePosition = e.GetPosition(this);
                double horizontalRatio = mousePosition.X / ActualWidth;
                SystemCommands.RestoreWindow(this);
                Left = e.GetPosition(null).X - RestoreBounds.Width * horizontalRatio;
                Top = Math.Max(0, e.GetPosition(null).Y - 24);
            }

            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void btnMinimize_Click(object sender, RoutedEventArgs e)
        {
            SystemCommands.MinimizeWindow(this);
        }

        private void btnMaximize_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(this);
            else
                SystemCommands.MaximizeWindow(this);
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (!IsInitialized)
                return;

            bool maximized = WindowState == WindowState.Maximized;
            btnMaximize.Content = maximized ? "\uE923" : "\uE922";
            btnMaximize.ToolTip = maximized ? "还原" : "最大化";
            WindowBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(18);
            ApplyDwmWindowAppearance();
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            _monitorTimer?.Stop();
            StopScrcpy();
            SystemCommands.CloseWindow(this);
        }

        private async void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            await OpenSleepSettingsAsync();
        }

        private async void btnSleepSettings_Click(object sender, RoutedEventArgs e)
        {
            await OpenSleepSettingsAsync();
        }

        private async Task OpenSleepSettingsAsync()
        {
            if (string.IsNullOrWhiteSpace(_selectedDeviceSerial))
            {
                MessageBox.Show("请先选择一台已连接的 ADB 设备。", "未选择设备",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var settingsWindow = new SettingsWindow(ToolManager.AdbPath, _selectedDeviceSerial);
            settingsWindow.Owner = this;
            if (settingsWindow.ShowDialog() == true)
            {
                _refreshCounter = 9;
                await RefreshAllAsync(CancellationToken.None);
            }
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

                string deviceDisplayName =
                    (cmbAdbDevices.SelectedItem as AdbDeviceInfo)?.DisplayName
                    ?? _selectedDeviceSerial!;
                var dialog = new WirelessAdbDialog(deviceDisplayName, 5555)
                {
                    Owner = this
                };
                if (dialog.ShowDialog() != true)
                    return;

                int port = dialog.Port;
                var (output, error) = await RunAdbAsync($"tcpip {port}");
                Debug.WriteLine($"无线ADB输出: {output}");
                Debug.WriteLine($"无线ADB错误: {error}");

                if (!string.IsNullOrEmpty(error) && !error.Contains("Warning"))
                {
                    MessageBox.Show($"启动无线ADB失败: {error}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    MessageBox.Show($"无线 ADB 已启动，端口：{port}\n请使用 adb connect <设备 IP>:{port} 连接。",
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

                StopScrcpy();
                Debug.WriteLine("正在打开设备视窗...");
                (int displayWidth, int displayHeight) = await GetActiveDisplaySizeAsync();
                var window = new ScrcpyWindow(
                    ToolManager.AdbPath,
                    ToolManager.ScrcpyPath,
                    _selectedDeviceSerial!,
                    displayWidth,
                    displayHeight);
                window.Owner = this;
                window.Closed += ScrcpyWindow_Closed;
                _scrcpyWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                StopScrcpy();
                Debug.WriteLine($"启动失败: {ex.Message}");
                MessageBox.Show($"启动失败: {ex.Message}\n\n请检查网络连接，或确认本地 ADB 与 scrcpy 工具完整。",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnStart.IsEnabled = true;
            }
        }

        private static (int width, int height) ParseDisplaySize(string value)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                value ?? string.Empty,
                @"(?<width>\d+)\s*x\s*(?<height>\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success &&
                int.TryParse(match.Groups["width"].Value, out int width) &&
                int.TryParse(match.Groups["height"].Value, out int height) &&
                width > 0 && height > 0)
            {
                return (width, height);
            }

            // ADB 查询失败时仍使用常见竖屏比例，避免退回到与设备无关的固定窗口。
            return (1080, 2400);
        }

        private async Task<(int width, int height)> GetActiveDisplaySizeAsync()
        {
            // wm size reports the unrotated override (for example 720x1280).
            // WindowManager bounds reflect the frame scrcpy is actually capturing.
            var (boundsOutput, _) = await RunAdbAsync(
                "shell dumpsys window displays | grep -m 1 mBounds=Rect");
            var boundsMatch = System.Text.RegularExpressions.Regex.Match(
                boundsOutput ?? string.Empty,
                @"mBounds=Rect\(\s*0\s*,\s*0\s*-\s*(?<width>\d+)\s*,\s*(?<height>\d+)\s*\)");

            if (boundsMatch.Success &&
                int.TryParse(boundsMatch.Groups["width"].Value, out int width) &&
                int.TryParse(boundsMatch.Groups["height"].Value, out int height) &&
                width > 0 && height > 0)
            {
                Debug.WriteLine($"[scrcpy] active display size: {width}x{height}");
                return (width, height);
            }

            return ParseDisplaySize(await GetPhoneResolutionAsync());
        }

        private async void PhysicalKey_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string keyCode })
                await SendKeyEvent(keyCode);
        }

        private void ScrcpyWindow_Closed(object? sender, EventArgs e)
        {
            if (sender is not ScrcpyWindow window || !ReferenceEquals(window, _scrcpyWindow))
                return;
            window.Closed -= ScrcpyWindow_Closed;
            _scrcpyWindow = null;
        }

        private void StopScrcpy()
        {
            ScrcpyWindow? window = _scrcpyWindow;
            _scrcpyWindow = null;
            if (window is null)
                return;
            window.Closed -= ScrcpyWindow_Closed;
            window.Close();
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

    public sealed class AdbDeviceInfo
    {
        public AdbDeviceInfo(string serial, string state, string? model)
        {
            Serial = serial;
            State = state;
            Model = model;

            string identity = string.IsNullOrWhiteSpace(serial)
                ? model ?? "ADB"
                : string.IsNullOrWhiteSpace(model) ? serial : $"{model} · {serial}";
            DisplayName = IsOnline ? identity : $"{identity} ({state})";
        }

        public string Serial { get; }
        public string State { get; }
        public string? Model { get; }
        public string DisplayName { get; }
        public bool IsOnline => State.Equals("device", StringComparison.OrdinalIgnoreCase);

        public override string ToString() => DisplayName;

        public static AdbDeviceInfo Placeholder(string text) => new("", "未连接", text);
    }
}
