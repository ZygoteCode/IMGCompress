using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Carbonado.UI.Controls;

/// <summary>Drawing of the compare view: checkerboard, images per pane, divider and label chips.</summary>
public sealed partial class BeforeAfterViewer
{
    private const double HeatmapBaseOpacity = 0.35;

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Background is { } background)
        {
            drawingContext.DrawRectangle(background, null, new Rect(RenderSize));
        }

        if (ImageSize is not { } image)
        {
            return;
        }

        var pane = PaneSize;
        var imageRect = new Rect(Pan.X, Pan.Y, image.Width * Scale, image.Height * Scale);
        switch (Mode)
        {
            case CompareMode.Split:
                double divider = Math.Round(Split * ActualWidth);
                DrawPane(drawingContext, new Rect(0, 0, divider, pane.Height), default, imageRect, Before, BeforeLabel, true);
                DrawPane(drawingContext, new Rect(divider, 0, ActualWidth - divider, pane.Height), default, imageRect, After, AfterLabel, false);
                DrawDivider(drawingContext, divider);
                break;
            case CompareMode.SideBySide:
                var right = new Vector(pane.Width + 1, 0);
                DrawPane(drawingContext, new Rect(pane), default, imageRect, Before, BeforeLabel, true);
                DrawPane(drawingContext, new Rect(new Point(right.X, 0), pane), right, imageRect, After, AfterLabel, false);
                if (DividerBrush is { } gap)
                {
                    drawingContext.DrawRectangle(gap, null, new Rect(pane.Width, 0, 1, pane.Height));
                }

                break;
            case CompareMode.Flicker:
                DrawPane(drawingContext, new Rect(pane), default, imageRect,
                    IsShowingBefore ? Before : After, IsShowingBefore ? BeforeLabel : AfterLabel, true);
                break;
            case CompareMode.Heatmap:
                var clip = new Rect(pane);
                drawingContext.PushClip(new RectangleGeometry(clip));
                if (After is { } after)
                {
                    drawingContext.PushOpacity(HeatmapBaseOpacity);
                    drawingContext.DrawImage(after, imageRect);
                    drawingContext.Pop();
                }

                if (Heatmap is { } heat)
                {
                    drawingContext.DrawImage(heat, imageRect);
                }

                drawingContext.Pop();
                break;
        }
    }

    /// <summary>Draws one image clipped to <paramref name="area"/>; the image rect is shifted by the pane origin.</summary>
    private void DrawPane(DrawingContext dc, Rect area, Vector origin, Rect imageRect, ImageSource? source, string? label, bool labelLeft)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(area));
        var shifted = Rect.Offset(imageRect, origin);
        DrawBackdrop(dc, shifted);
        if (source is not null)
        {
            dc.DrawImage(source, shifted);
        }

        DrawLabel(dc, area, label, labelLeft);
        dc.Pop();
    }

    private void DrawBackdrop(DrawingContext dc, Rect imageRect)
    {
        if (CheckerBrush is { } checker)
        {
            dc.DrawRectangle(checker, null, imageRect);
        }
    }

    private void DrawDivider(DrawingContext dc, double x)
    {
        if (DividerBrush is not { } line)
        {
            return;
        }

        var pen = new Pen(line, 1.5);
        pen.Freeze();
        dc.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight));
        var knob = new Point(x, ActualHeight / 2);
        dc.DrawEllipse(KnobBrush, pen, knob, 15, 15);

        // Two facing chevrons: "drag me sideways".
        var chevrons = new StreamGeometry();
        using (var ctx = chevrons.Open())
        {
            ctx.BeginFigure(new Point(knob.X - 3, knob.Y - 4.5), false, false);
            ctx.LineTo(new Point(knob.X - 7.5, knob.Y), true, true);
            ctx.LineTo(new Point(knob.X - 3, knob.Y + 4.5), true, true);
            ctx.BeginFigure(new Point(knob.X + 3, knob.Y - 4.5), false, false);
            ctx.LineTo(new Point(knob.X + 7.5, knob.Y), true, true);
            ctx.LineTo(new Point(knob.X + 3, knob.Y + 4.5), true, true);
        }

        chevrons.Freeze();
        var arrow = new Pen(line, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        arrow.Freeze();
        dc.DrawGeometry(null, arrow, chevrons);
    }

    /// <summary>Small caption chip in the top corner of a pane.</summary>
    private void DrawLabel(DrawingContext dc, Rect area, string? label, bool left)
    {
        if (string.IsNullOrEmpty(label) || Foreground is not { } text)
        {
            return;
        }

        var formatted = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            11, text, null, TextFormattingMode.Display, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var size = new Size(formatted.WidthIncludingTrailingWhitespace + 16, formatted.Height + 8);
        double x = left ? area.Left + 12 : area.Right - 12 - size.Width;
        var chip = new Rect(new Point(x, area.Top + 12), size);
        if (LabelBackground is { } chipBrush)
        {
            dc.DrawRoundedRectangle(chipBrush, null, chip, chip.Height / 2, chip.Height / 2);
        }

        dc.DrawText(formatted, new Point(chip.X + 8, chip.Y + 4));
    }
}
