using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Carbonado.UI.Motion;
using Carbonado.UI.Transitions;

namespace Carbonado.UI.Controls;

/// <summary>
/// Vertical navigation rail of <see cref="NavItem"/>s. A single prismatic indicator glides (Crystallize) to the
/// selected item instead of each item lighting its own marker; keyboard navigation is the ListBox's.
/// </summary>
[TemplatePart(Name = IndicatorPart, Type = typeof(FrameworkElement))]
public class NavRail : ListBox
{
    private const string IndicatorPart = "PART_Indicator";
    private static readonly TimeSpan GlideTime = TimeSpan.FromMilliseconds(320);

    private readonly TranslateTransform _indicatorOffset = new();
    private FrameworkElement? _indicator;
    private bool _placed;

    public NavRail()
    {
        SelectionMode = SelectionMode.Single;
    }

    /// <summary>Vertical offset the indicator is moving to (tests and diagnostics).</summary>
    internal double IndicatorTarget { get; private set; }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _indicator = GetTemplateChild(IndicatorPart) as FrameworkElement;
        if (_indicator is not null)
        {
            _indicator.RenderTransform = _indicatorOffset;
            _indicator.Opacity = 0;
        }

        _placed = false;
    }

    protected override bool IsItemItsOwnContainerOverride(object item) => item is NavItem;

    protected override DependencyObject GetContainerForItemOverride() => new NavItem();

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        MoveIndicator(_placed);
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        var size = base.ArrangeOverride(arrangeBounds);
        if (!_indicatorOffset.HasAnimatedProperties)
        {
            // Layout changes (resize, items) re-pin the indicator without animating.
            Dispatcher.BeginInvoke(
                () =>
                {
                    if (!_indicatorOffset.HasAnimatedProperties)
                    {
                        MoveIndicator(false);
                    }
                },
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        return size;
    }

    private void MoveIndicator(bool animate)
    {
        if (_indicator?.Parent is not UIElement host)
        {
            return;
        }

        if (SelectedItem is null || ItemContainerGenerator.ContainerFromItem(SelectedItem) is not FrameworkElement container || !container.IsArrangeValid)
        {
            _indicator.Opacity = SelectedItem is null ? 0 : _indicator.Opacity;
            return;
        }

        double target = container.TranslatePoint(new Point(0, (container.ActualHeight - _indicator.ActualHeight) / 2), host).Y;
        IndicatorTarget = target;
        _indicator.Opacity = 1;
        if (!animate || MotionSettings.ReducedMotion || !IsLoaded)
        {
            _indicatorOffset.BeginAnimation(TranslateTransform.YProperty, null);
            _indicatorOffset.Y = target;
        }
        else
        {
            var glide = new DoubleAnimation(target, MotionSettings.Scale(GlideTime)) { EasingFunction = Eases.Crystallize };
            glide.Completed += (_, _) =>
            {
                _indicatorOffset.BeginAnimation(TranslateTransform.YProperty, null);
                _indicatorOffset.Y = IndicatorTarget;
            };
            _indicatorOffset.BeginAnimation(TranslateTransform.YProperty, glide);
        }

        _placed = true;
    }
}
