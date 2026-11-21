using System.Collections.Concurrent;
using Carbonado.Core.Engine;
using Carbonado.Core.Persistence;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Watch;

namespace Carbonado.Core.Tests.Watch;

public class FolderWatcherTests
{
    [Fact]
    public async Task Watcher_RaisesOncePerStableFile_IgnoresOutputsAndNonImages()
    {
        using var dir = new TempDir("watch");
        var rule = new WatchRule("r", dir.Path, Recursive: true, "vs", new OutputPlan(), true);
        using var watcher = new FolderWatcher(rule, TimeSpan.FromMilliseconds(200));
        var events = new ConcurrentQueue<string>();
        watcher.FileReady += (_, p) => events.Enqueue(p);

        string image = dir.File("photo.png");
        await using (var fs = new FileStream(image, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            for (int i = 0; i < 6; i++)
            {
                await fs.WriteAsync(new byte[4096], TestContext.Current.CancellationToken);
                await fs.FlushAsync(TestContext.Current.CancellationToken);
                await Task.Delay(60, TestContext.Current.CancellationToken);
            }
        }

        File.WriteAllText(dir.File("readme.txt"), "not an image");
        string outputs = dir.Sub("Carbonado");
        File.WriteAllBytes(Path.Combine(outputs, "photo.avif"), new byte[100]);
        string produced = dir.File("produced.webp");
        watcher.Ignore(produced);
        File.WriteAllBytes(produced, new byte[100]);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (events.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.Equal([image], events.ToArray());
    }

    [Fact]
    public void Watcher_MissingFolder_Throws() =>
        Assert.Throws<DirectoryNotFoundException>(() =>
            new FolderWatcher(new WatchRule("r", @"Z:\definitely\missing\folder", false, "vs", new OutputPlan(), true)));
}
