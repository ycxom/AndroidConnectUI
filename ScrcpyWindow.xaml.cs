using System.ComponentModel;
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
    private const double StatusBarHeight = 48;
    private const double ControlPanelWidth = 318;
    private const double InitialViewportLongSide = 420;
    private const double MinimumViewportSide = 240;
    private const double WorkAreaMargin = 48;

    private readonly AdbClient _adb;
    private readonly string _scrcpyPath;
    private readonly string _serial;
    private readonly WindowRectAnimator _animator;
    private readonly double _devicePixelArea;
    private DeviceDisplayMonitor? _displayMonitor;

    // The three pieces of state that decide the window rect. Everything else about
    // sizing is derived, so there is no second source of truth to drift out of sync.
    private double _aspectRatio;
    private double _viewportArea;
    private bool _controlPanelOpen;

    private double _pendingAspectRatio;
    private bool _controlsLoaded;
    private bool _videoLoaded;
    private bool _decoderReportsVideoSize;
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
        _adb = new AdbClient(adbPath, serial);
        _scrcpyPath = scrcpyPath;
        _serial = serial;
        displayWidth = Math.Max(1, displayWidth);
        displayHeight = Math.Max(1, displayHeight);
        _aspectRatio = (double)displayWidth / displayHeight;
        _devicePixelArea = (double)displayWidth * displayHeight;

        InitializeComponent();
        _animator = new WindowRectAnimator(this);
        _animator.Settled += OnRectSettled;
        ConfigureInitialViewport(displayWidth, displayHeight);

        txtDeviceSerial.Text = serial;
        Title = $"Android Connect · {serial}";
        scrcpyHost.SessionEnded += ScrcpyHost_SessionEnded;
        scrcpyHost.VideoSizeChanged += ScrcpyHost_VideoSizeChanged;
        SourceInitialized += ScrcpyWindow_SourceInitialized;
        Loaded += ScrcpyWindow_Loaded;
        SizeChanged += ScrcpyWindow_SizeChanged;
        Closing += ScrcpyWindow_Closing;
    }

    // ---- Geometry -----------------------------------------------------------

    /// <summary>
    /// The chrome around the viewport right now. The control panel is just another
    /// inset here, which is what lets opening it widen the window by exactly its own
    /// width while the video keeps its size and aspect.
    /// </summary>
    private ScrcpyLayout CurrentLayout() => new(
        _nonClientWidthPixels / _dpiScaleX,
        _nonClientHeightPixels / _dpiScaleY,
        StatusBarHeight,
        _controlPanelOpen ? ControlPanelWidth : 0);

    private void ConfigureInitialViewport(int displayWidth, int displayHeight)
    {
        double scale = Math.Min(1, InitialViewportLongSide / Math.Max(displayWidth, displayHeight));
        double width = Math.Max(1, Math.Round(displayWidth * scale));
        double height = Math.Max(1, Math.Round(displayHeight * scale));

        screenViewport.Width = width;
        screenViewport.Height = height;
        _viewportArea = width * height;
        MinWidth = 240;
        MinHeight = 200;
    }

    /// <summary>
    /// Rebuilds the window rect from <see cref="_aspectRatio"/>, <see cref="_viewportArea"/>
    /// and the panel state, keeping the window centred where it already is. This is the
    /// only method that sets the window's size.
    /// </summary>
    private void ApplyLayout()
    {
        if (WindowState != WindowState.Normal)
            return;

        UpdateWindowMetrics();
        ScrcpyLayout layout = CurrentLayout();
        Rect workArea = SystemParameters.WorkArea;
        Size maxViewport = new(
            Math.Max(MinimumViewportSide, workArea.Width - layout.ChromeWidth - WorkAreaMargin),
            Math.Max(MinimumViewportSide, workArea.Height - layout.ChromeHeight - WorkAreaMargin));

        Size viewport = ScrcpyLayout.ViewportForArea(_aspectRatio, _viewportArea, maxViewport);
        Size target = layout.ToWindowSize(viewport);
        target = new Size(Math.Round(target.Width), Math.Round(target.Height));

        Rect current = new(Left, Top, ActualWidth, ActualHeight);
        _animator.AnimateTo(ScrcpyLayout.CenterWithin(current, target, workArea));
    }

    /// <summary>Records the size the user dragged the window to, so rotations preserve it.</summary>
    private void ScrcpyWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_animator.IsAnimating || WindowState != WindowState.Normal)
            return;

        Size viewport = CurrentLayout().ToViewportSize(e.NewSize);
        _viewportArea = viewport.Width * viewport.Height;
    }

    private void ScrcpyHost_VideoSizeChanged(object? sender, VideoSizeChangedEventArgs e)
    {
        if (e.Width <= 0 || e.Height <= 0)
            return;

        if (e.IsDecoderReported)
        {
            _decoderReportsVideoSize = true;
            StopDisplayMonitor();
        }

        double aspectRatio = (double)e.Width / e.Height;
        if (!_videoLoaded)
        {
            _videoLoaded = true;
            _aspectRatio = aspectRatio;
            ExpandViewportForLoadedVideo();
            return;
        }

        ObserveAspectRatio(aspectRatio, preemptAnimation: true);
    }

    /// <summary>
    /// Replaces the compact loading area with the device's native area (fitted to the
    /// desktop). This is deliberately driven by the first real scrcpy window rather
    /// than by Loaded, so the black loading window grows exactly when video is ready.
    /// </summary>
    private void ExpandViewportForLoadedVideo()
    {
        UpdateWindowMetrics();
        ScrcpyLayout layout = CurrentLayout();
        Rect workArea = SystemParameters.WorkArea;
        Size maxViewport = new(
            Math.Max(MinimumViewportSide, workArea.Width - layout.ChromeWidth - WorkAreaMargin),
            Math.Max(MinimumViewportSide, workArea.Height - layout.ChromeHeight - WorkAreaMargin));
        Size viewport = ScrcpyLayout.ViewportForArea(_aspectRatio, _devicePixelArea, maxViewport);
        _viewportArea = viewport.Width * viewport.Height;
        ApplyLayout();
    }

    /// <summary>Adopts a newly measured device aspect ratio and reshapes the window into it.</summary>
    private void ObserveAspectRatio(double aspectRatio) =>
        ObserveAspectRatio(aspectRatio, preemptAnimation: false);

    private void ObserveAspectRatio(double aspectRatio, bool preemptAnimation)
    {
        if (aspectRatio <= 0)
            return;

        if (Math.Abs(aspectRatio - _aspectRatio) < 0.01)
            return;

        // A change arriving mid-animation must not be dropped, or the window keeps the
        // stale ratio until the next rotation. Hold it and flush once the rect settles.
        if (_animator.IsAnimating && !preemptAnimation)
        {
            _pendingAspectRatio = aspectRatio;
            return;
        }

        // A Texture event comes from the decoder itself and is newer than any queued
        // ADB observation. Clear the stale value and let AnimateTo replace the current
        // animation immediately.
        if (preemptAnimation)
            _pendingAspectRatio = 0;
        _aspectRatio = aspectRatio;

        // A maximized window cannot honour the ratio. Keep the value so a later manual
        // resize is constrained correctly, but leave the frame alone.
        if (WindowState == WindowState.Normal)
            ApplyLayout();
    }

    private void OnRectSettled()
    {
        if (_pendingAspectRatio <= 0)
            return;

        double pending = _pendingAspectRatio;
        _pendingAspectRatio = 0;
        ObserveAspectRatio(pending);
    }

    // ---- Lifecycle ----------------------------------------------------------

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
        // ConfigureInitialViewport already sized the viewport to the device ratio, so
        // measure straight from it. Re-deriving the height from the measured width would
        // inherit any stretch the status bar imposed and skew a portrait window.
        UpdateLayout();

        var target = new Rect(Left, Top, ActualWidth, ActualHeight);
        SizeToContent = SizeToContent.Manual;
        Width = target.Width;
        Height = target.Height;
        screenViewport.ClearValue(WidthProperty);
        screenViewport.ClearValue(HeightProperty);
        UpdateWindowMetrics();

        // Keep the compact black frame stable. The first real scrcpy video size will
        // expand it; animating here caused a second, conflicting startup scale pass.
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

        if (!_decoderReportsVideoSize)
        {
            _displayMonitor = new DeviceDisplayMonitor(_adb);
            _displayMonitor.AspectRatioObserved += ObserveAspectRatio;
            _displayMonitor.Start();
        }

        await LoadDeviceControlsAsync();
    }

    private void ScrcpyWindow_Closing(object? sender, CancelEventArgs e)
    {
        StopDisplayMonitor();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        scrcpyHost.VideoSizeChanged -= ScrcpyHost_VideoSizeChanged;
        scrcpyHost.Stop();
    }

    private void ScrcpyHost_SessionEnded(object? sender, EventArgs e)
    {
        StopDisplayMonitor();
        scrcpyHost.Visibility = Visibility.Collapsed;
        screenPlaceholder.Visibility = Visibility.Visible;
        txtScreenMessage.Text = "scrcpy 会话已结束";
        sessionDot.Fill = new SolidColorBrush(Color.FromRgb(251, 113, 133));
        txtSessionStatus.Text = "会话已结束";
    }

    private void StopDisplayMonitor()
    {
        if (_displayMonitor is null)
            return;

        _displayMonitor.AspectRatioObserved -= ObserveAspectRatio;
        _displayMonitor.Dispose();
        _displayMonitor = null;
    }

    private void btnCloseSession_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Control panel ------------------------------------------------------

    private void btnToggleControlPanel_Click(object sender, RoutedEventArgs e)
    {
        _controlPanelOpen = !_controlPanelOpen;
        btnTogglePanel.Content = _controlPanelOpen ? "" : "";
        btnTogglePanel.ToolTip = _controlPanelOpen ? "收起设备控制面板" : "展开设备控制面板";

        double targetPanelWidth = _controlPanelOpen ? ControlPanelWidth : 0;
        if (AppPreferences.WindowAnimationsEnabled)
        {
            AnimateWidth(controlPanel, targetPanelWidth);
            controlPanel.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = _controlPanelOpen ? 1 : 0,
                Duration = WindowRectAnimator.Duration,
                EasingFunction = WindowRectAnimator.Easing
            });
        }
        else
        {
            controlPanel.BeginAnimation(WidthProperty, null);
            controlPanel.BeginAnimation(OpacityProperty, null);
            controlPanel.Width = targetPanelWidth;
            controlPanel.Opacity = _controlPanelOpen ? 1 : 0;
        }

        // The panel is part of the chrome, so the shared layout pass widens or narrows
        // the window by exactly its width while the video keeps its size and aspect.
        ApplyLayout();
    }

    private static void AnimateWidth(FrameworkElement element, double target)
    {
        var animation = new DoubleAnimation
        {
            From = element.ActualWidth,
            To = target,
            Duration = WindowRectAnimator.Duration,
            EasingFunction = WindowRectAnimator.Easing,
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => element.Width = target;
        element.BeginAnimation(WidthProperty, animation);
    }

    // ---- Device controls ----------------------------------------------------

    private async Task LoadDeviceControlsAsync()
    {
        _controlsLoaded = false;
        var brightnessTask = _adb.RunAsync("shell settings get system screen_brightness");
        var rotationTask = _adb.RunAsync("shell settings get system accelerometer_rotation");
        var volumeTask = _adb.RunAsync("shell media volume --stream 3 --get");
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
        var (_, error) = await _adb.RunAsync(command);
        if (!string.IsNullOrWhiteSpace(error))
            txtSessionStatus.Text = "控制命令失败";
    }

    private async void toggleAutoRotate_Changed(object sender, RoutedEventArgs e)
    {
        if (!_controlsLoaded)
            return;
        int enabled = toggleAutoRotate.IsChecked == true ? 1 : 0;
        await _adb.RunAsync($"shell settings put system accelerometer_rotation {enabled}");
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
        await _adb.RunAsync(command);
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

        var (_, error) = await _adb.RunAsync($"shell input keyevent {keyCode}");
        if (!string.IsNullOrWhiteSpace(error))
            txtSessionStatus.Text = "按键发送失败";
    }

    // ---- Native resize constraint -------------------------------------------

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

    /// <summary>Constrains interactive resizing to the device aspect ratio.</summary>
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

        // Same layout object as ApplyLayout, expressed in physical pixels.
        ScrcpyLayout layout = CurrentLayout().Scale(_dpiScaleX, _dpiScaleY);
        int extraWidth = (int)Math.Round(layout.ChromeWidth);
        int extraHeight = (int)Math.Round(layout.ChromeHeight);
        int viewportWidth = Math.Max(1, rect.Width - extraWidth);
        int viewportHeight = Math.Max(1, rect.Height - extraHeight);

        int heightFromWidth = (int)Math.Round(viewportWidth / _aspectRatio) + extraHeight;
        int widthFromHeight = (int)Math.Round(viewportHeight * _aspectRatio) + extraWidth;

        if (edge is WmszLeft or WmszRight)
        {
            SetRectHeight(ref rect, edge, heightFromWidth);
        }
        else if (edge is WmszTop or WmszBottom)
        {
            SetRectWidth(ref rect, edge, widthFromHeight);
        }
        else if (Math.Abs(heightFromWidth - rect.Height) <= Math.Abs(widthFromHeight - rect.Width))
        {
            SetRectHeight(ref rect, edge, heightFromWidth);
        }
        else
        {
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

    private const int WmSizing = 0x0214;
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;

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
