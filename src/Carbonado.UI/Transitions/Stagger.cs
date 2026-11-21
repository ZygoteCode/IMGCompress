using System.Windows;
using System.Windows.Media;

namespace Carbonado.UI.Transitions;

/// <summary>One element of a staggered reveal and the delay before it starts.</summary>
public readonly record struct StaggerItem(UIElement Element, int Index, TimeSpan Delay);

/// <summary>
/// Staggered composition: elements with <c>Stagger.Index</c> ≥ 0 enter in index order, each
/// <c>Index × Stagger.Step</c> after the reveal starts (rise 12 px + fade, Crystallize). <see cref="TransitionHost"/>
/// reveals its new content this way; call <see cref="Reveal"/> to play it on any subtree.
/// </summary>
public static class Stagger
{
    /// <summary>Default delay between consecutive indices (spec: 35 ms).</summary>
    public static readonly TimeSpan DefaultStep = TimeSpan.FromMilliseconds(35);

    /// <summary>Rise distance of a staggered element, in DIPs.</summary>
    public const double Rise = 12;

    /// <summary>Order of the element in its reveal; negative (default) = not staggered.</summary>
    public static readonly DependencyProperty IndexProperty = DependencyProperty.RegisterAttached(
        "Index", typeof(int), typeof(Stagger), new PropertyMetadata(-1));

    /// <summary>Delay between consecutive indices; inherited, so a container can set it for its children.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached(
        "Step", typeof(TimeSpan), typeof(Stagger),
        new FrameworkPropertyMetadata(DefaultStep, FrameworkPropertyMetadataOptions.Inherits), IsValidStep);

    public static int GetIndex(DependencyObject element) => (int)element.GetValue(IndexProperty);

    public static void SetIndex(DependencyObject element, int value) => element.SetValue(IndexProperty, value);

    public static TimeSpan GetStep(DependencyObject element) => (TimeSpan)element.GetValue(StepProperty);

    public static void SetStep(DependencyObject element, TimeSpan value) => element.SetValue(StepProperty, value);

    /// <summary>
    /// Staggered elements in the visual tree under <paramref name="root"/> (root included), ordered by index
    /// (ties keep tree order), with their start delays <c>Index × Step</c>.
    /// </summary>
    public static IReadOnlyList<StaggerItem> Plan(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var items = new List<StaggerItem>();
        Collect(root, items);
        return items
            .Select((item, order) => (item, order))
            .OrderBy(x => x.item.Index)
            .ThenBy(x => x.order)
            .Select(x => x.item)
            .ToList();
    }

    /// <summary>Plays the staggered enter on every element of <see cref="Plan"/>; returns how many were animated.</summary>
    public static int Reveal(DependencyObject root, TimeSpan baseDelay = default)
    {
        var plan = Plan(root);
        foreach (var item in plan)
        {
            EnterAnimation.Play(item.Element, Rise, TimeSpan.FromMilliseconds(420), baseDelay + item.Delay);
        }

        return plan.Count;
    }

    private static void Collect(DependencyObject node, List<StaggerItem> items)
    {
        if (node is UIElement element && GetIndex(element) is >= 0 and var index)
        {
            items.Add(new StaggerItem(element, index, GetStep(element) * index));
        }

        if (node is not Visual and not System.Windows.Media.Media3D.Visual3D)
        {
            return;
        }

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            Collect(VisualTreeHelper.GetChild(node, i), items);
        }
    }

    private static bool IsValidStep(object value) => value is TimeSpan step && step >= TimeSpan.Zero;
}
