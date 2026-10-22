using System.Diagnostics;

namespace AiCeo.Services;

/// <summary>
/// Convenience for the published Windows .exe: open the UI in the default browser once Kestrel
/// is listening. Disabled with <c>AICEO_NO_BROWSER=1</c> (and never runs outside Windows).
/// </summary>
public static class BrowserLauncher
{
    public const string DisableVariable = "AICEO_NO_BROWSER";

    public static string ResolveLaunchUrl(IConfiguration configuration)
    {
        var configured = configuration["urls"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var first = configured
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(candidate => candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(first))
            {
                return first
                    .Replace("0.0.0.0", "localhost", StringComparison.Ordinal)
                    .Replace("[::]", "localhost", StringComparison.Ordinal)
                    .Replace("+", "localhost", StringComparison.Ordinal)
                    .Replace("*", "localhost", StringComparison.Ordinal);
            }
        }

        return "http://localhost:5088";
    }

    public static void TryOpen(ILogger logger, string url)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (Environment.GetEnvironmentVariable(DisableVariable) == "1")
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not open the default browser for {Url}", url);
        }
    }
}
