using System.Windows;
using System.Windows.Media;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// Size-relative staging of the intro. The composition is authored in a 1280×800 design space centered on the
/// core (the diamond's center) and uniformly scaled to fit the overlay, so every window size shows the same shot;
/// layers draw in design units through <see cref="DesignTransform"/>.
/// </summary>
internal readonly struct IntroLayout
{
    public const double DesignWidth = 1280;
    public const double DesignHeight = 800;

    /// <summary>Side of the diamond's 3D viewport, design units.</summary>
    public const double DiamondBox = 340;

    /// <summary>Wordmark cap height, design units.</summary>
    public const double WordmarkCapHeight = 46;

    /// <summary>Top of the wordmark below the core, design units.</summary>
    public const double WordmarkTop = 172;

    /// <summary>Tagline font size and top below the core, design units.</summary>
    public const double TaglineSize = 12.5;
    public const double TaglineTop = 252;

    public IntroLayout(double width, double height)
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        Scale = Math.Min(Width / DesignWidth, Height / DesignHeight);
        Core = new Point(Width / 2, (Height / 2) - (56 * Scale));
        if (Scale > 0)
        {
            double dx = Width / 2 / Scale;
            double dy = Math.Max(Core.Y, Height - Core.Y) / Scale;
            Reach = Math.Sqrt((dx * dx) + (dy * dy));
        }

        var transform = new MatrixTransform(Scale, 0, 0, Scale, Core.X, Core.Y);
        transform.Freeze();
        DesignTransform = transform;
    }

    public double Width { get; }

    public double Height { get; }

    /// <summary>Screen DIPs per design unit.</summary>
    public double Scale { get; }

    /// <summary>Screen position of the core (diamond center, implosion focus).</summary>
    public Point Core { get; }

    /// <summary>Design-space distance from the core to the farthest overlay corner.</summary>
    public double Reach { get; }

    /// <summary>Design space (origin at the core) → overlay coordinates.</summary>
    public Transform DesignTransform { get; }

    public bool IsEmpty => Scale <= 0.001;

    /// <summary>Core in relative (0..1) overlay coordinates (shader centers).</summary>
    public Point RelativeCore => IsEmpty ? new Point(0.5, 0.5) : new Point(Core.X / Width, Core.Y / Height);

    /// <summary>The diamond's square viewport in overlay coordinates.</summary>
    public Rect DiamondRect
    {
        get
        {
            double side = DiamondBox * Scale;
            return new Rect(Core.X - (side / 2), Core.Y - (side / 2), side, side);
        }
    }
}
