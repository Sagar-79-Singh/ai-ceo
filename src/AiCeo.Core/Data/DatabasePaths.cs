namespace AiCeo.Data;

/// <summary>
/// Resolves the SQLite file location for the desktop app: <c>AICEO_DB_PATH</c> wins, otherwise the
/// database lives in the app's private data folder (which is always writable).
/// </summary>
public static class DatabasePaths
{
    public const string EnvironmentVariable = "AICEO_DB_PATH";

    /// <summary>
    /// Resolves the database path for the given fallback folder (the app's data directory),
    /// creating the directory when needed.
    /// </summary>
    public static string ResolveFromEnvironment(string fallbackDirectory)
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return EnsureDirectory(Path.Combine(fallbackDirectory, "aiceo.db"));
        }

        var path = configured.Trim();
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(fallbackDirectory, path);
        }

        return EnsureDirectory(path);
    }

    private static string EnsureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return fullPath;
    }
}
