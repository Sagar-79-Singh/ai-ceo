using System.Reflection;

namespace AiCeo.Services;

/// <summary>
/// The Blazor client script (<c>_framework/blazor.web.js</c>) ships in a single-file publish as a
/// loose file beside the .exe. A single <c>AiCeo.exe</c> is a much better download, so the build
/// also embeds every <c>_framework/*</c> static asset into the assembly and this store serves it
/// when the copy on disk is missing. Identical bytes, no version skew.
/// </summary>
public sealed class FrameworkAssetStore
{
    private const string ResourcePrefix = "AiCeo.FrameworkAssets.";
    private const string UrlPrefix = "/_framework/";

    private readonly Dictionary<string, string> _resources = new(StringComparer.OrdinalIgnoreCase);

    public FrameworkAssetStore(Assembly assembly)
    {
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = resourceName[ResourcePrefix.Length..].Replace('\\', '/');
            if (!relative.StartsWith("_framework/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _resources["/" + relative] = resourceName;
        }
    }

    public static FrameworkAssetStore Instance { get; } =
        new(typeof(FrameworkAssetStore).Assembly);

    public IReadOnlyCollection<string> Paths => _resources.Keys;

    public bool TryGetResource(string assetPath, out string resourceName)
    {
        var key = UrlPrefix + assetPath.TrimStart('/');
        return _resources.TryGetValue(key, out resourceName!);
    }

    public Stream? Open(string resourceName)
        => typeof(FrameworkAssetStore).Assembly.GetManifestResourceStream(resourceName);

    public static string ContentTypeFor(string assetPath)
    {
        if (assetPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            return "text/javascript; charset=utf-8";
        }

        if (assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || assetPath.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
        {
            return "application/json; charset=utf-8";
        }

        if (assetPath.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase))
        {
            return "application/wasm";
        }

        if (assetPath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
        {
            return "text/css; charset=utf-8";
        }

        return "application/octet-stream";
    }
}
