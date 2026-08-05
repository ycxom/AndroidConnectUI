using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AndroidConnectUI;

public sealed class ScrcpyHost : HwndHost
{
    private static readonly Regex TextureSizePattern = new(
        @"\bTexture:\s*(\d+)x(\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_SYSMENU = 0x00080000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_POPUP = unchecked((long)0x80000000);
    private const long SS_BLACKRECT = 0x00000004L;
    private const long WS_EX_DLGMODALFRAME = 0x00000001L;
    private const long WS_EX_WINDOWEDGE = 0x00000100L;
    private const long WS_EX_CLIENTEDGE = 0x00000200L;
    private const long WS_EX_STATICEDGE = 0x00020000L;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private IntPtr _hostHandle;
    private IntPtr _scrcpyHandle;
    private Process? _process;

    public event EventHandler? SessionEnded;

    /// <summary>
    /// Raised once with scrcpy's genuine video size, measured while it is still a
    /// top-level window. After embedding, the child is force-sized to the host, so
    /// its client rect only echoes back our own layout and cannot be measured again.
    /// </summary>
    public event EventHandler<VideoSizeChangedEventArgs>? VideoSizeChanged;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hostHandle = CreateWindowEx(
            0, "static", string.Empty, (int)(WS_CHILD | SS_BLACKRECT),
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hostHandle == IntPtr.Zero)
            throw new InvalidOperationException("无法创建 scrcpy 宿主窗口。");
        return new HandleRef(this, _hostHandle);
    }

    public async Task StartAsync(string scrcpyPath, string serial)
    {
        Stop();
        if (_hostHandle != IntPtr.Zero)
            _ = ShowWindow(_hostHandle, SW_HIDE);
        var startInfo = new ProcessStartInfo
        {
            FileName = scrcpyPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--serial");
        startInfo.ArgumentList.Add(serial);
        startInfo.ArgumentList.Add("--turn-screen-off");
        startInfo.ArgumentList.Add("--window-title");
        startInfo.ArgumentList.Add($"Android Connect · {serial}");
        // scrcpy 在完成嵌入前是独立顶层窗口，先在屏幕外创建可避免启动闪窗。
        startInfo.ArgumentList.Add("--window-x=-32000");
        startInfo.ArgumentList.Add("--window-x=-32000");
        startInfo.ArgumentList.Add("--window-y=-32000");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("scrcpy 启动失败。");
        _process = process;
        process.OutputDataReceived += Process_LogDataReceived;
        process.ErrorDataReceived += Process_LogDataReceived;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        IntPtr window = IntPtr.Zero;
        for (int attempt = 0; attempt < 200 && !process.HasExited; attempt++)
        {
            process.Refresh();
            window = process.MainWindowHandle;
            if (window != IntPtr.Zero)
                break;
            await Task.Delay(50);
        }

        if (process.HasExited)
        {
            int exitCode = process.ExitCode;
            Stop();
            throw new InvalidOperationException($"scrcpy 已退出，退出代码：{exitCode}。");
        }
        if (window == IntPtr.Zero)
        {
            Stop();
            throw new TimeoutException("等待 scrcpy 视窗超时。");
        }

        ReportVideoSize(window);

        long style = GetWindowLongPtr(window, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_POPUP);
        style |= WS_CHILD | WS_VISIBLE;
        _ = SetWindowLongPtr(window, GWL_STYLE, new IntPtr(style));
        long exStyle = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        exStyle &= ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE);
        _ = SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(exStyle));
        _ = SetParent(window, _hostHandle);
        if (!IsChild(_hostHandle, window))
        {
            Stop();
            throw new InvalidOperationException("无法将 scrcpy 嵌入设备视窗。");
        }

        _ = SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        _scrcpyHandle = window;
        process.Exited += Process_Exited;
        ResizeChild();
        _ = ShowWindow(_hostHandle, SW_SHOW);
        _ = ShowWindow(window, SW_SHOW);
    }

    public void Stop()
    {
        if (_hostHandle != IntPtr.Zero)
            _ = ShowWindow(_hostHandle, SW_HIDE);
        _scrcpyHandle = IntPtr.Zero;
        Process? process = _process;
        _process = null;
        if (process is null)
            return;
        process.Exited -= Process_Exited;
        process.OutputDataReceived -= Process_LogDataReceived;
        process.ErrorDataReceived -= Process_LogDataReceived;
        try { process.CancelOutputRead(); } catch { }
        try { process.CancelErrorRead(); } catch { }
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
        process.Dispose();
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizeChild();
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Stop();
        if (hwnd.Handle != IntPtr.Zero)
            _ = DestroyWindow(hwnd.Handle);
        _hostHandle = IntPtr.Zero;
    }

    private void ResizeChild()
    {
        if (_scrcpyHandle != IntPtr.Zero)
            _ = MoveWindow(_scrcpyHandle, 0, 0,
                Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), true);
    }

    private void ReportVideoSize(IntPtr window)
    {
        if (!GetClientRect(window, out NativeRect rect) || rect.Width <= 0 || rect.Height <= 0)
            return;

        VideoSizeChanged?.Invoke(
            this,
            new VideoSizeChangedEventArgs(rect.Width, rect.Height, isDecoderReported: false));
    }

    private void Process_LogDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;

        Match match = TextureSizePattern.Match(e.Data);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out int width) ||
            !int.TryParse(match.Groups[2].Value, out int height) ||
            width <= 0 || height <= 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(sender, _process))
                VideoSizeChanged?.Invoke(
                    this,
                    new VideoSizeChangedEventArgs(width, height, isDecoderReported: true));
        });
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _process))
                return;
            if (sender is Process process)
            {
                process.OutputDataReceived -= Process_LogDataReceived;
                process.ErrorDataReceived -= Process_LogDataReceived;
                try { process.CancelOutputRead(); } catch { }
                try { process.CancelErrorRead(); } catch { }
            }
            _scrcpyHandle = IntPtr.Zero;
            if (_hostHandle != IntPtr.Zero)
                _ = ShowWindow(_hostHandle, SW_HIDE);
            _process?.Dispose();
            _process = null;
            SessionEnded?.Invoke(this, EventArgs.Empty);
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

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
}

public sealed class VideoSizeChangedEventArgs(
    int width,
    int height,
    bool isDecoderReported) : EventArgs
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public bool IsDecoderReported { get; } = isDecoderReported;
}
