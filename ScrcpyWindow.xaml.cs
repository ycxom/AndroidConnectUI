using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AndroidConnectUI;

public partial class ScrcpyWindow : Window
{
    private const int WmSizing = 0x0214;
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;
    private const double StatusBarHeight = 48;
    private const double ControlPanelWidth = 318;

    private readonly string _adbPath;
    private readonly string _scrcpyPath;
    private readonly string _serial;
    private double _deviceAspectRatio;
    private bool _controlPanelOpen;
    private bool _controlsLoaded;
    private bool _videoSizeReceived;
    private bool _isWindowAnimating;
    private double _windowWidthBeforePanel;
    private IntPtr _windowHandle;
    private HwndSource? _windowSource;
    private int _nonClientWidthPixels;
    private int _nonClientHeightPixels;
    private double _dpiScaleX = 1;
    private double _dpiScaleY = 1;

    public ScrcpyWindow(
        string adbPath,
        string scrcpyPath,
        string serial,
        int displayWidth,
        int displayHeight)
    {
        _adbPath = adbPath;
        _scrcpyPath = scrcpyPath;
        _serial = serial;
        displayWidth = Math.Max(1, displayWidth);
        displayHeight = Math.Max(1, displayHeight);
        _deviceAspectRatio = (double)displayWidth / displayHeight;
        InitializeComponent();
        ConfigureInitialViewport(displayWidth, displayHeight);
        txtDeviceSerial.Text = serial;
        Title = $"Android Connect · {serial}";
        scrcpyHost.SessionEnded += ScrcpyHost_SessionEnded;
        scrcpyHost.VideoSizeChanged += ScrcpyHost_VideoSizeChanged;
        SourceInitialized += ScrcpyWindow_SourceInitialized;
        Loaded += ScrcpyWindow_Loaded;
        Closing += ScrcpyWindow_Closing;
    }

    private void ConfigureInitialViewport(int displayWidth, int displayHeight)
    {
        const double initialLongSide = 420;
        double scale = Math.Min(1, initialLongSide / Math.Max(displayWidth, displayHeight));

        screenViewport.Width = Math.Max(1, Math.Round(displayWidth * scale));
        screenViewport.Height = Math.Max(1, Math.Round(displayHeight * scale));
        MinWidth = 240;
        MinHeight = 200;
    }

    private void ScrcpyWindow_SourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= ScrcpyWindow_SourceInitialized;
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowMessageHook);
        UpdateWindowMetrics();
    }

    private void FinishInitialSizing()
    {
        // The status bar can be wider than a portrait display. Correct the
        // viewport height from its final measured width before the first frame.
        UpdateLayout();
        double viewportWidth = Math.Max(1, screenViewport.ActualWidth);
        screenViewport.Height = viewportWidth / _deviceAspectRatio;
        UpdateLayout();

        double initialWidth = ActualWidth;
        double initialHeight = ActualHeight;
        SizeToContent = SizeToContent.Manual;
        Width = initialWidth;
        Height = initialHeight;
        screenViewport.ClearValue(WidthProperty);
        screenViewport.ClearValue(HeightProperty);
        UpdateWindowMetrics();
    }

    private async void ScrcpyWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ScrcpyWindow_Loaded;
        FinishInitialSizing();
        try
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            await scrcpyHost.StartAsync(_scrcpyPath, _serial);
            screenPlaceholder.Visibility = Visibility.Collapsed;
            sessionControls.Visibility = Visibility.Visible;
            sessionDot.Fill = new SolidColorBrush(Color.FromRgb(32, 201, 151));
            txtSessionStatus.Text = "scrcpy 运行中";
        }
        catch (Exception ex)
        {
            scrcpyHost.Visibility = Visibility.Collapsed;
            screenPlaceholder.Visibility = Visibility.Visible;
            txtScreenMessage.Text = ex.Message;
            sessionDot.Fill = new SolidColorBrush(Color.FromRgb(251, 113, 133));
            txtSessionStatus.Text = "启动失败";
        }

        await LoadDeviceControlsAsync();
    }

    private void btnToggleControlPanel_Click(object sender, RoutedEventArgs e)
    {
        _controlPanelOpen = !_controlPanelOpen;

        double targetPanelWidth;
        double targetWindowWidth;
        if (_controlPanelOpen)
        {
            _windowWidthBeforePanel = ActualWidth;
            targetPanelWidth = ControlPanelWidth;
            targetWindowWidth = Math.Min(SystemParameters.WorkArea.Width, _windowWidthBeforePanel + ControlPanelWidth);
            if (Left + targetWindowWidth > SystemParameters.WorkArea.Right)
                Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - targetWindowWidth);
            btnTogglePanel.Content = "\uE76B";
            btnTogglePanel.ToolTip = "收起设备控制面板";
        }
        else
        {
            targetPanelWidth = 0;
            targetWindowWidth = Math.Max(MinWidth, _windowWidthBeforePanel);
            btnTogglePanel.Content = "\uE700";
            btnTogglePanel.ToolTip = "展开设备控制面板";
        }

        if (AppPreferences.WindowAnimationsEnabled)
        {
            AnimateWidth(controlPanel, targetPanelWidth);
            AnimateWindowWidth(targetWindowWidth);
            controlPanel.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = _controlPanelOpen ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            controlPanel.BeginAnimation(WidthProperty, null);
            controlPanel.BeginAnimation(OpacityProperty, null);
            BeginAnimation(WidthProperty, null);
            controlPanel.Width = targetPanelWidth;
            controlPanel.Opacity = _controlPanelOpen ? 1 : 0;
            Width = targetWindowWidth;
        }
    }

    private static void AnimateWidth(FrameworkElement element, double target)
    {
        var animation = new DoubleAnimation
        {
            From = element.ActualWidth,
            To = target,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => element.Width = target;
        element.BeginAnimation(WidthProperty, animation);
    }

    private void AnimateWindowWidth(double target)
    {
        var animation = new DoubleAnimation
        {
            From = ActualWidth,
            To = target,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => Width = target;
        BeginAnimation(WidthProperty, animation);
    }

    private async Task LoadDeviceControlsAsync()
    {
        _controlsLoaded = false;
        var brightnessTask = RunAdbAsync("shell settings get system screen_brightness");
        var rotationTask = RunAdbAsync("shell settings get system accelerometer_rotation");
        var volumeTask = RunAdbAsync("shell media volume --stream 3 --get");
        await Task.WhenAll(brightnessTask, rotationTask, volumeTask);

        if (int.TryParse(brightnessTask.Result.output.Trim(), out int brightness))
            sliderBrightness.Value = Math.Clamp(brightness, 1, 255);
        if (int.TryParse(rotationTask.Result.output.Trim(), out int autoRotate))
            toggleAutoRotate.IsChecked = autoRotate != 0;

        Match volumeMatch = Regex.Match(volumeTask.Result.output, @"volume is\s+(\d+).*?\[0\.\.(\d+)\]",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (volumeMatch.Success &&
            int.TryParse(volumeMatch.Groups[1].Value, out int volume) &&
            int.TryParse(volumeMatch.Groups[2].Value, out int maximum))
        {
            sliderVolume.Maximum = maximum;
            sliderVolume.Value = Math.Clamp(volume, 0, maximum);
        }

        _controlsLoaded = true;
        UpdateSliderLabels();
    }

    private async void ControlTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action })
            return;

        string command = action switch
        {
            "power" => "shell input keyevent 26",
            "notifications" => "shell cmd statusbar expand-notifications",
            "screenshot" => "shell input keyevent 120",
            "portrait" => "shell settings put system accelerometer_rotation 0; settings put system user_rotation 0",
            "landscape" => "shell settings put system accelerometer_rotation 0; settings put system user_rotation 1",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(command))
            return;

        if (action is "portrait" or "landscape")
        {
            _controlsLoaded = false;
            toggleAutoRotate.IsChecked = false;
            _controlsLoaded = true;
        }
        var (_, error) = await RunAdbAsync(command);
        if (!string.IsNullOrWhiteSpace(error))
            txtSessionStatus.Text = "控制命令失败";
    }

    private async void toggleAutoRotate_Changed(object sender, RoutedEventArgs e)
    {
        if (!_controlsLoaded)
            return;
        int enabled = toggleAutoRotate.IsChecked == true ? 1 : 0;
        await RunAdbAsync($"shell settings put system accelerometer_rotation {enabled}");
    }

    private void ControlSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSliderLabels();
    }

    private async void ControlSlider_Apply(object sender, RoutedEventArgs e)
    {
        if (!_controlsLoaded || sender is not Slider { Tag: string control })
            return;
        int value = (int)Math.Round(((Slider)sender).Value);
        string command = control == "brightness"
            ? $"shell settings put system screen_brightness {value}"
            : $"shell media volume --stream 3 --set {value}";
        await RunAdbAsync(command);
    }

    private void UpdateSliderLabels()
    {
        if (txtBrightnessValue is null || txtVolumeValue is null)
            return;
        txtBrightnessValue.Text = ((int)Math.Round(sliderBrightness.Value)).ToString();
        txtVolumeValue.Text = ((int)Math.Round(sliderVolume.Value)).ToString();
    }

    private async void PhysicalKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string keyCode })
            return;

        var (_, error) = await RunAdbAsync($"shell input keyevent {keyCode}");
        if (!string.IsNullOrWhiteSpace(error))
            txtSessionStatus.Text = "按键发送失败";
    }

    private async Task<(string output, string error)> RunAdbAsync(string arguments)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var process = new Process();
                process.StartInfo.FileName = _adbPath;
                process.StartInfo.Arguments = $"-s \"{_serial}\" {arguments}";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.Start();
                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(); } catch { }
                    return ("", "ADB 操作超时");
                }
                return (process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
            }
            catch (Exception ex)
            {
                return ("", ex.Message);
            }
        });
    }

    private void ScrcpyHost_SessionEnded(object? sender, EventArgs e)
    {
        scrcpyHost.Visibility = Visibility.Collapsed;
        screenPlaceholder.Visibility = Visibility.Visible;
        txtScreenMessage.Text = "scrcpy 会话已结束";
        sessionDot.Fill = new SolidColorBrush(Color.FromRgb(251, 113, 133));
        txtSessionStatus.Text = "会话已结束";
    }

    private void btnCloseSession_Click(object sender, RoutedEventArgs e) => Close();

    private void ScrcpyHost_VideoSizeChanged(object? sender, VideoSizeChangedEventArgs e)
    {
        if (e.Width <= 0 || e.Height <= 0 || _isWindowAnimating)
            return;

        double aspectRatio = (double)e.Width / e.Height;
        bool isFirstVideoFrame = !_videoSizeReceived;
        _videoSizeReceived = true;
        if (!isFirstVideoFrame && Math.Abs(aspectRatio - _deviceAspectRatio) < 0.01)
            return;

        _deviceAspectRatio = aspectRatio;
        ResizeWindowForVideo(e.Width, e.Height);
    }

    private void ResizeWindowForVideo(int videoWidth, int videoHeight)
    {
        UpdateWindowMetrics();
        Rect workArea = SystemParameters.WorkArea;
        double extraWidth = _nonClientWidthPixels / _dpiScaleX +
            (_controlPanelOpen ? ControlPanelWidth : 0);
        double extraHeight = _nonClientHeightPixels / _dpiScaleY + StatusBarHeight;
        double maximumWidth = Math.Max(240, workArea.Width - extraWidth - 48);
        double maximumHeight = Math.Max(240, workArea.Height - extraHeight - 48);
        double scale = Math.Min(1, Math.Min(
            maximumWidth / videoWidth,
            maximumHeight / videoHeight));
        double screenWidth = Math.Max(240, Math.Round(videoWidth * scale));
        double screenHeight = screenWidth / _deviceAspectRatio;

        double targetWidth = screenWidth + extraWidth;
        double targetHeight = screenHeight + extraHeight;
        double targetLeft = Math.Clamp(
            Left - (targetWidth - ActualWidth) / 2,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - targetWidth));
        double targetTop = Math.Clamp(
            Top - (targetHeight - ActualHeight) / 2,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - targetHeight));

        AnimateWindowTo(targetLeft, targetTop, targetWidth, targetHeight);
    }

    private void AnimateWindowTo(double left, double top, double width, double height)
    {
        if (!AppPreferences.WindowAnimationsEnabled)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            UpdateLayout();
            return;
        }

        _isWindowAnimating = true;
        var duration = TimeSpan.FromMilliseconds(360);
        var easing = new QuarticEase { EasingMode = EasingMode.EaseOut };
        AnimateWindowProperty(LeftProperty, Left, left, duration, easing);
        AnimateWindowProperty(TopProperty, Top, top, duration, easing);
        AnimateWindowProperty(WidthProperty, ActualWidth, width, duration, easing);
        AnimateWindowProperty(
            HeightProperty,
            ActualHeight,
            height,
            duration,
            easing,
            () => _isWindowAnimating = false);
    }

    private void AnimateWindowProperty(
        DependencyProperty property,
        double from,
        double to,
        TimeSpan duration,
        IEasingFunction easing,
        Action? completed = null)
    {
        BeginAnimation(property, null);
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            SetValue(property, to);
            completed?.Invoke();
        };
        BeginAnimation(property, animation);
    }

    private void ScrcpyWindow_Closing(object? sender, CancelEventArgs e)
    {
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        scrcpyHost.VideoSizeChanged -= ScrcpyHost_VideoSizeChanged;
        scrcpyHost.Stop();
    }

    private void UpdateWindowMetrics()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _dpiScaleX = dpi.DpiScaleX;
        _dpiScaleY = dpi.DpiScaleY;

        if (_windowHandle == IntPtr.Zero ||
            !GetWindowRect(_windowHandle, out NativeRect windowRect) ||
            !GetClientRect(_windowHandle, out NativeRect clientRect))
        {
            return;
        }

        _nonClientWidthPixels = Math.Max(0, windowRect.Width - clientRect.Width);
        _nonClientHeightPixels = Math.Max(0, windowRect.Height - clientRect.Height);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmSizing || lParam == IntPtr.Zero)
            return IntPtr.Zero;

        var rect = Marshal.PtrToStructure<NativeRect>(lParam);
        int edge = wParam.ToInt32();
        int extraWidth = _nonClientWidthPixels +
            (int)Math.Round((_controlPanelOpen ? ControlPanelWidth : 0) * _dpiScaleX);
        int extraHeight = _nonClientHeightPixels +
            (int)Math.Round(StatusBarHeight * _dpiScaleY);
        int screenWidth = Math.Max(1, rect.Width - extraWidth);
        int screenHeight = Math.Max(1, rect.Height - extraHeight);

        if (edge is WmszLeft or WmszRight)
        {
            SetRectHeight(ref rect, edge, (int)Math.Round(screenWidth / _deviceAspectRatio) + extraHeight);
        }
        else if (edge is WmszTop or WmszBottom)
        {
            SetRectWidth(ref rect, edge, (int)Math.Round(screenHeight * _deviceAspectRatio) + extraWidth);
        }
        else
        {
            int heightFromWidth = (int)Math.Round(screenWidth / _deviceAspectRatio) + extraHeight;
            int widthFromHeight = (int)Math.Round(screenHeight * _deviceAspectRatio) + extraWidth;
            if (Math.Abs(heightFromWidth - rect.Height) <= Math.Abs(widthFromHeight - rect.Width))
                SetRectHeight(ref rect, edge, heightFromWidth);
            else
                SetRectWidth(ref rect, edge, widthFromHeight);
        }

        Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private static void SetRectWidth(ref NativeRect rect, int edge, int width)
    {
        width = Math.Max(1, width);
        if (edge is WmszLeft or WmszTopLeft or WmszBottomLeft)
            rect.Left = rect.Right - width;
        else
            rect.Right = rect.Left + width;
    }

    private static void SetRectHeight(ref NativeRect rect, int edge, int height)
    {
        height = Math.Max(1, height);
        if (edge is WmszTop or WmszTopLeft or WmszTopRight)
            rect.Top = rect.Bottom - height;
        else
            rect.Bottom = rect.Top + height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
}
