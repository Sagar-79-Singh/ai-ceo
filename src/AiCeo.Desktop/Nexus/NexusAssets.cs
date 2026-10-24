using System.Security.Cryptography;
using System.Text;
using Avalonia.Platform;

namespace AiCeo.Desktop.Nexus;

/// <summary>
/// Materialises the Nexus shell (<c>Assets/nexus.html</c> + <c>Assets/nexus.css</c>) on disk so the
/// native web engine can load it from a real <c>file://</c> URL.
///
/// A <c>file://</c> URL — rather than a local HTTP server or <c>NavigateToString</c> — keeps the
/// process free of listening sockets and sidesteps the 2 MB string limit of the WebView2
/// "navigate to string" API (the precompiled Tailwind stylesheet alone is far larger than that).
/// The files are rewritten only when their contents change, so start-up stays cheap.
/// </summary>
public static class NexusAssets
{
    /// <summary>File name of the page inside the extraction folder.</summary>
    public const string PageFileName = "nexus.html";

    /// <summary>File name of the compiled Tailwind + Font Awesome stylesheet.</summary>
    public const string StyleFileName = "nexus.css";

    private const string PageResource = "Assets/nexus.html";
    private const string StyleResource = "Assets/nexus.css";

    /// <summary>Extracts the shell and returns the <c>file://</c> URL the web view should open.</summary>
    public static Uri EnsureExtracted(out string directory)
    {
        var folder = Path.Combine(AppPaths.DataDirectory(), "nexus");

        try
        {
            directory = Write(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A locked-down profile can still run the UI from the temp folder.
            directory = Write(Path.Combine(Path.GetTempPath(), "AiCeoOrchestrator", "nexus"));
        }

        return new Uri(Path.Combine(directory, PageFileName));
    }

    /// <summary>Reads the page straight out of the assembly (used by the diagnostics/self-test).</summary>
    public static string ReadPage() => ReadResource(PageResource);

    /// <summary>Reads the compiled stylesheet straight out of the assembly.</summary>
    public static string ReadStyles() => ReadResource(StyleResource);

    /// <summary>Validates that both assets exist and still carry the host-bridge markers.</summary>
    public static bool IsValid(out string detail)
    {
        try
        {
            var html = ReadPage();
            var css = ReadStyles();

            var hasBridge = html.Contains("nexusReceive", StringComparison.Ordinal)
                && html.Contains("chrome.webview.postMessage", StringComparison.Ordinal);
            var hasShader = html.Contains("shader-canvas-ANIMATION_5", StringComparison.Ordinal)
                && html.Contains("precision highp float", StringComparison.Ordinal);
            var hasStyles = html.Contains("href=\"nexus.css\"", StringComparison.Ordinal)
                && css.Contains("--obsidian-950-rgb", StringComparison.Ordinal)
                && css.Contains("font-family:\"Font Awesome 6 Free\"");

            detail = $"nexus.html {html.Length / 1024} KB · nexus.css {css.Length / 1024} KB"
                + $" · bridge: {hasBridge} · shader: {hasShader} · styles: {hasStyles}";

            return hasBridge && hasShader && hasStyles;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
        {
            detail = "Nexus assets missing: " + exception.Message;
            return false;
        }
    }

    private static string Write(string folder)
    {
        Directory.CreateDirectory(folder);

        WriteIfChanged(Path.Combine(folder, PageFileName), ReadPage());
        WriteIfChanged(Path.Combine(folder, StyleFileName), ReadStyles());

        return folder;
    }

    private static void WriteIfChanged(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);

        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);

            if (existing.Length == bytes.Length
                && SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            {
                return;
            }
        }

        File.WriteAllBytes(path, bytes);
    }

    private static string ReadResource(string path)
    {
        var assemblyName = typeof(NexusAssets).Assembly.GetName().Name ?? "AiCeo";
        using var stream = AssetLoader.Open(new Uri($"avares://{assemblyName}/{path}"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
