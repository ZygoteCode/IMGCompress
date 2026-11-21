using Carbonado.Core.Engine;
using Carbonado.Core.Tests.TestSupport;

namespace Carbonado.Core.Tests.Engine;

public class AtomicFileTests
{
    [Fact]
    public void AtomicFile_Commit_MovesAndLeavesNoTemp()
    {
        using var dir = new TempDir("atomic");
        string temp = Write(dir, ".tmp-1", "new");
        string final = Path.Combine(dir.Path, "deep", "er", "out.png");

        string? committed = AtomicFile.Commit(temp, final, ConflictPolicy.Rename);

        Assert.Equal(final, committed);
        Assert.Equal("new", File.ReadAllText(final));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void AtomicFile_Rename_PicksTheNextFreeName()
    {
        using var dir = new TempDir("atomic");
        string final = Write(dir, "photo.avif", "first");
        Write(dir, "photo (1).avif", "second");

        string? committed = AtomicFile.Commit(Write(dir, ".tmp", "third"), final, ConflictPolicy.Rename);

        Assert.Equal(dir.File("photo (2).avif"), committed);
        Assert.Equal("first", File.ReadAllText(final));
        Assert.Equal("second", File.ReadAllText(dir.File("photo (1).avif")));
        Assert.Equal("third", File.ReadAllText(committed!));
        Assert.Equal(3, Directory.GetFiles(dir.Path).Length);
    }

    [Fact]
    public void AtomicFile_Rename_SkipsNamesTakenByFolders()
    {
        using var dir = new TempDir("atomic");
        dir.Sub("x.png");

        string? committed = AtomicFile.Commit(Write(dir, ".tmp", "data"), dir.File("x.png"), ConflictPolicy.Rename);

        Assert.Equal(dir.File("x (1).png"), committed);
    }

    [Fact]
    public void AtomicFile_Overwrite_Replaces()
    {
        using var dir = new TempDir("atomic");
        string final = Write(dir, "photo.webp", "old");

        string? committed = AtomicFile.Commit(Write(dir, ".tmp", "new"), final, ConflictPolicy.Overwrite);

        Assert.Equal(final, committed);
        Assert.Equal("new", File.ReadAllText(final));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void AtomicFile_Skip_ReturnsNullAndDeletesTheTemp()
    {
        using var dir = new TempDir("atomic");
        string final = Write(dir, "photo.jxl", "keep me");
        string temp = Write(dir, ".tmp", "discard");

        string? committed = AtomicFile.Commit(temp, final, ConflictPolicy.Skip);

        Assert.Null(committed);
        Assert.Equal("keep me", File.ReadAllText(final));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void AtomicFile_Skip_WritesWhenTheNameIsFree()
    {
        using var dir = new TempDir("atomic");

        string? committed = AtomicFile.Commit(Write(dir, ".tmp", "data"), dir.File("free.png"), ConflictPolicy.Skip);

        Assert.Equal(dir.File("free.png"), committed);
    }

    [Fact]
    public void AtomicFile_Failure_DeletesTheTempAndKeepsTheTarget()
    {
        using var dir = new TempDir("atomic");
        string final = Write(dir, "locked.png", "original");
        File.SetAttributes(final, FileAttributes.ReadOnly);
        string temp = Write(dir, ".tmp", "new");
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => AtomicFile.Commit(temp, final, ConflictPolicy.Overwrite));
            Assert.False(File.Exists(temp));
            Assert.Equal("original", File.ReadAllText(final));

            // A folder in the way of an overwrite.
            string folder = dir.Sub("folder.png");
            string temp2 = Write(dir, ".tmp2", "new");
            Assert.Throws<IOException>(() => AtomicFile.Commit(temp2, folder, ConflictPolicy.Overwrite));
            Assert.False(File.Exists(temp2));
        }
        finally
        {
            File.SetAttributes(final, FileAttributes.Normal);
        }
    }

    [Fact]
    public void AtomicFile_MissingTemp_Throws()
    {
        using var dir = new TempDir("atomic");

        Assert.Throws<FileNotFoundException>(() => AtomicFile.Commit(dir.File("nope.tmp"), dir.File("out.png"), ConflictPolicy.Rename));
        Assert.False(File.Exists(dir.File("out.png")));
    }

    [Fact]
    public void AtomicFile_ConcurrentRenames_NeverOverwrite()
    {
        using var dir = new TempDir("atomic");
        string final = dir.File("same.png");

        var committed = Enumerable.Range(0, 24).AsParallel().Select(i => AtomicFile.Commit(Write(dir, $".tmp-{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture)), final, ConflictPolicy.Rename)).ToList();

        Assert.Equal(24, committed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(24, Directory.GetFiles(dir.Path, "same*.png").Length);
        Assert.Empty(Directory.GetFiles(dir.Path, ".tmp-*"));
        Assert.Equal(Enumerable.Range(0, 24).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(), committed.Select(p => File.ReadAllText(p!)).Order());
    }

    private static string Write(TempDir dir, string name, string text)
    {
        string path = dir.File(name);
        File.WriteAllText(path, text);
        return path;
    }
}
