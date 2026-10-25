using System.Text;

namespace AiCeo.Desktop.Nexus;

/// <summary>
/// The Nexus shell (<c>Assets/nexus.html</c> + <c>Assets/nexus.css</c>) as the native WebView sees it.
///
/// v1.0.15: the page is handed to PhotinoX with <c>LoadString</c> instead of being extracted to a
/// <c>file://</c> URL. The compiled Tailwind stylesheet is inlined into the document first, so the
/// page has no sub-resources of its own to fetch and therefore cannot be broken by a web engine
/// that restricts <c>file://</c> access (WebKitGTK gates that behind a setting, WKWebView gates it
/// behind read-access grants). The document is ~190 KB, comfortably inside the 2 MB limit of the
/// WebView2 "navigate to string" API, and there is nothing left to fail at start-up.
/// </summary>
public static class NexusAssets
{
    /// <summary>File name of the page inside the assembly.</summary>
    public const string PageFileName = "nexus.html";

    /// <summary>File name of the compiled Tailwind + Font Awesome stylesheet.</summary>
    public const string StyleFileName = "nexus.css";

    private const string PageResource = "AiCeo.Desktop.Assets.nexus.html";
    private const string StyleResource = "AiCeo.Desktop.Assets.nexus.css";

    /// <summary>The stylesheet reference the page ships with; replaced by an inline block.</summary>
    private const string StyleLink = "<link rel=\"stylesheet\" href=\"nexus.css\" />";

    /// <summary>
    /// The complete document the window loads: the shipped HTML with the stylesheet inlined.
    /// Deterministic and side-effect free, so the smoke test exercises exactly what start-up uses.
    /// </summary>
    public static string RenderDocument()
    {
        var html = ReadPage();
        var css = ReadStyles();

        var document = html.Contains(StyleLink, StringComparison.Ordinal)
            ? html.Replace(StyleLink, "<style>" + Environment.NewLine + css + Environment.NewLine + "</style>", StringComparison.Ordinal)
            : html.Insert(InsertIndex(html), "<style>" + Environment.NewLine + css + Environment.NewLine + "</style>");

        return document;
    }

    /// <summary>Reads the page straight out of the assembly.</summary>
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

            // The page→host path prefers window.external.sendMessage (PhotinoX injects it on every
            // engine) and still falls back to chrome.webview.postMessage; the host→page path is the
            // nexusReceive/nexusToast pair NexusPresenter sends.
            var hasBridge = html.Contains("external.sendMessage", StringComparison.Ordinal)
                && html.Contains("external.receiveMessage", StringComparison.Ordinal)
                && html.Contains("chrome.webview.postMessage", StringComparison.Ordinal)
                && html.Contains("window.nexusReceive", StringComparison.Ordinal)
                && html.Contains("window.nexusToast", StringComparison.Ordinal);
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

    /// <summary>Fallback position for the inline stylesheet: the end of the document head.</summary>
    private static int InsertIndex(string html)
    {
        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return head >= 0 ? head : 0;
    }

    private static string ReadResource(string name)
    {
        var assembly = typeof(NexusAssets).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded resource '{name}' is missing from {assembly.GetName().Name}.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
