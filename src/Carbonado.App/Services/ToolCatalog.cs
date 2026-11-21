using Carbonado.App.Pages.Engines;
using Carbonado.Core.Decoding;
using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;
using Carbonado.Core.Logging;
using Carbonado.Core.Tools;

namespace Carbonado.App.Services;

/// <summary>
/// Locates the encoders (bundled tools/, repository tools/ in development, legacy IMGCompress folders, PATH),
/// verifies their SHA-256 against tools/manifest.json and exposes what they make possible.
/// </summary>
public sealed class ToolCatalog(AppServices services) : IToolCatalog
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler? Changed;

    public ToolLocator? Locator { get; private set; }

    /// <summary>The engine's universal loader (null until the first refresh).</summary>
    public IImageLoader? Loader { get; private set; }

    public IReadOnlyList<ToolInfo> Tools { get; private set; } = [];

    public IReadOnlyCollection<OutputFormat> AvailableFormats { get; private set; } = [];

    public string ToolsFolder { get; private set; } = Path.Combine(AppContext.BaseDirectory, "tools");

    /// <summary>Locates tools now (fast: paths only); integrity hashes are computed in <see cref="RefreshAsync"/>.</summary>
    public ToolLocator Locate()
    {
        var defaults = ToolLocator.DefaultOptions(AppContext.BaseDirectory);
        var options = defaults with { LegacyDirs = [.. defaults.LegacyDirs ?? [], .. services.Settings.ExtraToolDirs] };
        var locator = new ToolLocator(options);
        ToolsFolder = options.SearchRoots.FirstOrDefault(Directory.Exists) ?? ToolsFolder;
        Locator = locator;
        Loader = new ImageLoader(locator, ProcessRunner.Shared, services.SessionTempDir ?? Path.GetTempPath());
        AvailableFormats = new EncoderRegistry(locator, ProcessRunner.Shared).Available.Select(a => a.Format).Distinct().ToList();
        return locator;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var locator = Locate();
                Tools = locator.Discover();
                foreach (var t in Tools.Where(t => t.Integrity == ToolIntegrity.Mismatch))
                {
                    Log.Warn($"Tool integrity mismatch: {t.Path}");
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
