namespace AiCeo.Desktop;

/// <summary>Per-user locations that work the same on Windows, Linux and macOS.</summary>
internal static class AppPaths
{
    private const string FolderName = "AiCeoOrchestrator";

    /// <summary>
    /// Writable per-user data folder. A single-file executable can live anywhere (Downloads, a USB
    /// stick, a read-only share), so state never goes next to the binary.
    /// </summary>
    internal static string DataDirectory()
    {
        var root = RootDirectory();
        var directory = Path.Combine(root, FolderName);

        try
        {
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fall back to the temp folder rather than failing to start.
            var fallback = Path.Combine(Path.GetTempPath(), FolderName);
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static string RootDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrWhiteSpace(appData) ? AppContext.BaseDirectory : appData;
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return xdg;
        }

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".local", "share");
    }
}
