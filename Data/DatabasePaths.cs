namespace AiCeo.Data;

/// <summary>
/// Resolves the SQLite file location. The published app is a single .exe, which may live in a
/// read-only folder, so user data defaults to <c>%LOCALAPPDATA%</c>.
/// </summary>
public static class DatabasePaths
{
    public const string EnvironmentVariable = "AICEO_DB_PATH";

    public static string Resolve(IConfiguration configuration, string contentRootPath)
    {
        var configured = configuration["Database:Path"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directory = string.IsNullOrWhiteSpace(localAppData)
                ? Path.Combine(contentRootPath, "App_Data")
                : Path.Combine(localAppData, "AiCeoOrchestrator");

            return EnsureDirectory(Path.Combine(directory, "aiceo.db"));
        }

        var path = configured.Trim();
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(contentRootPath, path);
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
