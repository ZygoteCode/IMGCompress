using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Carbonado.UI.Controls;
using Carbonado.UI.Tests.TestSupport;
using Carbonado.UI.Tests.Theme;

namespace Carbonado.UI.Tests.Controls;

/// <summary>Keyboard, pointer, drag-and-drop and lifetime behaviour of the brand controls in real (off-screen) windows.</summary>
public class InteractionTests
{
    [Fact]
    public void NavRail_ArrowKeysMoveSelection_AndTheIndicatorFollows() => Sta.RunLoop(async () =>
    {
        var rail = new NavRail();
        foreach (string label in new[] { "Forge", "Compare", "Profiles", "Archive" })
        {
            rail.Items.Add(new NavItem { Label = label });
        }

        using var live = new LiveWindow(rail, 120, 400);
        await LiveWindow.Until(() => rail.IsLoaded);
        rail.SelectedIndex = 0;
        Assert.True(((NavItem)rail.Items[0]).Focus());

        Press(Key.Down);
        await LiveWindow.Until(() => rail.SelectedIndex == 1);
        Press(Key.Down);
        await LiveWindow.Until(() => rail.SelectedIndex == 2);
        Press(Key.Up);
        await LiveWindow.Until(() => rail.SelectedIndex == 1);
        Assert.True(((NavItem)rail.Items[1]).IsKeyboardFocused);

        var item = (NavItem)rail.Items[1];
        double expected = item.TranslatePoint(new Point(0, item.ActualHeight / 2), rail).Y;
        var indicator = (FrameworkElement)rail.Template.FindName("PART_Indicator", rail);
        await LiveWindow.Until(() => Math.Abs(indicator.TranslatePoint(new Point(0, indicator.ActualHeight / 2), rail).Y - expected) < 0.5);
    });

    [Fact]
    public void ModalHost_TrapsFocus_TabCyclesInsideTheDialog() => Sta.RunLoop(async () =>
    {
        var page = new Button { Content = "Page" };
        var first = new Button { Content = "First" };
        var last = new Button { Content = "Last" };
        var dialog = new StackPanel { Children = { first, new TextBox(), last } };
        var host = new ModalHost { Content = new StackPanel { Children = { page } }, Dialog = dialog };
        using var live = new LiveWindow(host);
        await LiveWindow.Until(() => host.IsLoaded);
        Assert.True(page.Focus());

        host.IsOpen = true;
        await LiveWindow.Until(() => first.IsKeyboardFocused);

        // Tab past the last control wraps to the first one (and Shift+Tab back), never into the page.
        Assert.True(last.Focus());
        last.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        Assert.True(first.IsKeyboardFocused);
        first.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
        Assert.True(last.IsKeyboardFocused);

        // Focusing the page behind the scrim is refused and focus returns to the dialog.
        page.Focus();
        Assert.False(page.IsKeyboardFocused);
        await LiveWindow.Until(() => first.IsKeyboardFocused);

        // Closing restores focus to where it was before the dialog opened.
        host.IsOpen = false;
        await LiveWindow.Until(() => page.IsKeyboardFocused);
    });

    [Fact]
    public void ModalHost_AllowsFocusInDialogPopups_AndEscapeClosesThePopupFirst() => Sta.RunLoop(async () =>
    {
        var page = new Button { Content = "Page" };
        var inPopup = new Button { Content = "In popup" };
        // An invisible popup inside the dialog stands for a combo drop-down or a menu.
        var popup = new Popup { AllowsTransparency = true, Child = new Border { Opacity = 0, Child = inPopup } };
        var ok = new Button { Content = "OK" };
        var host = new ModalHost { Content = page, Dialog = new StackPanel { Children = { ok, popup } } };
        using var live = new LiveWindow(host);
        await LiveWindow.Until(() => host.IsLoaded);
        host.IsOpen = true;
        await LiveWindow.Until(() => ok.IsKeyboardFocused);

        Assert.False(RaiseFocus(inPopup, ok), "focus moving into a popup owned by the dialog must be allowed");
        Assert.True(RaiseFocus(page, ok), "focus moving to the page behind the scrim must be refused");

        popup.IsOpen = true;
        await LiveWindow.Until(() => PresentationSource.FromVisual(inPopup) is not null);
        Assert.False(RaiseFocus(inPopup, ok), "focus inside the open popup must be allowed too");
        RaiseKey(inPopup, Key.Escape);
        Assert.True(host.IsOpen, "Esc inside an open popup must not close the dialog");
        popup.IsOpen = false;

        RaiseKey(ok, Key.Escape);
        Assert.False(host.IsOpen);
    });

    [Fact]
    public void DropZone_TracksDragEnterOverLeaveAndDrop() => Sta.Run(() =>
    {
        var content = new TextBlock { Text = "Drop images" };
        var zone = new DropZone { Content = content, Width = 300, Height = 180 };
        var stage = ThemeHarness.Stage(zone);
        ThemeHarness.Layout(stage, 320, 200);
        var group = DragStates(zone);
        var seen = new List<bool>();
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(DropZone.IsDragOverProperty, typeof(DropZone))
            .AddValueChanged(zone, (_, _) => seen.Add(zone.IsDragOver));
        string[] paths = [@"C:\Photos\aurora.png", @"D:\Scans"];
        var files = new DataObject(DataFormats.FileDrop, paths);

        // Enter with files: drag-over look, copy cursor.
        var enter = Drag(DragDrop.DragEnterEvent, files, content);
        content.RaiseEvent(enter);
        Assert.True(zone.IsDragOver);
        Assert.Equal(DragDropEffects.Copy, enter.Effects);
        Assert.Equal("DragOver", group.CurrentState?.Name);

        // Text is refused while hovering.
        var text = Drag(DragDrop.DragOverEvent, new DataObject(DataFormats.UnicodeText, "nope"), zone);
        zone.RaiseEvent(text);
        Assert.False(zone.IsDragOver);
        Assert.Equal(DragDropEffects.None, text.Effects);
        zone.RaiseEvent(Drag(DragDrop.DragOverEvent, files, zone));
        Assert.True(zone.IsDragOver);

        // Moving from the zone onto its content raises Leave then Enter in one pass: no flicker to Idle.
        seen.Clear();
        zone.RaiseEvent(Drag(DragDrop.DragLeaveEvent, files, zone));
        content.RaiseEvent(Drag(DragDrop.DragEnterEvent, files, content));
        Sta.DoEvents();
        Assert.True(zone.IsDragOver);
        Assert.Empty(seen);
        Assert.Equal("DragOver", group.CurrentState?.Name);

        // A real leave (no Enter follows) resets the look.
        zone.RaiseEvent(Drag(DragDrop.DragLeaveEvent, files, zone));
        Sta.DoEvents();
        Assert.False(zone.IsDragOver);
        Assert.Equal("Idle", group.CurrentState?.Name);

        // Drop raises FilesDropped with the paths and accepts the copy.
        IReadOnlyList<string>? dropped = null;
        zone.FilesDropped += (_, e) => dropped = e.Paths;
        zone.RaiseEvent(Drag(DragDrop.DragEnterEvent, files, zone));
        var drop = Drag(DragDrop.DropEvent, files, content);
        content.RaiseEvent(drop);
        Assert.Equal(paths, dropped);
        Assert.Equal(DragDropEffects.Copy, drop.Effects);
        Assert.False(zone.IsDragOver);
    });

    [Fact]
    public void DropZone_IgnoresDataThatFailsToRender() => Sta.Run(() =>
    {
        var zone = new DropZone();
        bool raised = false;
        zone.FilesDropped += (_, _) => raised = true;
        var broken = new BrokenData();

        var enter = Drag(DragDrop.DragEnterEvent, broken, zone);
        zone.RaiseEvent(enter);
        Assert.False(zone.IsDragOver);
        Assert.Equal(DragDropEffects.None, enter.Effects);
        Assert.False(zone.AcceptDrop(broken));
        Assert.False(raised);
    });

    [Fact]
    public void ToastHost_PausesWhileHovered() => Sta.RunLoop(async () =>
    {
        var host = new ToastHost { DismissAfter = TimeSpan.FromMilliseconds(250) };
        using var live = new LiveWindow(host, 420, 300);
        await LiveWindow.Until(() => host.IsLoaded);

        var toast = host.Show("Batch complete", "12 files compressed", ToastKind.Success);
        toast.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        await Task.Delay(700);
        Assert.Contains(toast, host.Items.OfType<Toast>()); // well past DismissAfter, still there while hovered

        toast.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        await LiveWindow.Until(() => host.Items.Count == 0, TimeSpan.FromSeconds(2));
    });

    [Fact]
    public void Charts_ObserveTheirCollectionsOnlyWhileLoaded() => Sta.RunLoop(async () =>
    {
        var values = new ObservableCollection<double> { 3, 5, 4 };
        var labels = new ObservableCollection<string> { "a", "b", "c" };
        var slices = new ObservableCollection<DonutSlice> { new("JXL", 2, Brushes.Transparent) };
        var bars = new BarChart { Values = values, Labels = labels };
        var donut = new DonutChart { Slices = slices };
        Assert.Equal(0, Handlers(values) + Handlers(labels) + Handlers(slices)); // not loaded: nothing attached

        var panel = new StackPanel { Children = { bars, donut } };
        var live = new LiveWindow(panel);
        await LiveWindow.Until(() => bars.IsLoaded && donut.IsLoaded);
        Assert.Equal((1, 1, 1), (Handlers(values), Handlers(labels), Handlers(slices)));
        Assert.Equal(2, bars.ObservedCollections);
        values.Add(9); // still reacts while shown

        // Swapping the data while loaded moves the subscription.
        var replacement = new ObservableCollection<double> { 1 };
        bars.Values = replacement;
        Assert.Equal((0, 1), (Handlers(values), Handlers(replacement)));

        // Unloaded: the view-model's collections no longer reference the charts (no leak of the page).
        panel.Children.Clear();
        await LiveWindow.Until(() => !bars.IsLoaded && !donut.IsLoaded);
        Assert.Equal(0, Handlers(replacement) + Handlers(labels) + Handlers(slices));

        // Shown again: observed again.
        panel.Children.Add(bars);
        await LiveWindow.Until(() => bars.IsLoaded);
        Assert.Equal((1, 1), (Handlers(replacement), Handlers(labels)));
        live.Dispose();
        await LiveWindow.Until(() => !bars.IsLoaded);
        Assert.Equal(0, Handlers(replacement) + Handlers(labels));
    });

    /// <summary>Raises KeyDown on the element that has keyboard focus.</summary>
    private static void Press(Key key) => RaiseKey((UIElement)Keyboard.FocusedElement, key, Keyboard.KeyDownEvent);

    /// <summary>Raises PreviewKeyDown (and KeyDown unless handled) on <paramref name="target"/>; returns whether it was handled.</summary>
    private static bool RaiseKey(UIElement target, Key key) => RaiseKey(target, key, Keyboard.PreviewKeyDownEvent);

    private static bool RaiseKey(UIElement target, Key key, RoutedEvent routedEvent)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = routedEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }

    /// <summary>Raises PreviewGotKeyboardFocus for a move to <paramref name="to"/>; returns whether it was refused.</summary>
    private static bool RaiseFocus(UIElement to, UIElement from)
    {
        var args = new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, from, to) { RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent };
        to.RaiseEvent(args);
        return args.Handled;
    }

    /// <summary>A drag event as the OLE drop target would raise it (DragEventArgs has no public constructor).</summary>
    private static DragEventArgs Drag(RoutedEvent routedEvent, IDataObject data, UIElement target)
    {
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single(c => c.GetParameters().Length == 5);
        var args = (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy | DragDropEffects.Move, target, new Point(10, 10)]);
        args.RoutedEvent = routedEvent;
        return args;
    }

    private static VisualStateGroup DragStates(DropZone zone)
    {
        var root = (FrameworkElement)VisualTreeHelper.GetChild(zone, 0);
        return VisualStateManager.GetVisualStateGroups(root).OfType<VisualStateGroup>().Single(g => g.Name == "DragStates");
    }

    /// <summary>Handlers attached to an ObservableCollection's CollectionChanged event.</summary>
    private static int Handlers<T>(ObservableCollection<T> collection) =>
        (typeof(ObservableCollection<T>).GetField("CollectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(collection) as Delegate)?.GetInvocationList().Length ?? 0;

    /// <summary>Virtual/COM data whose rendering fails, like some shell and browser drags.</summary>
    private sealed class BrokenData : IDataObject
    {
        public object GetData(string format) => throw new COMException("DV_E_FORMATETC", unchecked((int)0x80040064));

        public object GetData(Type format) => throw new COMException("DV_E_FORMATETC", unchecked((int)0x80040064));

        public object GetData(string format, bool autoConvert) => throw new COMException("DV_E_FORMATETC", unchecked((int)0x80040064));

        public bool GetDataPresent(string format) => throw new COMException("DV_E_FORMATETC", unchecked((int)0x80040064));

        public bool GetDataPresent(Type format) => throw new COMException("DV_E_FORMATETC", unchecked((int)0x80040064));

        public bool GetDataPresent(string format, bool autoConvert) => throw new COMException("DV_E_FORMATETC", unchecked((int)0x80040064));

        public string[] GetFormats() => [];

        public string[] GetFormats(bool autoConvert) => [];

        public void SetData(object data) => throw new NotSupportedException();

        public void SetData(string format, object data) => throw new NotSupportedException();

        public void SetData(Type format, object data) => throw new NotSupportedException();

        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
    }
}
