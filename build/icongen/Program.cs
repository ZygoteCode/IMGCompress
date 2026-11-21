using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Carbonado.UI.Brand;

namespace Carbonado.Build;

/// <summary>
/// <c>IconGen &lt;out.ico&gt; [previewDir]</c>: renders the brand <see cref="DiamondMark"/> at every Windows icon size
/// (16–256 px, PNG-compressed entries; the mark picks its level of detail from the pixel size).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
        var frames = new List<(int Size, byte[] Png)>();
        foreach (int size in sizes)
        {
            var mark = new DiamondMark { Width = size, Height = size };
            mark.Measure(new Size(size, size));
            mark.Arrange(new Rect(0, 0, size, size));
            mark.UpdateLayout();
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(mark);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            frames.Add((size, ms.ToArray()));
            if (args.Length > 1)
            {
                Directory.CreateDirectory(args[1]);
                File.WriteAllBytes(Path.Combine(args[1], $"icon-{size}.png"), ms.ToArray());
            }
        }

        using var ico = File.Create(args[0]);
        using var w = new BinaryWriter(ico);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in frames)
        {
            w.Write(png);
        }

        Console.WriteLine($"wrote {args[0]} ({frames.Count} sizes, {offset} bytes)");
        return 0;
    }
}
