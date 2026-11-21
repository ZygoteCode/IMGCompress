using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Carbonado.UI.Brand;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// CARBONADO cutting in under the diamond: each faceted letter (its own visual, drawn once) arrives blurred, wide
/// tracked (0.6 em → 0.18 em) and low, then snaps sharp; a sheen sweeps the word; the tagline types in letter by
/// letter with a caret. With reduced motion everything only fades.
/// </summary>
internal sealed class WordmarkReveal : IntroLayer
{
    private const double StartTracking = 0.6;
    private const double TaglineTracking = 0.32;

    private readonly DrawingVisual[] _letters;
    private readonly BlurEffect?[] _blurs;
    private readonly DrawingVisual _sheen;
    private readonly DrawingVisual _tagline;
    private readonly Geometry _word = IntroAssets.Word;
    private TaglineCell[] _cells = [];
    private (string? Text, double Size, double PixelsPerDip) _cellsKey;
    private (int Typed, double Head, bool Caret, double Left, double Top) _drawn = (-1, 0, false, 0, 0);

    public WordmarkReveal()
    {
        BrandPalette p = BrandPalette.Current;

        // Faceted fill: a hard diagonal split between a lit and a shaded bevel, like a cut stone.
        var facet = new LinearGradientBrush(
            [
                new GradientStop(p.Diamond50, 0),
                new GradientStop(p.Diamond50, 0.5),
                new GradientStop(BrandPalette.Mix(p.Diamond50, p.Mist300, 0.55), 0.5),
                new GradientStop(BrandPalette.Mix(p.Mist300, p.Iris, 0.15), 1),
            ],
            new Point(0.15, 0),
            new Point(0.85, 1));
        facet.Freeze();

        _letters = new DrawingVisual[IntroState.LetterCount];
        for (int i = 0; i < _letters.Length; i++)
        {
            var letter = new DrawingVisual();
            using (DrawingContext dc = letter.RenderOpen())
            {
                dc.DrawGeometry(facet, null, WordmarkGeometry.Letters[i]);
            }

            letter.Opacity = 0;
            _letters[i] = Add(letter);
        }

        _blurs = new BlurEffect?[IntroState.LetterCount];

        _sheen = Add(new DrawingVisual());
        _tagline = Add(new DrawingVisual());
    }

    /// <summary>Localized tagline (typed under the wordmark); null or empty hides it.</summary>
    public string? Tagline { get; set; }

    /// <summary>Lays out the tagline glyphs for <paramref name="layout"/> ahead of time (font loading included).</summary>
    public void Prepare(in IntroLayout layout)
    {
        if (!layout.IsEmpty)
        {
            EnsureCells(layout);
        }
    }

    public override void Render(IntroState state, in IntroLayout layout)
    {
        if (layout.IsEmpty)
        {
            return;
        }

        double em = IntroLayout.WordmarkCapHeight * layout.Scale;
        double k = em / WordmarkGeometry.CapHeight;
        double left = layout.Core.X - (WordmarkGeometry.Width() * k / 2);
        double top = layout.Core.Y + (IntroLayout.WordmarkTop * layout.Scale);
        double exit = state.TextExit;

        for (int i = 0; i < _letters.Length; i++)
        {
            double p = state.Letters[i];
            double opacity, spread, rise, blur;
            if (state.FadeOnly)
            {
                (opacity, spread, rise, blur) = (p, 0, 0, 0);
            }
            else
            {
                opacity = Clamp01(p * 2.4);
                spread = (StartTracking - WordmarkGeometry.RestTracking) * em * (i - ((_letters.Length - 1) / 2.0)) * (1 - p);
                rise = 0.34 * em * (1 - p);
                blur = 14 * layout.Scale * Math.Pow(Clamp01(1 - p), 1.5);
                rise -= 8 * layout.Scale * exit;
                blur += 3 * layout.Scale * exit;
            }

            DrawingVisual letter = _letters[i];
            letter.Opacity = opacity * (1 - exit);
            var placement = new MatrixTransform(k, 0, 0, k, left + (WordmarkGeometry.Offset(i) * k) + spread, top + rise);
            placement.Freeze();
            letter.Transform = placement;
            if (UseEffects && blur > 0.35 && letter.Opacity > 0.002)
            {
                BlurEffect effect = _blurs[i] ??= new BlurEffect { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
                effect.Radius = blur;
                letter.Effect = effect;
            }
            else
            {
                letter.Effect = null;
            }
        }

        DrawSheen(state, k, left, top);
        DrawTagline(state, layout, top);
    }

    private void DrawSheen(IntroState state, double k, double left, double top)
    {
        using DrawingContext dc = _sheen.RenderOpen();
        double s = state.Sheen;
        if (state.FadeOnly || s <= 0 || s >= 1 || state.TextExit >= 1)
        {
            return;
        }

        BrandPalette p = BrandPalette.Current;
        double band = -0.25 + (1.5 * s);
        var light = new LinearGradientBrush(
            [
                new GradientStop(p.Transparent, band - 0.14),
                new GradientStop(BrandPalette.WithAlpha(p.Ice, 0.35), band - 0.04),
                new GradientStop(BrandPalette.WithAlpha(p.Diamond50, 0.95), band),
                new GradientStop(BrandPalette.WithAlpha(p.Ice, 0.35), band + 0.04),
                new GradientStop(p.Transparent, band + 0.14),
            ],
            new Point(0, 0.2),
            new Point(1, 0.8));
        light.Freeze();
        dc.PushTransform(new MatrixTransform(k, 0, 0, k, left, top));
        dc.PushOpacity(1 - state.TextExit);
        dc.DrawGeometry(light, null, _word);
        dc.Pop();
        dc.Pop();
    }

    private void DrawTagline(IntroState state, in IntroLayout layout, double wordTop)
    {
        double size = EnsureCells(layout);
        _tagline.Opacity = (1 - state.TextExit) * (state.FadeOnly ? state.Tagline : 1);
        if (_cells.Length == 0)
        {
            return;
        }

        double width = _cells[^1].X + _cells[^1].Text.WidthIncludingTrailingWhitespace;
        double left = layout.Core.X - (width / 2);
        double top = wordTop + ((IntroLayout.TaglineTop - IntroLayout.WordmarkTop) * layout.Scale);

        // Typing: whole glyphs up to the head, the head glyph fading in; a caret blinks at the head while typing.
        double typed = state.FadeOnly ? _cells.Length : state.Tagline * _cells.Length;
        int whole = (int)Math.Floor(typed);
        double head = Math.Round(typed - whole, 2);
        bool caret = !state.FadeOnly && state.Tagline > 0 && state.Tagline < 1;
        var key = (whole, head, caret, Math.Round(left, 2), Math.Round(top, 2));
        if (key == _drawn)
        {
            return;
        }

        _drawn = key;
        using DrawingContext dc = _tagline.RenderOpen();
        if (typed <= 0)
        {
            return;
        }

        for (int i = 0; i < Math.Min(whole, _cells.Length); i++)
        {
            dc.DrawText(_cells[i].Text, new Point(left + _cells[i].X, top));
        }

        if (whole < _cells.Length && head > 0)
        {
            dc.PushOpacity(head);
            dc.DrawText(_cells[whole].Text, new Point(left + _cells[whole].X, top));
            dc.Pop();
        }

        if (caret)
        {
            BrandPalette p = BrandPalette.Current;
            double x = left + (whole < _cells.Length ? _cells[whole].X : width) + (size * 0.1);
            var caretBrush = new SolidColorBrush(BrandPalette.WithAlpha(p.Aqua, 0.9));
            caretBrush.Freeze();
            dc.DrawRectangle(caretBrush, null, new Rect(x, top + (size * 0.1), Math.Max(1, size * 0.09), size * 1.05));
        }
    }

    /// <summary>(Re)builds the glyph cells when the text, size or DPI changed; returns the font size.</summary>
    private double EnsureCells(in IntroLayout layout)
    {
        string? text = Tagline;
        double size = IntroLayout.TaglineSize * layout.Scale;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_cellsKey != (text, size, pixelsPerDip))
        {
            _cellsKey = (text, size, pixelsPerDip);
            _cells = BuildCells(text, size, pixelsPerDip);
            _drawn = (-1, 0, false, 0, 0);
            _tagline.RenderOpen().Close();
        }

        return size;
    }

    private static TaglineCell[] BuildCells(string? text, double size, double pixelsPerDip)
    {
        if (string.IsNullOrWhiteSpace(text) || size <= 0)
        {
            return [];
        }

        BrandPalette p = BrandPalette.Current;
        var typeface = new Typeface(p.DisplayFont, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        var brush = new SolidColorBrush(p.Mist300);
        brush.Freeze();
        string upper = text.ToUpper(CultureInfo.CurrentCulture);
        var cells = new TaglineCell[upper.Length];
        double x = 0;
        for (int i = 0; i < upper.Length; i++)
        {
            var glyph = new FormattedText(upper[i].ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);
            cells[i] = new TaglineCell(glyph, x);
            x += glyph.WidthIncludingTrailingWhitespace + (TaglineTracking * size);
        }

        return cells;
    }

    private readonly record struct TaglineCell(FormattedText Text, double X);
}
