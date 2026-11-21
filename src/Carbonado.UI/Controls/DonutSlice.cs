using System.Windows.Media;

namespace Carbonado.UI.Controls;

/// <summary>One segment of a <see cref="DonutChart"/>; non-positive values take no space.</summary>
public sealed record DonutSlice(string Label, double Value, Brush Brush);
