using System.Windows;
using System.Windows.Media.Animation;

namespace AndroidConnectUI;

/// <summary>
/// Moves a window to a target rect as one motion. First-frame reveal, rotation and
/// panel toggling all drive this, so they share a single duration and easing.
/// </summary>
internal sealed class WindowRectAnimator(Window window)
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(240);
    public static readonly IEasingFunction Easing = new QuarticEase { EasingMode = EasingMode.EaseOut };

    /// <summary>True while a rect animation is in flight; layout updates defer until it settles.</summary>
    public bool IsAnimating { get; private set; }

    /// <summary>Raised once the rect has fully settled, including when animations are disabled.</summary>
    public event Action? Settled;

    public void AnimateTo(Rect target)
    {
        if (!AppPreferences.WindowAnimationsEnabled)
        {
            foreach (DependencyProperty property in RectProperties)
                window.BeginAnimation(property, null);
            window.Left = target.Left;
            window.Top = target.Top;
            window.Width = target.Width;
            window.Height = target.Height;
            IsAnimating = false;
            Settled?.Invoke();
            return;
        }

        IsAnimating = true;
        Animate(Window.LeftProperty, window.Left, target.Left);
        Animate(Window.TopProperty, window.Top, target.Top);
        Animate(FrameworkElement.WidthProperty, window.ActualWidth, target.Width);
        Animate(FrameworkElement.HeightProperty, window.ActualHeight, target.Height, isLast: true);
    }

    /// <summary>Fades the window in while <see cref="AnimateTo"/> grows it into place.</summary>
    public void FadeIn()
    {
        if (!AppPreferences.WindowAnimationsEnabled)
            return;

        window.Opacity = 0;
        window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = Duration,
            EasingFunction = Easing
        });
    }

    private static readonly DependencyProperty[] RectProperties =
    [
        Window.LeftProperty,
        Window.TopProperty,
        FrameworkElement.WidthProperty,
        FrameworkElement.HeightProperty
    ];

    private void Animate(DependencyProperty property, double from, double to, bool isLast = false)
    {
        // Clearing first stops any in-flight animation on this property; its Completed
        // never fires, which is why only the last property owns the Settled signal.
        window.BeginAnimation(property, null);
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = Duration,
            EasingFunction = Easing,
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            window.SetValue(property, to);
            if (!isLast)
                return;
            IsAnimating = false;
            Settled?.Invoke();
        };
        window.BeginAnimation(property, animation);
    }
}
