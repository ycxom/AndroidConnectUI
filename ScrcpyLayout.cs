using System.Windows;

namespace AndroidConnectUI;

/// <summary>
/// Converts between the mirrored video viewport and the window frame around it.
/// Every sizing path — startup, rotation, panel toggle, manual resize — goes through
/// here, so the chrome arithmetic exists in exactly one place and cannot drift
/// between the DIP-based layout code and the pixel-based WM_SIZING hook.
/// </summary>
internal readonly record struct ScrcpyLayout(
    double NonClientWidth,
    double NonClientHeight,
    double StatusBarHeight,
    double PanelWidth)
{
    /// <summary>Everything to the left/right of the viewport: window border plus control panel.</summary>
    public double ChromeWidth => NonClientWidth + PanelWidth;

    /// <summary>Everything above/below the viewport: title bar plus session status bar.</summary>
    public double ChromeHeight => NonClientHeight + StatusBarHeight;

    public Size ToWindowSize(Size viewport) =>
        new(viewport.Width + ChromeWidth, viewport.Height + ChromeHeight);

    public Size ToViewportSize(Size window) => new(
        Math.Max(1, window.Width - ChromeWidth),
        Math.Max(1, window.Height - ChromeHeight));

    /// <summary>
    /// Reproduces this layout in another unit. The WM_SIZING hook works in physical
    /// pixels while everything else works in DIPs; this keeps them the same object.
    /// </summary>
    public ScrcpyLayout Scale(double scaleX, double scaleY) => new(
        NonClientWidth * scaleX,
        NonClientHeight * scaleY,
        StatusBarHeight * scaleY,
        PanelWidth * scaleX);

    /// <summary>
    /// Rebuilds a viewport at <paramref name="aspectRatio"/> covering the same
    /// <paramref name="area"/> as before, shrunk uniformly if it would not fit
    /// <paramref name="maxViewport"/>. Holding the area constant is what makes a
    /// rotation read as a reshape rather than a jump back to a fit-to-desktop size.
    /// </summary>
    public static Size ViewportForArea(double aspectRatio, double area, Size maxViewport)
    {
        if (aspectRatio <= 0 || area <= 0)
            return maxViewport;

        double width = Math.Sqrt(area * aspectRatio);
        double height = width / aspectRatio;
        double fit = Math.Min(1, Math.Min(maxViewport.Width / width, maxViewport.Height / height));
        return new Size(width * fit, height * fit);
    }

    /// <summary>Centres <paramref name="size"/> on the old rect, then clamps it into <paramref name="workArea"/>.</summary>
    public static Rect CenterWithin(Rect previous, Size size, Rect workArea)
    {
        double left = Math.Clamp(
            previous.Left + (previous.Width - size.Width) / 2,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - size.Width));
        double top = Math.Clamp(
            previous.Top + (previous.Height - size.Height) / 2,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - size.Height));
        return new Rect(left, top, size.Width, size.Height);
    }
}
