using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using Carbonado.UI.Transitions;

namespace Carbonado.UI.Controls;

/// <summary>
/// Animated metric: when <see cref="Value"/> changes the number counts to it like an odometer — each digit rolls
/// vertically in a fixed-width (tabular) cell, the lowest continuously, higher ones on carry. Right-aligned so the
/// units stay put; the width changes only at the start and end of a count, never per frame. Culture-aware
/// separators (<see cref="FrameworkElement.Language"/>); reduced motion or hidden ⇒ the value is set at once.
/// </summary>
public sealed class CountUpText : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CountUpText),
        new FrameworkPropertyMetadata(0.0, OnValueChanged), v => v is double d && double.IsFinite(d));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(CountFormat), typeof(CountUpText),
        new FrameworkPropertyMetadata(CountFormat.Integer, OnLookChanged));

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(Duration), typeof(CountUpText),
        new FrameworkPropertyMetadata(new Duration(TimeSpan.FromMilliseconds(900))));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(CountUpText), new FrameworkPropertyMetadata(TextElement.ForegroundProperty.DefaultMetadata.DefaultValue,
            FrameworkPropertyMetadataOptions.Inherits, OnLookChanged));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(CountUpText), new FrameworkPropertyMetadata(TextElement.FontSizeProperty.DefaultMetadata.DefaultValue,
            FrameworkPropertyMetadataOptions.Inherits, OnLookChanged));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(CountUpText), new FrameworkPropertyMetadata(TextElement.FontFamilyProperty.DefaultMetadata.DefaultValue,
            FrameworkPropertyMetadataOptions.Inherits, OnLookChanged));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
        typeof(CountUpText), new FrameworkPropertyMetadata(TextElement.FontWeightProperty.DefaultMetadata.DefaultValue,
            FrameworkPropertyMetadataOptions.Inherits, OnLookChanged));

    private static readonly DependencyPropertyKey DisplayValuePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayValue), typeof(double), typeof(CountUpText), new PropertyMetadata(0.0));

    /// <summary>Value currently shown (moves toward <see cref="Value"/> during a count).</summary>
    public static readonly DependencyProperty DisplayValueProperty = DisplayValuePropertyKey.DependencyProperty;

    private readonly Dictionary<char, FormattedText> _glyphs = [];
    private readonly FrameTween _tween;
    private double _from;
    private double _to;
    private double _reference;
    private double _digitWidth;
    private double _lineHeight;
    private bool _frozenFrame;
    private Brush? _drum;

    static CountUpText()
    {
        LanguageProperty.OverrideMetadata(typeof(CountUpText), new FrameworkPropertyMetadata(OnLookChanged));
    }

    public CountUpText()
    {
        _tween = new FrameTween(OnFrame);
        Unloaded += (_, _) => _tween.Complete();
    }

    /// <summary>Target value; changing it starts a count from the value currently shown.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>How the value is written (integer, bytes, percent, score).</summary>
    public CountFormat Format
    {
        get => (CountFormat)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    /// <summary>Length of a count (default 0.9 s, scaled by MotionSettings).</summary>
    public Duration Duration
    {
        get => (Duration)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <summary>Text brush (inherited like TextBlock.Foreground).</summary>
    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Text size (inherited).</summary>
    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>Typeface family (inherited; the theme uses Inter Display).</summary>
    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>Typeface weight (inherited).</summary>
    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public double DisplayValue
    {
        get => (double)GetValue(DisplayValueProperty);
        private set => SetValue(DisplayValuePropertyKey, value);
    }

    /// <summary>Characters currently shown.</summary>
    public string Text => Odometer.Text(Reading(DisplayValue));

    /// <summary>The settled text of <see cref="Value"/> (what the count ends on).</summary>
    public string TargetText => Odometer.Format(Value, Format, Numbers);

    /// <summary>True while digits are rolling (a count is playing or a frame is frozen for inspection).</summary>
    private bool IsCounting => _tween.IsRunning || _frozenFrame;

    private NumberFormatInfo Numbers =>
        (Language?.GetSpecificCulture() ?? CultureInfo.CurrentCulture).NumberFormat;

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureGlyphs();
        double width = MeasureReading(Settled());
        if (IsCounting)
        {
            width = Math.Max(width, Math.Max(MeasureReading(Rolling(_from)), MeasureReading(Rolling(_to))));
        }

        return new Size(width, _lineHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        EnsureGlyphs();
        var reading = Reading(DisplayValue);
        double x = ActualWidth - MeasureReading(reading);
        foreach (var glyph in reading)
        {
            if (!glyph.IsDigit)
            {
                var text = Glyph(glyph.Char);
                drawingContext.DrawText(text, new Point(x, 0));
                x += text.WidthIncludingTrailingWhitespace;
                continue;
            }

            DrawDigit(drawingContext, glyph, x);
            x += _digitWidth;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CountUpTextAutomationPeer(this);

    private void DrawDigit(DrawingContext dc, OdometerGlyph glyph, double x)
    {
        var current = Glyph(glyph.Char);
        double inset = (_digitWidth - current.WidthIncludingTrailingWhitespace) / 2;
        if (glyph.Roll < 0.001)
        {
            dc.DrawText(current, new Point(x + inset, 0));
            return;
        }

        // Drum: the digit slides up out of its cell while the next one rises in; both fade toward the cell edges.
        var next = Glyph((char)('0' + ((glyph.Digit + 1) % 10)));
        double offset = glyph.Roll * _lineHeight;
        dc.PushClip(new RectangleGeometry(new Rect(x, 0, _digitWidth, _lineHeight)));
        dc.PushOpacityMask(_drum ??= CreateDrum(_lineHeight));
        dc.DrawText(current, new Point(x + inset, -offset));
        dc.DrawText(next, new Point(x + ((_digitWidth - next.WidthIncludingTrailingWhitespace) / 2), _lineHeight - offset));
        dc.Pop();
        dc.Pop();
    }

    /// <summary>What is on screen: rolling digits during a count, the rounded value at rest.</summary>
    private IReadOnlyList<OdometerGlyph> Reading(double display) => IsCounting ? Rolling(display) : Settled();

    /// <summary>Vertical alpha ramp (opaque in the middle of the line, clear at its edges); alpha only, no color.</summary>
    private static Brush CreateDrum(double height)
    {
        var drum = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, height),
            GradientStops =
            {
                new GradientStop(new Color { A = 0 }, 0),
                new GradientStop(new Color { A = 255 }, 0.3),
                new GradientStop(new Color { A = 255 }, 0.7),
                new GradientStop(new Color { A = 0 }, 1),
            },
        };
        drum.Freeze();
        return drum;
    }

    private IReadOnlyList<OdometerGlyph> Rolling(double display) => Odometer.Read(display, Format, _reference, Numbers);

    private IReadOnlyList<OdometerGlyph> Settled() => Odometer.Read(Value, Format, Value, Numbers, rolling: false);

    private double MeasureReading(IReadOnlyList<OdometerGlyph> reading)
    {
        double width = 0;
        foreach (var glyph in reading)
        {
            width += glyph.IsDigit ? _digitWidth : Glyph(glyph.Char).WidthIncludingTrailingWhitespace;
        }

        return width;
    }

    private void EnsureGlyphs()
    {
        if (_glyphs.Count > 0)
        {
            return;
        }

        _digitWidth = 0;
        _lineHeight = 0;
        for (char c = '0'; c <= '9'; c++)
        {
            var digit = Glyph(c);
            _digitWidth = Math.Max(_digitWidth, digit.WidthIncludingTrailingWhitespace);
            _lineHeight = Math.Max(_lineHeight, digit.Height);
        }
    }

    private FormattedText Glyph(char c)
    {
        if (!_glyphs.TryGetValue(c, out var text))
        {
            var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
            text = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize,
                Foreground, null, TextFormattingMode.Ideal, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _glyphs[c] = text;
        }

        return text;
    }

    /// <summary>
    /// Freezes the frame at <paramref name="progress"/> (0..1, before easing) of a count from
    /// <paramref name="from"/> to <see cref="Value"/>: deterministic rolling digits for snapshots.
    /// </summary>
    internal void FreezeFrame(double from, double progress)
    {
        _tween.Stop();
        _frozenFrame = true;
        _from = from;
        _reference = Math.Max(Math.Abs(from), Math.Abs(Value));
        _to = Odometer.Quantize(Value, Format, _reference, Numbers);
        DisplayValue = _from + ((_to - _from) * Eases.CrystallizeCurve.Evaluate(progress));
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnFrame(double progress)
    {
        DisplayValue = _from + ((_to - _from) * progress);
        if (progress >= 1)
        {
            // At rest the text is the target's own rounded layout (a count may have used a larger unit).
            DisplayValue = Value;
            _from = Value;
            InvalidateMeasure();
        }

        InvalidateVisual();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var counter = (CountUpText)d;
        counter._frozenFrame = false;
        double target = (double)e.NewValue;
        counter._from = counter.DisplayValue;
        counter._reference = Math.Max(Math.Abs(counter._from), Math.Abs(target));
        counter._to = Odometer.Quantize(target, counter.Format, counter._reference, counter.Numbers);
        counter.InvalidateMeasure();
        var duration = counter.Duration.HasTimeSpan ? counter.Duration.TimeSpan : TimeSpan.Zero;
        if (counter.IsLoaded && counter.IsVisible)
        {
            counter._tween.Start(duration, Eases.CrystallizeCurve);
        }
        else
        {
            counter._tween.Complete();
        }
    }

    private static void OnLookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var counter = (CountUpText)d;
        counter._glyphs.Clear();
        counter._drum = null;
        counter.InvalidateMeasure();
        counter.InvalidateVisual();
    }

    /// <summary>Screen readers get the settled value, not the rolling digits.</summary>
    private sealed class CountUpTextAutomationPeer(CountUpText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(CountUpText);

        protected override string GetNameCore()
        {
            string name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((CountUpText)Owner).TargetText : name;
        }
    }
}
