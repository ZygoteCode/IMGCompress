using System.Windows;
using Carbonado.UI.Tests.TestSupport;
using Carbonado.UI.Tests.Theme;

namespace Carbonado.UI.Tests.Controls;

public class ControlSnapshotTests
{
    public static TheoryData<string> SceneNames => BrandScenes.Names();

    /// <summary>Renders every brand control composition to tests/artifacts/ui/controls for visual review.</summary>
    [Theory]
    [MemberData(nameof(SceneNames))]
    public void BrandControl_RendersScene(string name) => Sta.Run(() =>
    {
        var scene = BrandScenes.All[name];
        var root = scene.Build();
        root.HorizontalAlignment = HorizontalAlignment.Center;
        root.VerticalAlignment = VerticalAlignment.Center;
        var stage = ThemeHarness.Stage(root);
        ThemeHarness.Layout(stage, scene.Width, scene.Height);
        scene.AfterLayout?.Invoke(root);
        ThemeHarness.Layout(stage, scene.Width, scene.Height);

        var bitmap = ThemeHarness.Render(stage, scene.Width, scene.Height, "ui/controls", name);
        Assert.True(Snapshot.DistinctColors(bitmap, 2) > 12, $"{name} rendered (almost) blank");
    });
}
