using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Carbonado.UI.Motion;

namespace Carbonado.UI.Transitions;

/// <summary>
/// The brand "enter" gesture for one element: opacity 0 → base and a vertical offset → 0 (Crystallize).
/// The element is hidden from the first frame, so a delayed element never flashes before its turn; the animations
/// release the properties when they finish. Reduced motion keeps only a short fade with no delay.
/// </summary>
internal static class EnterAnimation
{
    public static void Play(UIElement element, double offsetY, TimeSpan duration, TimeSpan delay)
    {
        bool reduced = MotionSettings.ReducedMotion;
        var span = MotionSettings.Scale(duration);
        var wait = reduced ? TimeSpan.Zero : MotionSettings.Scale(delay);
        double target = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);

        element.BeginAnimation(UIElement.OpacityProperty, Hold(0, target, wait, span, Eases.Glide));
        if (!reduced && offsetY != 0 && TranslateOf(element) is { } translate)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, Hold(offsetY, 0, wait, span, Eases.Crystallize));
        }
    }

    /// <summary>Key frames: <paramref name="from"/> held until <paramref name="wait"/>, then eased to <paramref name="to"/>.</summary>
    internal static DoubleAnimationUsingKeyFrames Hold(double from, double to, TimeSpan wait, TimeSpan span, IEasingFunction ease)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (wait > TimeSpan.Zero)
        {
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(wait)));
        }

        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(wait + span), ease));
        animation.Freeze();
        return animation;
    }

    /// <summary>
    /// The element's animatable translate transform, creating one when it has no transform; null when it uses a
    /// transform we must not replace (the enter then degrades to a fade).
    /// </summary>
    internal static TranslateTransform? TranslateOf(UIElement element)
    {
        switch (element.RenderTransform)
        {
            case TranslateTransform { IsFrozen: false } existing:
                return existing;
            case TransformGroup { IsFrozen: false } group:
                return group.Children.OfType<TranslateTransform>().FirstOrDefault(t => !t.IsFrozen);
            case null:
            case var identity when identity == Transform.Identity || (identity.IsFrozen && identity.Value.IsIdentity):
                var created = new TranslateTransform();
                element.RenderTransform = created;
                return created;
            default:
                return null;
        }
    }
}
