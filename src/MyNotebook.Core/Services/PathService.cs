namespace MyNotebook.Core.Services;

/// <summary>
/// Portable-first path resolver. Order:
///   1. An explicit root (tests, or a user-chosen folder) if supplied.
///   2. "<exe folder>\Data" when writable  → IsPortable = true.
///   3. "%LOCALAPPDATA%\MyNotebook"          → IsPortable = false (fallback).
/// </summary>
public sealed class PathService : IPathService
{
    private const string DataFolderName = "Data";
    private const string FallbackAppName = "MyNotebook";

    public string DataRoot { get; }
    public bool IsPortable { get; }

    public PathService(string? explicitRoot = null, string? exeDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            DataRoot = Path.GetFullPath(explicitRoot);
            Directory.CreateDirectory(DataRoot);
            IsPortable = true;
            return;
        }

        var baseDir = exeDirectory ?? AppContext.BaseDirectory;
        var portable = Path.Combine(baseDir, DataFolderName);

        if (TryEnsureWritable(portable))
        {
            DataRoot = portable;
            IsPortable = true;
        }
        else
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FallbackAppName);
            Directory.CreateDirectory(fallback);
            DataRoot = fallback;
            IsPortable = false;
        }
    }

    public string DbPath => Path.Combine(DataRoot, "notebook.db");

    public string EnsureScreenshotDir(long noteId)
    {
        var dir = Path.Combine(DataRoot, "attachments", "screenshots", noteId.ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string NewScreenshotRelPath(long noteId)
    {
        EnsureScreenshotDir(noteId);
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var shortGuid = Guid.NewGuid().ToString("N").Substring(0, 8);
        // Forward slashes in storage; ToAbsolute normalizes per-OS.
        return $"attachments/screenshots/{noteId}/{ts}_{shortGuid}.png";
    }

    public string ToAbsolute(string relPath)
    {
        var native = relPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(DataRoot, native);
    }

    /// <summary>Probe-write a temp file to confirm the directory is writable.</summary>
    private static bool TryEnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
