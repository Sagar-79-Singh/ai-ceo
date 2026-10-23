using System.Text.RegularExpressions;

namespace AiCeo.Tools;

/// <summary>
/// Best-effort safety net for <c>RunCommand</c>.
///
/// A shell can do anything its user can do, so this is deliberately honest about its limits:
/// it runs every command with the workspace as the working directory, refuses a denylist of
/// destructive system operations, and refuses commands that name paths outside the workspace.
/// It is a guardrail, not an OS sandbox — the README says so too.
/// </summary>
public static class CommandGuard
{
    /// <summary>Commands that are refused outright, whatever the workspace.</summary>
    private static readonly string[] DeniedPatterns =
    {
        @"\bformat(\.com)?\b",
        @"\bdiskpart\b",
        @"\bbcdedit\b",
        @"\bshutdown\b",
        @"\brestart-computer\b",
        @"\bstop-computer\b",
        @"\breg\s+(add|delete|import|load|unload)\b",
        @"\bregedit\b",
        @"\bmkfs(\.\w+)?\b",
        @"\bdd\s+if=",
        @"\bcipher\s+/w\b",
        @"\btakeown\b",
        @"\bicacls\b.*\s/grant\b",
        @"\bnet\s+user\b",
        @"\bnet\s+localgroup\b",
        @"\bnetsh\b",
        @"\bschtasks\b\s+/create\b",
        @"\battrib\b.*\s/s\b",
        @"\bdel\b\s+/[sfq].*\b(/s|/q)\b",
        @"\brm\b\s+(-[a-z]*r[a-z]*f|-[a-z]*f[a-z]*r)\w*\s+(/|~|\*)",
        @"\brd\b\s+/s\b",
        @"\brmdir\b\s+/s\b",
        @"\bgit\s+push\b.*\s--force\b",
        @"\bcurl\b[^|]*\|\s*(ba)?sh\b",
        @"\biwr\b[^|]*\|\s*iex\b",
        @"\binvoke-expression\b",
        @"\bsc\s+(delete|config)\b",
        @"\bwmic\b",
        @"\bpowershell(\.exe)?\b.*-(e|enc|encodedcommand)\b"
    };

    private static readonly Regex[] DeniedRegexes = DeniedPatterns
        .Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
        .ToArray();

    /// <summary>Absolute-path-looking tokens, checked against the workspace root.</summary>
    private static readonly Regex PathTokenRegex = new(
        @"(?<token>(?:[A-Za-z]:[\\/]|\\\\[^\s""']+|(?:^|[\s""'=<>|&(])/(?:[^\s""']+)))",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public const int MaxCommandLength = 2000;

    /// <summary>Throws <see cref="WorkspaceViolationException"/> when a command is not allowed.</summary>
    public static void Validate(string command, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new WorkspaceViolationException("The terminal command was empty.");
        }

        if (command.Length > MaxCommandLength)
        {
            throw new WorkspaceViolationException($"The terminal command is longer than {MaxCommandLength} characters.");
        }

        foreach (var regex in DeniedRegexes)
        {
            if (regex.IsMatch(command))
            {
                throw new WorkspaceViolationException(
                    $"Blocked: '{regex}' is a destructive or system-level operation and is not allowed from an agent.");
            }
        }

        // Any absolute path mentioned must live inside the workspace (commands that reference
        // executables by bare name are fine).
        foreach (Match match in PathTokenRegex.Matches(command))
        {
            var token = match.Groups["token"].Value.Trim('"', '\'', '(', '&', '|', '>', '<', '=');

            // "/s", "/q" style switches are not paths.
            if (token.Length <= 2 || (token.StartsWith('/') && token.Length <= 3 && !token.Contains('/', StringComparison.Ordinal)))
            {
                continue;
            }

            if (token.StartsWith('/') && OperatingSystem.IsWindows())
            {
                continue; // Windows switches like /c, /f
            }

            var path = token;
            if (!WorkspaceGuard.TryResolve(workspaceRoot, path, out _, out _))
            {
                throw new WorkspaceViolationException(
                    $"Blocked: the command references '{token}', which is outside the workspace folder.");
            }
        }
    }
}
