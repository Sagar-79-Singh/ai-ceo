using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiCeo.Services;
using Microsoft.Extensions.Logging;

namespace AiCeo.Tools;

/// <summary>
/// The four workspace tools the worker agents can call. Every path is funnelled through
/// <see cref="WorkspaceGuard"/> before the file system is touched, and every command is checked
/// by <see cref="CommandGuard"/> and run with the workspace as its working directory.
/// </summary>
public sealed class ToolService
{
    public const int MaxReadChars = 200_000;
    public const int MaxWriteChars = 2_000_000;
    public const int MaxListEntries = 300;
    public const int MaxCommandOutputChars = 24_000;
    public const int DefaultCommandTimeoutSeconds = 90;
    public const int MaxCommandTimeoutSeconds = 300;

    private readonly WorkspaceService _workspace;
    private readonly ILogger<ToolService> _logger;

    public ToolService(WorkspaceService workspace, ILogger<ToolService> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    public static IReadOnlyList<LlmToolDefinition> Definitions => ToolCatalog.Definitions;

    /// <summary>Lists files and folders. Paths are workspace-relative in the output.</summary>
    public async Task<ToolResult> ListFilesAsync(string? directoryPath, CancellationToken cancellationToken = default)
    {
        var root = await RequireRootAsync(cancellationToken);
        var directory = WorkspaceGuard.Resolve(root, directoryPath);

        if (!Directory.Exists(directory))
        {
            return ToolResult.Failure(ToolCatalog.ListFiles, $"'{directoryPath ?? "."}' is not a directory inside the workspace.");
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Workspace: {WorkspaceGuard.ToRelative(root, directory)}");

        var entries = new List<(string Relative, bool IsDirectory, long Size)>();
        var truncated = false;

        try
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                // Link targets are validated: a symlink pointing outside is never listed as usable.
                var isDirectory = info is DirectoryInfo;
                entries.Add((WorkspaceGuard.ToRelative(root, info.FullName), isDirectory, isDirectory ? 0 : ((FileInfo)info).Length));

                if (entries.Count >= MaxListEntries)
                {
                    truncated = true;
                    break;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Failure(ToolCatalog.ListFiles, $"Access to '{directoryPath}' was denied by the operating system.");
        }

        foreach (var entry in entries.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Relative, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(entry.IsDirectory
                ? $"[dir]  {entry.Relative}/"
                : $"[file] {entry.Relative} ({entry.Size} bytes)");
        }

        if (entries.Count == 0)
        {
            builder.AppendLine("(empty)");
        }

        if (truncated)
        {
            builder.AppendLine($"(listing stopped after {MaxListEntries} entries)");
        }

        return ToolResult.Success(ToolCatalog.ListFiles, builder.ToString().TrimEnd(), truncated);
    }

    /// <summary>Reads a text file from inside the workspace.</summary>
    public async Task<ToolResult> ReadFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return ToolResult.Failure(ToolCatalog.ReadFile, "filePath is required.");
        }

        var root = await RequireRootAsync(cancellationToken);
        var path = WorkspaceGuard.Resolve(root, filePath);

        if (!File.Exists(path))
        {
            return ToolResult.Failure(ToolCatalog.ReadFile, $"'{filePath}' does not exist inside the workspace.");
        }

        var info = new FileInfo(path);
        if (info.Length > MaxReadChars)
        {
            return ToolResult.Failure(
                ToolCatalog.ReadFile,
                $"'{filePath}' is {info.Length} bytes, larger than the {MaxReadChars} byte read limit.");
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            var truncated = content.Length > MaxReadChars;

            if (truncated)
            {
                content = content[..MaxReadChars];
            }

            _logger.LogInformation("read_file {Path} ({Bytes} bytes)", WorkspaceGuard.ToRelative(root, path), info.Length);
            return ToolResult.Success(ToolCatalog.ReadFile, content, truncated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Failure(ToolCatalog.ReadFile, $"Could not read '{filePath}': {exception.Message}");
        }
    }

    /// <summary>Writes (or overwrites) a text file, creating parent folders inside the workspace.</summary>
    public async Task<ToolResult> WriteFileAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return ToolResult.Failure(ToolCatalog.WriteFile, "filePath is required.");
        }

        content ??= string.Empty;

        if (content.Length > MaxWriteChars)
        {
            return ToolResult.Failure(
                ToolCatalog.WriteFile,
                $"Refused to write {content.Length} characters; the limit is {MaxWriteChars}.");
        }

        var root = await RequireRootAsync(cancellationToken);
        var path = WorkspaceGuard.Resolve(root, filePath);

        if (Directory.Exists(path))
        {
            return ToolResult.Failure(ToolCatalog.WriteFile, $"'{filePath}' is a directory.");
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                // Re-validate: a symlinked parent must not become a way out of the workspace.
                WorkspaceGuard.Resolve(root, directory);
                Directory.CreateDirectory(directory);
            }

            var existed = File.Exists(path);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

            var relative = WorkspaceGuard.ToRelative(root, path);
            _logger.LogInformation("write_file {Path} ({Chars} chars, existed: {Existed})", relative, content.Length, existed);

            return ToolResult.Success(
                ToolCatalog.WriteFile,
                $"{(existed ? "Overwrote" : "Created")} {relative} ({content.Length} characters).");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Failure(ToolCatalog.WriteFile, $"Could not write '{filePath}': {exception.Message}");
        }
    }

    /// <summary>Runs a terminal command with the workspace as the working directory.</summary>
    public async Task<ToolResult> RunCommandAsync(
        string command,
        CancellationToken cancellationToken = default,
        int timeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return ToolResult.Failure(ToolCatalog.RunCommand, "command is required.");
        }

        var root = await RequireRootAsync(cancellationToken);
        var trimmed = command.Trim();

        try
        {
            CommandGuard.Validate(trimmed, root);
        }
        catch (WorkspaceViolationException exception)
        {
            _logger.LogWarning("Blocked command: {Command}", trimmed);
            return ToolResult.Failure(ToolCatalog.RunCommand, exception.Message);
        }

        var timeout = Math.Clamp(timeoutSeconds, 5, MaxCommandTimeoutSeconds);

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (OperatingSystem.IsWindows())
        {
            // chcp 65001 so cmd emits UTF-8; /d skips AutoRun scripts.
            startInfo.Arguments = $"/d /s /c \"chcp 65001 >nul & {trimmed}\"";
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(trimmed);
        }

        startInfo.Environment["AICEO_WORKSPACE"] = root;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeout));

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return ToolResult.Failure(ToolCatalog.RunCommand, "The command could not be started.");
            }

            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
                {
                    // Already gone.
                }
            }

            var stdout = await SafeResultAsync(stdoutTask);
            var stderr = await SafeResultAsync(stderrTask);
            var exitCode = timedOut ? -1 : process.ExitCode;

            var builder = new StringBuilder();
            builder.AppendLine($"command: {trimmed}");
            builder.AppendLine($"exit code: {exitCode}{(timedOut ? " (timed out)" : string.Empty)}");

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                builder.AppendLine("--- stdout ---");
                builder.AppendLine(stdout.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                builder.AppendLine("--- stderr ---");
                builder.AppendLine(stderr.TrimEnd());
            }

            var text = builder.ToString().TrimEnd();
            var truncated = text.Length > MaxCommandOutputChars;
            if (truncated)
            {
                text = text[..MaxCommandOutputChars] + "\n…(output truncated)";
            }

            if (timedOut)
            {
                return ToolResult.Failure(ToolCatalog.RunCommand, text + $"\nThe command exceeded {timeout}s and was stopped.");
            }

            // A non-zero exit code is reported as content, not as a tool failure: it is information
            // the agent needs, e.g. a compiler telling it what to fix.
            return ToolResult.Success(ToolCatalog.RunCommand, $"{text}\n(exit code {exitCode})", truncated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "run_command failed: {Command}", trimmed);
            return ToolResult.Failure(ToolCatalog.RunCommand, $"Could not run the command: {exception.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>Dispatches a model tool call by name.</summary>
    public async Task<ToolResult> ExecuteAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        var tool = ToolCatalog.Normalize(toolName);
        if (tool is null)
        {
            return ToolResult.Failure(toolName ?? "(unknown)", $"Unknown tool '{toolName}'.");
        }

        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ToolResult.Failure(tool, "The tool arguments were not valid JSON.");
        }

        try
        {
            return tool switch
            {
                ToolCatalog.ListFiles => await ListFilesAsync(GetString(arguments, "directoryPath") ?? GetString(arguments, "path"), cancellationToken),
                ToolCatalog.ReadFile => await ReadFileAsync(GetString(arguments, "filePath") ?? GetString(arguments, "path") ?? string.Empty, cancellationToken),
                ToolCatalog.WriteFile => await WriteFileAsync(
                    GetString(arguments, "filePath") ?? GetString(arguments, "path") ?? string.Empty,
                    GetString(arguments, "content") ?? string.Empty,
                    cancellationToken),
                ToolCatalog.RunCommand => await RunCommandAsync(GetString(arguments, "command") ?? string.Empty, cancellationToken),
                _ => ToolResult.Failure(tool, $"Unknown tool '{toolName}'.")
            };
        }
        catch (WorkspaceViolationException exception)
        {
            // The guardrail fired: this is a security-relevant event worth a log line.
            _logger.LogWarning("Workspace guardrail blocked {Tool}: {Message}", tool, exception.Message);
            return ToolResult.Failure(tool, exception.Message);
        }
    }

    private async Task<string> RequireRootAsync(CancellationToken cancellationToken)
    {
        var root = await _workspace.GetRootAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw WorkspaceViolationException.NoWorkspace();
        }

        return root;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }

    private static async Task<string> SafeResultAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
