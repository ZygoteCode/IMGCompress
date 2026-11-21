namespace Carbonado.Core.Tests.TestSupport;

/// <summary>A unique temporary directory deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "carbonado-tests", (name ?? "t") + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Sub(string name)
    {
        string p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    {
                        System.IO.File.SetAttributes(f, FileAttributes.Normal);
                    }

                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}
