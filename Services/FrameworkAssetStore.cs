using System.Reflection;

namespace AiCeo.Services;

/// <summary>
/// The Blazor client script (<c>_framework/blazor.web.js</c>) is a loose file beside the exe after a
/// single-file publish. The build also embeds it (and <c>blazor.server.js</c>) into the assembly so
/// <c>AiCeo.exe</c> can be shipped on its own; this store maps <c>/_framework/*</c> onto the
/// embedded copy. Identical bytes, no version skew with the runtime.
/// </summary>
public sealed class FrameworkAssetStore
{
    private const string ResourcePrefix = "AiCeo.FrameworkAssets.";
    private const string FrameworkSegment = "_framework/";

    private readonly Dictionary<string, string> _resources = new(StringComparer.OrdinalIgnoreCase);

    public FrameworkAssetStore(Assembly assembly)
    {
        // Logical names look like:
        //   AiCeo.FrameworkAssets.10.0.13/_framework/blazor.web.js
        // The version prefix lets several cached SDK versions coexist in one assembly; the newest
        // version wins for each URL path.
        var candidates = new Dictionary<string, (string Resource, string Version)>(StringComparer.OrdinalIgnoreCase);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = resourceName[ResourcePrefix.Length..].Replace('\\', '/');
            var index = relative.IndexOf(FrameworkSegment, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var version = relative[..index].Trim('/');
            var urlPath = "/" + relative[index..];

            if (!candidates.TryGetValue(urlPath, out var existing) || CompareVersions(version, existing.Version) > 0)
            {
                candidates[urlPath] = (resourceName, version);
            }
        }

        foreach (var candidate in candidates)
        {
            _resources[candidate.Key] = candidate.Value.Resource;
        }
    }

    public static FrameworkAssetStore Instance { get; } =
        new(typeof(FrameworkAssetStore).Assembly);

    public IReadOnlyCollection<string> Paths => _resources.Keys;

    public bool TryGetResource(string assetPath, out string resourceName)
        => _resources.TryGetValue("/_framework/" + assetPath.TrimStart('/'), out resourceName!);

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

    private static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.CompareOrdinal(left, right);
    }
}
