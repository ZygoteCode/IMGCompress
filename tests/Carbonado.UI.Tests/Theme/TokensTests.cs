using System.Windows;
using System.Windows.Media;
using Carbonado.UI.Motion;
using Carbonado.UI.Tests.TestSupport;

namespace Carbonado.UI.Tests.Theme;

public class TokensTests
{
    [Fact]
    public void Tokens_LoadAndExposeBrandKeys() => Sta.Run(() =>
    {
        var dict = Snapshot.LoadDictionary("Theme/Tokens.xaml");
        foreach (string key in new[] { "B.Window", "B.Surface", "B.Text", "B.Accent", "B.Prisma", "B.Spectrum", "Font.UI", "Ease.Crystallize" })
        {
            Assert.True(dict.Contains(key), $"missing {key}");
        }

        var window = (SolidColorBrush)dict["B.Window"];
        Assert.Equal(Color.FromRgb(0x05, 0x06, 0x0A), window.Color);
        Assert.True(window.IsFrozen);
    });

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    public void Bezier_Endpoints(double t, double expected) =>
        Assert.Equal(expected, new CubicBezier(0.16, 1, 0.3, 1).Evaluate(t), 6);

    [Fact]
    public void Bezier_Linear_IsIdentity()
    {
        var linear = new CubicBezier(0, 0, 1, 1);
        for (double t = 0; t <= 1; t += 0.05)
        {
            Assert.Equal(t, linear.Evaluate(t), 5);
        }
    }

    [Fact]
    public void Bezier_EaseOutIsAheadOfLinear()
    {
        var crystallize = new CubicBezier(0.16, 1, 0.3, 1);
        Assert.True(crystallize.Evaluate(0.25) > 0.6);
        Assert.True(crystallize.Evaluate(0.5) > crystallize.Evaluate(0.25));
    }
}
