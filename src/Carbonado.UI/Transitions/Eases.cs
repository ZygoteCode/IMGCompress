using System.Windows.Media.Animation;
using Carbonado.UI.Motion;

namespace Carbonado.UI.Transitions;

/// <summary>
/// Brand easing curves (spec §3, same values as the <c>Ease.*</c> tokens) for animations built in code.
/// WPF easing objects are created once per UI thread and frozen; the raw curves are thread-safe structs.
/// </summary>
internal static class Eases
{
    /// <summary>Crystallize: fast attack, long soft landing.</summary>
    public static readonly CubicBezier CrystallizeCurve = new(0.16, 1, 0.3, 1);

    [ThreadStatic]
    private static BezierEase? t_crystallize;

    [ThreadStatic]
    private static BezierEase? t_glide;

    [ThreadStatic]
    private static BackEase? t_settle;

    public static IEasingFunction Crystallize => t_crystallize ??= Frozen(new BezierEase { X1 = 0.16, Y1 = 1, X2 = 0.3, Y2 = 1 });

    public static IEasingFunction Glide => t_glide ??= Frozen(new BezierEase { X1 = 0.4, Y1 = 0, X2 = 0.2, Y2 = 1 });

    public static IEasingFunction Settle => t_settle ??= Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    private static T Frozen<T>(T ease)
        where T : EasingFunctionBase
    {
        ease.Freeze();
        return ease;
    }
}
