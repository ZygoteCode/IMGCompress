namespace Carbonado.UI.Controls;

/// <summary>How <see cref="BeforeAfterViewer"/> presents the two images.</summary>
public enum CompareMode
{
    /// <summary>One view; a draggable divider shows Before on the left and After on the right.</summary>
    Split,

    /// <summary>Two panes side by side with synchronized zoom and pan.</summary>
    SideBySide,

    /// <summary>Alternates Before and After in place (A/B blink).</summary>
    Flicker,

    /// <summary>The difference heat-map over a dimmed After.</summary>
    Heatmap,
}
