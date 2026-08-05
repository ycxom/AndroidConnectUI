using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace AndroidConnectUI;

/// <summary>
/// Tracks the mirrored display's aspect ratio by polling the device.
/// <para>
/// The embedded scrcpy child window is force-sized to its host, so its client rect
/// only echoes back our own layout and can never reveal a rotation — the device
/// itself is the only usable source.
/// </para>
/// </summary>
internal sealed class DeviceDisplayMonitor(AdbClient adb) : IDisposable
{
    private const string DisplayProbeCommand =
        "shell \"dumpsys window displays | grep -A 8 'mDisplayId=0'\"";

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background)
    {
        // The compact probe takes about 100 ms on a physical device. A 150 ms
        // cadence keeps rotation response near one frame of UI interaction without
        // allowing requests to overlap on slower devices.
        Interval = TimeSpan.FromMilliseconds(150)
    };
    private bool _pollInFlight;

    /// <summary>Raised on the UI thread whenever a poll succeeds, with the current ratio.</summary>
    public event Action<double>? AspectRatioObserved;

    public void Start()
    {
        _timer.Tick += Timer_Tick;
        _timer.Start();
        _ = PollAsync();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }

    private void Timer_Tick(object? sender, EventArgs e) => _ = PollAsync();

    private async Task PollAsync()
    {
        if (_pollInFlight)
            return;

        _pollInFlight = true;
        try
        {
            var (output, _) = await adb.RunAsync(DisplayProbeCommand);
            if (TryParseAspectRatio(output, out double aspectRatio))
                AspectRatioObserved?.Invoke(aspectRatio);
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    /// <summary>
    /// Extracts the mirrored display's aspect ratio from "dumpsys window displays".
    /// Pure and deliberately internal so it can be exercised against captured output.
    /// </summary>
    internal static bool TryParseAspectRatio(string dumpsys, out double aspectRatio)
    {
        aspectRatio = 0;
        if (string.IsNullOrEmpty(dumpsys))
            return false;

        // Devices list virtual displays (overlays, app pairs) *before* the physical
        // one, and those never rotate, so anchor onto display 0 rather than taking
        // the first match in the dump.
        Match anchor = Regex.Match(dumpsys, @"mDisplayId=0\b");
        if (!anchor.Success)
            return false;
        string block = dumpsys[anchor.Index..];

        // The display bounds are already expressed in the current orientation and
        // are therefore more reliable than combining the natural size with rotation.
        Match bounds = Regex.Match(
            block,
            @"mBounds=Rect\(\s*0\s*,\s*0\s*-\s*(\d+)\s*,\s*(\d+)\s*\)");
        if (bounds.Success &&
            int.TryParse(bounds.Groups[1].Value, out int boundsWidth) &&
            int.TryParse(bounds.Groups[2].Value, out int boundsHeight) &&
            boundsWidth > 0 && boundsHeight > 0)
        {
            aspectRatio = (double)boundsWidth / boundsHeight;
            return true;
        }

        // "cur=" reflects any resolution override, but on some builds it stays in the
        // device's natural orientation, so the reported rotation is the authority and
        // "cur=" only contributes the short and long side.
        Match size = Regex.Match(block, @"\bcur=(\d+)x(\d+)");
        if (!size.Success ||
            !int.TryParse(size.Groups[1].Value, out int width) ||
            !int.TryParse(size.Groups[2].Value, out int height) ||
            width <= 0 || height <= 0)
        {
            return false;
        }

        int shortSide = Math.Min(width, height);
        int longSide = Math.Max(width, height);
        Match rotation = Regex.Match(block, @"\bmRotation=ROTATION_(\d+)");
        bool isLandscape = rotation.Success &&
            int.TryParse(rotation.Groups[1].Value, out int turn) &&
            turn is 1 or 3 or 90 or 270;

        aspectRatio = isLandscape
            ? (double)longSide / shortSide
            : (double)shortSide / longSide;
        return true;
    }
}
