namespace SqliteBrowser.Core.Tests.TestSupport;

/// <summary>Allocates a unique temporary file path and deletes it (plus any -wal/-shm sidecar files) on dispose.</summary>
public sealed class TempFile : IDisposable
{
    public string Path { get; }

    public TempFile(string extension = ".db")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sqlitebrowser-tests-{Guid.NewGuid():N}{extension}");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = Path + suffix;
            if (File.Exists(candidate))
            {
                try
                {
                    File.Delete(candidate);
                }
                catch (IOException)
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }
}
