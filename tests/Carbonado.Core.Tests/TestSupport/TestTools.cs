using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.TestSupport;

/// <summary>Locates the repository's encoder binaries for integration tests.</summary>
public static class TestTools
{
    private static readonly Lazy<string?> s_root = new(FindRoot);

    /// <summary>The tools root (contains manifest.json), or null when not found.</summary>
    public static string? Root => s_root.Value;

    /// <summary>Relative path of each tool inside the tools root.</summary>
    public static string RelativePath(ToolId id) => id switch
    {
        ToolId.Cjxl => @"libjxl\cjxl.exe",
        ToolId.Djxl => @"libjxl\djxl.exe",
        ToolId.Jxlinfo => @"libjxl\jxlinfo.exe",
        ToolId.Ssimulacra2 => @"libjxl\ssimulacra2.exe",
        ToolId.Avifenc => @"libavif\avifenc.exe",
        ToolId.Avifdec => @"libavif\avifdec.exe",
        ToolId.Cwebp => @"libwebp\cwebp.exe",
        ToolId.Dwebp => @"libwebp\dwebp.exe",
        ToolId.Gif2webp => @"libwebp\gif2webp.exe",
        ToolId.Oxipng => @"oxipng\oxipng.exe",
        ToolId.Ect => @"ect\ect.exe",
        ToolId.Pngquant => @"pngquant\pngquant.exe",
        ToolId.Ffmpeg => @"ffmpeg\ffmpeg.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    /// <summary>Absolute path of the tool, or null if missing.</summary>
    public static string? Path(ToolId id)
    {
        if (Root is null)
        {
            return null;
        }

        string p = System.IO.Path.Combine(Root, RelativePath(id));
        return File.Exists(p) ? p : null;
    }

    /// <summary>Returns the tool path or skips the current test when the tool is unavailable.</summary>
    public static string Require(ToolId id)
    {
        string? p = Path(id);
        if (p is null)
        {
            Assert.Skip($"Tool {id} not available (set CARBONADO_TOOLS_DIR or run build/fetch-tools.ps1).");
        }

        return p!;
    }

    private static string? FindRoot()
    {
        string? env = Environment.GetEnvironmentVariable("CARBONADO_TOOLS_DIR");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(System.IO.Path.Combine(env, "manifest.json")))
        {
            return System.IO.Path.GetFullPath(env);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, "tools", "manifest.json");
            if (File.Exists(candidate))
            {
                return System.IO.Path.GetDirectoryName(candidate);
            }

            dir = dir.Parent;
        }

        return null;
    }
}
