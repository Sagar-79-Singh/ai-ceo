namespace AiCeo.Tools;

/// <summary>
/// The single chokepoint that decides whether a path is inside the workspace.
///
/// It defends against the usual escape attempts:
/// <list type="bullet">
///   <item><c>..</c> traversal and mixed separators (<c>..\..\Windows\System32</c>)</item>
///   <item>absolute paths outside the root (<c>C:\Windows\win.ini</c>, <c>/etc/passwd</c>)</item>
///   <item>paths that only share a prefix (<c>C:\Work</c> vs <c>C:\WorkspaceEvil</c>)</item>
///   <item>symlinks/junctions inside the workspace that point outside it</item>
///   <item>drive-relative and UNC paths</item>
/// </list>
/// </summary>
public static class WorkspaceGuard
{
    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Resolves <paramref name="candidatePath"/> against <paramref name="workspaceRoot"/> and
    /// throws <see cref="WorkspaceViolationException"/> when the result is not inside the root.
    /// Relative paths are resolved against the root.
    /// </summary>
    public static string Resolve(string? workspaceRoot, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw WorkspaceViolationException.NoWorkspace();
        }

        var root = NormalizeRoot(workspaceRoot);

        var raw = string.IsNullOrWhiteSpace(candidatePath) ? "." : candidatePath.Trim();
        if (raw.Contains('\0'))
        {
            throw WorkspaceViolationException.Outside(raw, root);
        }

        // A rooted path is taken as-is; anything else is relative to the workspace.
        string combined;
        try
        {
            combined = Path.IsPathRooted(raw) ? raw : Path.Combine(root, raw);
        }
        catch (ArgumentException)
        {
            throw WorkspaceViolationException.Outside(raw, root);
        }

        string full;
        try
        {
            full = Path.GetFullPath(combined);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw WorkspaceViolationException.Outside(raw, root);
        }

        if (!IsInside(root, full))
        {
            throw WorkspaceViolationException.Outside(raw, root);
        }

        // Symlinks: if the target (or its parent directory) resolves somewhere else, re-validate.
        var realRoot = ResolveLinkTarget(root) ?? root;
        var realTarget = ResolveLinkTarget(full);
        if (realTarget is not null && !IsInside(realRoot, realTarget))
        {
            throw WorkspaceViolationException.Outside(raw, root);
        }

        return full;
    }

    /// <summary>Non-throwing variant, used by the UI for validation.</summary>
    public static bool TryResolve(string? workspaceRoot, string? candidatePath, out string fullPath, out string? error)
    {
        try
        {
            fullPath = Resolve(workspaceRoot, candidatePath);
            error = null;
            return true;
        }
        catch (WorkspaceViolationException exception)
        {
            fullPath = string.Empty;
            error = exception.Message;
            return false;
        }
    }

    /// <summary>True when <paramref name="path"/> is the root itself or lives underneath it.</summary>
    public static bool IsInside(string workspaceRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var root = NormalizeRoot(workspaceRoot);
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (string.Equals(candidate, Path.TrimEndingDirectorySeparator(root), PathComparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, PathComparison);
    }

    /// <summary>Converts a full path into a workspace-relative path for display and tool output.</summary>
    public static string ToRelative(string workspaceRoot, string fullPath)
    {
        var root = NormalizeRoot(workspaceRoot);
        var candidate = Path.GetFullPath(fullPath);

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(candidate),
                Path.TrimEndingDirectorySeparator(root),
                PathComparison))
        {
            return ".";
        }

        var relative = Path.GetRelativePath(root, candidate);
        return relative.Replace('\\', '/');
    }

    /// <summary>Normalises a folder path so prefix checks are unambiguous.</summary>
    public static string NormalizeRoot(string workspaceRoot)
    {
        var full = Path.GetFullPath(workspaceRoot.Trim());
        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>
    /// Resolves symlinks/junctions for an existing path (walking up to the nearest existing
    /// ancestor), so a link inside the workspace cannot smuggle access to the outside.
    /// </summary>
    private static string? ResolveLinkTarget(string path)
    {
        var current = Path.GetFullPath(path);

        for (var depth = 0; depth < 32; depth++)
        {
            try
            {
                var info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : File.Exists(current)
                        ? new FileInfo(current)
                        : null;

                if (info is not null && info.LinkTarget is not null)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is not null)
                    {
                        return Path.GetFullPath(target.FullName);
                    }
                }
            }
            catch (IOException)
            {
                // Broken link or unreadable entry: treat as "no link" and continue upwards.
            }
            catch (UnauthorizedAccessException)
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        return null;
    }
}
