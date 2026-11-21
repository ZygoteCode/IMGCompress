using Carbonado.Core.Encoders.Adapters;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders;

/// <summary>
/// The encoder adapters usable on this machine: those whose tools are installed. Fallbacks are registered only when
/// the primary encoder for their format is missing (ffmpeg's libjxl/libaom/libwebp without cjxl/avifenc/cwebp; the
/// built-in PNG optimizer without oxipng and ECT).
/// </summary>
public sealed class EncoderRegistry
{
    public EncoderRegistry(ToolLocator tools, ProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(runner);
        IEncoderAdapter[] all =
        [
            new CjxlAdapter(tools, runner),
            new AvifencAdapter(tools, runner),
            new CwebpAdapter(tools, runner),
            new SjpegAdapter(tools, runner),
            new OxipngAdapter(tools, runner),
            new EctAdapter(tools, runner),
            new PngquantAdapter(tools, runner),
            new Gif2WebpAdapter(tools, runner),
            new BuiltinPngAdapter(tools, runner),
            new FfmpegJxlAdapter(tools, runner),
            new FfmpegAvifAdapter(tools, runner),
            new FfmpegWebPAdapter(tools, runner),
        ];
        Available = all.Where(a => a.RequiredTools.All(tools.Has) && !IsShadowed(a.Id, tools)).ToList();
    }

    /// <summary>Usable adapters, primary encoders first.</summary>
    public IReadOnlyList<IEncoderAdapter> Available { get; }

    /// <summary>Usable adapters producing <paramref name="f"/> that can make <paramref name="k"/> outputs.</summary>
    public IEnumerable<IEncoderAdapter> For(OutputFormat f, EncodeKind k) => Available.Where(a => a.Format == f && a.Kinds.Contains(k));

    /// <summary>The usable adapter with this id, or null.</summary>
    public IEncoderAdapter? Find(string id) => Available.FirstOrDefault(a => a.Id == id);

    private static bool IsShadowed(string id, ToolLocator tools) => id switch
    {
        "jxl.ffmpeg" => tools.Has(ToolId.Cjxl),
        "avif.ffmpeg" => tools.Has(ToolId.Avifenc),
        "webp.ffmpeg" => tools.Has(ToolId.Cwebp),
        "png.builtin" => tools.Has(ToolId.Oxipng) || tools.Has(ToolId.Ect),
        _ => false,
    };
}
