using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ChromeProps = Carbonado.UI.Theme.Chrome;
using IconPresenter = Carbonado.UI.Controls.IconPresenter;

namespace Carbonado.UI.Tests.Theme;

/// <summary>
/// A themed sample: the element to stage and the control that receives the state, the render size, the states that
/// apply, whether its chrome must paint a background, and whether it is a root-only control (ToolTip, ContextMenu).
/// </summary>
internal sealed record Specimen(
    Func<(FrameworkElement Root, Control? Target)> Create,
    int Width,
    int Height,
    string[] States,
    bool Opaque = true,
    bool IsRoot = false);

/// <summary>Every implicit and keyed style of Theme.xaml with sample content, rendered in each applicable state.</summary>
internal static class ThemeSpecimens
{
    public const string Normal = "Normal";
    public const string Hover = "Hover";
    public const string Pressed = "Pressed";
    public const string Disabled = "Disabled";
    public const string Checked = "Checked";
    public const string Focused = "Focused";
    public const string Indeterminate = "Indeterminate";

    private static readonly string[] ButtonStates = [Normal, Hover, Pressed, Disabled, Focused];
    private static readonly string[] ToggleStates = [Normal, Hover, Pressed, Disabled, Checked, Focused];
    private static readonly string[] InputStates = [Normal, Hover, Disabled, Focused];
    private static readonly string[] ItemStates = [Normal, Hover, Disabled, Checked, Focused];
    private static readonly string[] Still = [Normal];

    public static readonly IReadOnlyDictionary<string, Specimen> All = new Dictionary<string, Specimen>
    {
        ["Button"] = new(() => Self(new Button { Content = "Compress" }), 200, 72, ButtonStates),
        ["Button.Primary"] = new(() => Self(Styled(new Button { Content = "Start forging" }, "Button.Primary", "Icon.Play")), 220, 80, ButtonStates),
        ["Button.Secondary"] = new(() => Self(Styled(new Button { Content = "Add files" }, "Button.Secondary", "Icon.AddFile")), 200, 72, ButtonStates),
        ["Button.Ghost"] = new(() => Self(Styled(new Button { Content = "Show in folder" }, "Button.Ghost", "Icon.FolderOpen")), 220, 72, ButtonStates, Opaque: false),
        ["Button.Icon"] = new(() => Self(Styled(new Button(), "Button.Icon", contentIcon: "Icon.Settings")), 72, 72, ButtonStates, Opaque: false),
        ["Button.Danger"] = new(() => Self(Styled(new Button { Content = "Remove all" }, "Button.Danger", "Icon.Remove")), 200, 72, ButtonStates),
        ["RepeatButton"] = new(() => Self(new RepeatButton { Content = "Nudge" }), 160, 72, ButtonStates),
        ["ToggleButton"] = new(() => Self(new ToggleButton { Content = "Lossless" }), 180, 72, ToggleStates),
        ["Toggle.Switch"] = new(() => Self(Styled(new ToggleButton { Content = "Reduced motion" }, "Toggle.Switch")), 220, 56, ToggleStates),
        ["CheckBox"] = new(() => Self(new CheckBox { Content = "Keep color profile" }), 220, 56, ToggleStates),
        ["RadioButton"] = new(() => Self(new RadioButton { Content = "Same folder" }), 200, 56, ToggleStates),
        ["Radio.Chip"] = new(() => Self(Styled(new RadioButton { Content = "VS · Excellent" }, "Radio.Chip")), 200, 64, ToggleStates),
        ["TextBox"] = new(() => Self(TextInput()), 300, 72, InputStates),
        ["PasswordBox"] = new(() => Self(new PasswordBox { Width = 260, Password = "carbonado" }), 300, 72, InputStates),
        ["ComboBox"] = new(() => Self(Combo()), 300, 72, InputStates),
        ["ComboBoxItem"] = new(Dropdown, 300, 190, [Normal, Hover, Disabled, Checked], Opaque: false),
        ["Slider"] = new(() => Self(new Slider { Width = 260, Minimum = 30, Maximum = 99, Value = 85 }), 300, 64, [Normal, Hover, Disabled, Focused], Opaque: false),
        ["ProgressBar"] = new(() => Self(new ProgressBar { Width = 260, Value = 64 }), 300, 40, [Normal, Disabled, Indeterminate]),
        ["ScrollBar"] = new(() => Self(new ScrollBar { Height = 160, Maximum = 100, ViewportSize = 40, Value = 30 }), 60, 190, [Normal, Hover, Disabled], Opaque: false),
        ["ScrollViewer"] = new(Scroller, 280, 180, Still, Opaque: false),
        ["ListBox"] = new(ListSample, 300, 200, ItemStates, Opaque: false),
        ["ListView"] = new(GridSample, 380, 190, ItemStates, Opaque: false),
        ["ToolTip"] = new(() => (Tip(), null), 260, 72, Still, IsRoot: true),
        ["MenuItem"] = new(MenuSample, 300, 260, [Normal, Hover, Disabled, Checked], Opaque: false, IsRoot: true),
        ["Separator"] = new(Divider, 260, 90, Still, Opaque: false),
        ["Expander"] = new(() => Self(ExpanderSample()), 340, 170, [Normal, Hover, Disabled, Checked]),
        ["GroupBox"] = new(() => Self(GroupSample()), 340, 160, [Normal, Disabled]),
        ["Border.Card"] = new(() => (Surface("Border.Card", "Card"), null), 340, 160, Still),
        ["Border.Glass"] = new(() => (Surface("Border.Glass", "Glass"), null), 340, 140, Still),
        ["Typography"] = new(() => (TypeSpecimen(), null), 640, 330, Still),
    };

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, specimen) in All)
        {
            foreach (string state in specimen.States)
            {
                data.Add(name, state);
            }
        }

        return data;
    }

    private static (FrameworkElement, Control?) Self(Control control) => (control, control);

    private static Control Styled(Control control, string style, string? icon = null, string? contentIcon = null)
    {
        control.Style = (Style)ThemeHarness.Theme[style];
        if (icon is not null)
        {
            ChromeProps.SetIcon(control, (Geometry)ThemeHarness.Theme[icon]);
        }

        if (contentIcon is not null && control is ContentControl content)
        {
            content.Content = ThemeHarness.Theme[contentIcon];
        }

        return control;
    }

    private static TextBox TextInput()
    {
        var box = new TextBox { Width = 260 };
        ChromeProps.SetPlaceholder(box, "Search the queue");
        ChromeProps.SetIcon(box, (Geometry)ThemeHarness.Theme["Icon.Search"]);
        return box;
    }

    private static ComboBox Combo()
    {
        var combo = new ComboBox { Width = 260 };
        foreach (string format in new[] { "Auto (smallest)", "JPEG XL", "AVIF", "WebP" })
        {
            combo.Items.Add(format);
        }

        combo.SelectedIndex = 0;
        return combo;
    }

    private static (FrameworkElement, Control?) Dropdown()
    {
        var items = new StackPanel();
        ComboBoxItem? target = null;
        foreach (string format in new[] { "Auto (smallest)", "JPEG XL", "AVIF", "WebP" })
        {
            var item = new ComboBoxItem { Content = format };
            target ??= format == "JPEG XL" ? item : null;
            items.Children.Add(item);
        }

        var card = new Border { Child = items, Width = 240, Style = (Style)ThemeHarness.Theme["Border.Popup"] };
        return (card, target);
    }

    private static (FrameworkElement, Control?) Scroller()
    {
        var text = new TextBlock
        {
            Text = string.Join("\n", Enumerable.Range(1, 24).Select(i => $"IMG_{2300 + i}.jpg  →  JPEG XL  −{40 + (i * 7 % 50)} %")),
            Width = 420,
            Margin = new Thickness(12),
        };
        text.SetResourceReference(TextBlock.StyleProperty, "Text.Mono");
        var scroller = new ScrollViewer
        {
            Content = text,
            Width = 240,
            Height = 150,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return (scroller, null);
    }

    private static (FrameworkElement, Control?) ListSample()
    {
        var list = new ListBox { Width = 260 };
        ListBoxItem? target = null;
        foreach (string name in new[] { "aurora.png", "carbon-macro.jpg", "facets.webp", "obsidian.tiff" })
        {
            var item = new ListBoxItem { Content = name };
            target ??= name == "carbon-macro.jpg" ? item : null;
            list.Items.Add(item);
        }

        return (list, target);
    }

    private static (FrameworkElement, Control?) GridSample()
    {
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "FILE", Width = 150, DisplayMemberBinding = new Binding("[0]") });
        view.Columns.Add(new GridViewColumn { Header = "FORMAT", Width = 90, DisplayMemberBinding = new Binding("[1]") });
        view.Columns.Add(new GridViewColumn { Header = "SAVED", Width = 90, DisplayMemberBinding = new Binding("[2]") });
        var list = new ListView { View = view, Width = 350 };
        ListViewItem? target = null;
        foreach (var row in new[] { new[] { "aurora.png", "JXL", "−71 %" }, ["carbon-macro.jpg", "JXL", "−31 %"], ["facets.webp", "AVIF", "−58 %"] })
        {
            var item = new ListViewItem { Content = row };
            target ??= row[0] == "carbon-macro.jpg" ? item : null;
            list.Items.Add(item);
        }

        return (list, target);
    }

    private static ToolTip Tip() => new() { Content = "Compare before / after  (F11)" };

    private static (FrameworkElement, Control?) MenuSample()
    {
        var menu = new ContextMenu { IsOpen = false };
        menu.Items.Add(new MenuItem { Header = "Open", InputGestureText = "Enter", Icon = Icon("Icon.Open") });
        var target = new MenuItem { Header = "Show in folder", InputGestureText = "Ctrl+E", Icon = Icon("Icon.FolderOpen"), IsCheckable = true };
        menu.Items.Add(target);
        menu.Items.Add(new MenuItem { Header = "Compare", InputGestureText = "F11", Icon = Icon("Icon.Compare") });
        menu.Items.Add(new Separator());
        var rerun = new MenuItem { Header = "Re-run with profile", Icon = Icon("Icon.Rerun") };
        rerun.Items.Add(new MenuItem { Header = "VS · Excellent" });
        menu.Items.Add(rerun);
        menu.Items.Add(new MenuItem { Header = "Remove", InputGestureText = "Del", Icon = Icon("Icon.Remove") });
        return (menu, target);
    }

    private static IconPresenter Icon(string key) => new() { Icon = (Geometry)ThemeHarness.Theme[key], Width = 16, Height = 16 };

    private static (FrameworkElement, Control?) Divider()
    {
        var stack = new StackPanel { Width = 220 };
        stack.Children.Add(Caption("Above the line"));
        stack.Children.Add(new Separator());
        stack.Children.Add(Caption("Below the line"));
        return (stack, null);
    }

    private static Expander ExpanderSample() => new()
    {
        Header = "Advanced options",
        Width = 300,
        Content = Caption("Effort, metadata policy and resize limits live here."),
    };

    private static GroupBox GroupSample() => new()
    {
        Header = "OUTPUT",
        Width = 300,
        Content = Caption("Subfolder \"Carbonado\" next to each source."),
    };

    private static Border Surface(string style, string title)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, Style = (Style)ThemeHarness.Theme["Text.Subtitle"] });
        stack.Children.Add(new TextBlock { Text = "Hairline edges catch the light from above.", Style = (Style)ThemeHarness.Theme["Text.Body"], Margin = new Thickness(0, 6, 0, 0) });
        return new Border { Child = stack, Width = 300, Style = (Style)ThemeHarness.Theme[style] };
    }

    private static StackPanel TypeSpecimen()
    {
        var stack = new StackPanel { Width = 600 };
        foreach (var (style, text) in new[]
                 {
                     ("Text.Hero", "−91 %"),
                     ("Text.Display", "Carbonado"),
                     ("Text.Title", "Infinite pressure. Flawless brilliance."),
                     ("Text.Subtitle", "Quality grade VS · excellent (85)"),
                     ("Text.Body", "Every lossy output is measured with SSIMULACRA2 and verified before it is written."),
                     ("Text.Caption", "1 284 FILES · 3.2 GB SAVED"),
                     ("Text.Mono", @"C:\Photos\2026\aurora.png → aurora.jxl"),
                 })
        {
            stack.Children.Add(new TextBlock { Text = text, Style = (Style)ThemeHarness.Theme[style], Margin = new Thickness(0, 0, 0, 6) });
        }

        return stack;
    }

    private static TextBlock Caption(string text) => new() { Text = text, Style = (Style)ThemeHarness.Theme["Text.Body"], Margin = new Thickness(0, 4, 0, 4) };
}
