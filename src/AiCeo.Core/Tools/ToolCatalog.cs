using AiCeo.Services;

namespace AiCeo.Tools;

/// <summary>
/// Tool names and JSON schemas exposed to the model. Kept in one place so the prompt, the
/// dispatcher and the tests can never disagree about what a tool is called.
/// </summary>
public static class ToolCatalog
{
    public const string ListFiles = "list_files";
    public const string ReadFile = "read_file";
    public const string WriteFile = "write_file";
    public const string RunCommand = "run_command";

    /// <summary>Every tool the workers can call (in the order they are advertised).</summary>
    public static IReadOnlyList<LlmToolDefinition> Definitions { get; } = new[]
    {
        new LlmToolDefinition(
            ListFiles,
            "List files and folders inside the workspace. Paths are relative to the workspace root; "
            + "omit the path or pass '.' for the root. Never reaches outside the workspace.",
            """
            {"type":"object","properties":{"directoryPath":{"type":"string","description":"Relative path inside the workspace. Omit for the root."}},"required":[]}
            """),

        new LlmToolDefinition(
            ReadFile,
            "Read a UTF-8 text file from the workspace and return its contents. "
            + "Pass a path relative to the workspace root.",
            """
            {"type":"object","properties":{"filePath":{"type":"string","description":"Path of the file to read, relative to the workspace root."}},"required":["filePath"]}
            """),

        new LlmToolDefinition(
            WriteFile,
            "Create or overwrite a text file in the workspace with the supplied content. "
            + "Parent folders are created as needed. Use this to write code and files to the user's disk.",
            """
            {"type":"object","properties":{"filePath":{"type":"string","description":"Path of the file to write, relative to the workspace root."},"content":{"type":"string","description":"Full file content to write."}},"required":["filePath","content"]}
            """),

        new LlmToolDefinition(
            RunCommand,
            "Run a terminal command in the workspace folder and return stdout, stderr and the exit code. "
            + "The working directory is always the workspace root. Destructive system commands and paths "
            + "outside the workspace are refused.",
            """
            {"type":"object","properties":{"command":{"type":"string","description":"Command line to execute, e.g. \"dotnet build\" or \"dir\"."}},"required":["command"]}
            """)
    };

    /// <summary>Normalises tool names from models that use CamelCase or a namespace prefix.</summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();

        // Some models emit "functions.list_files" or "tools/list_files".
        var separator = trimmed.LastIndexOfAny(new[] { '.', '/' });
        if (separator >= 0 && separator < trimmed.Length - 1)
        {
            trimmed = trimmed[(separator + 1)..];
        }

        var compact = trimmed.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        return compact switch
        {
            "listfiles" => ListFiles,
            "readfile" => ReadFile,
            "writefile" => WriteFile,
            "runcommand" or "executecommand" or "runterminal" => RunCommand,
            _ => null
        };
    }
}
