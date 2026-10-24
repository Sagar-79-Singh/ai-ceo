using Avalonia.Platform;

namespace AiCeo.Desktop.Nexus;

/// <summary>
/// Decides whether the embedded Nexus surface (the HTML/Tailwind/WebGL shell) can be shown.
///
/// The window is only a shell: the real UI lives in <c>Assets/nexus.html</c> and is rendered by the
/// platform's native web engine through <c>Avalonia.Controls.WebView</c>. When no engine is present
/// (for example a bare Linux box without WebKitGTK) the app falls back to the native Avalonia shell
/// instead of failing to start.
/// </summary>
public static class NexusAvailability
{
    /// <summary>Environment variable that force-selects a shell: <c>nexus</c> or <c>classic</c>.</summary>
    public const string OverrideVariable = "AICEO_UI";

    /// <summary>True when the HTML surface should be used on this machine.</summary>
    public static bool IsSupported => Probe().Supported;

    /// <summary>Human-readable explanation of the decision (logged and shown in diagnostics).</summary>
    public static string Reason => Probe().Reason;

    /// <summary>True when the user asked for the classic shell via <c>AICEO_UI=classic</c>.</summary>
    public static bool IsForcedClassic
        => string.Equals(Environment.GetEnvironmentVariable(OverrideVariable), "classic", StringComparison.OrdinalIgnoreCase);

    private static (bool Supported, string Reason) Probe()
    {
        if (IsForcedClassic)
        {
            return (false, $"{OverrideVariable}=classic");
        }

        var forced = string.Equals(
            Environment.GetEnvironmentVariable(OverrideVariable),
            "nexus",
            StringComparison.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            return Check(WebViewAdapterType.WebView2, "Edge WebView2", forced);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Check(WebViewAdapterType.WkWebView, "WebKit", forced);
        }

        // Linux embedded engines need distribution packages (WebKitGTK / WPE) that are not
        // guaranteed to exist; the native shell is the safe default there.
        return Check(WebViewAdapterType.WebKitGtk, "WebKitGTK", forced);
    }

    private static (bool Supported, string Reason) Check(WebViewAdapterType type, string label, bool forced)
    {
        try
        {
            var info = WebViewAdapterInfo.GetAdapterInfo(type);

            if (!info.IsSupported)
            {
                return forced
                    ? (true, $"{label} forced by {OverrideVariable} (reported: {info.UnavailableReason})")
                    : (false, info.UnavailableReason ?? $"{label} is not supported on this platform");
            }

            if (!info.IsInstalled)
            {
                return forced
                    ? (true, $"{label} forced by {OverrideVariable} (runtime not detected)")
                    : (false, info.UnavailableReason ?? $"The {label} runtime is not installed");
            }

            return (true, $"{label} {info.Version}".TrimEnd());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return forced
                ? (true, $"{label} forced by {OverrideVariable} (probe failed: {exception.Message})")
                : (false, $"The {label} probe failed: {exception.Message}");
        }
    }
}
