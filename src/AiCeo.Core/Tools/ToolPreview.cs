using System.Text;

namespace AiCeo.Tools;

/// <summary>
/// Builds the text the v2.0.0 approval card shows: a line diff for <c>write_file</c> and the command
/// itself for <c>run_command</c>.
///
/// The diff is deliberately a review preview, not a patch format: unchanged head and tail are
/// trimmed, the changed block is marked <c>-</c> / <c>+</c>, and everything is bounded so a 2 MB
/// file cannot flood the web-message channel.
/// </summary>
public static class ToolPreview
{
    /// <summary>Lines kept in the changed block.</summary>
    public const int MaxChangedLines = 60;

    /// <summary>Context lines kept above and below the change.</summary>
    public const int ContextLines = 3;

    /// <summary>Longest single line rendered; the rest is clipped.</summary>
    public const int MaxLineLength = 240;

    /// <summary>Files longer than this are summarised instead of diffed line by line.</summary>
    public const int MaxDiffableLines = 4_000;

    /// <summary>Preview for <c>run_command</c>: the command, quoted for review.</summary>
    public static string ForCommand(string command)
        => "$ " + Clip((command ?? string.Empty).Trim());

    /// <summary>Preview for <c>write_file</c>: a new file or a diff against what is on disk.</summary>
    public static string ForWrite(string path, string? existing, string content)
    {
        var incoming = SplitLines(content ?? string.Empty);

        if (existing is null)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"new file · {incoming.Count} line{(incoming.Count == 1 ? string.Empty : "s")}");

            foreach (var line in incoming.Take(MaxChangedLines))
            {
                builder.Append("+ ").AppendLine(line);
            }

            if (incoming.Count > MaxChangedLines)
            {
                builder.AppendLine($"… {incoming.Count - MaxChangedLines} more lines not shown");
            }

            return builder.ToString().TrimEnd();
        }

        var previous = SplitLines(existing);

        if (previous.Count > MaxDiffableLines || incoming.Count > MaxDiffableLines)
        {
            return $"{path}: {previous.Count} lines on disk → {incoming.Count} lines after this write "
                + $"(too large for a line-by-line preview; the whole file is replaced).";
        }

        return BuildDiff(previous, incoming, path);
    }

    /// <summary>Common head/tail trimming plus a bounded changed block.</summary>
    private static string BuildDiff(IReadOnlyList<string> previous, IReadOnlyList<string> incoming, string path)
    {
        var head = 0;
        while (head < previous.Count && head < incoming.Count && previous[head] == incoming[head])
        {
            head++;
        }

        if (head == previous.Count && head == incoming.Count)
        {
            return $"{path}: unchanged ({incoming.Count} lines).";
        }

        var tail = 0;
        while (tail < previous.Count - head
               && tail < incoming.Count - head
               && previous[previous.Count - 1 - tail] == incoming[incoming.Count - 1 - tail])
        {
            tail++;
        }

        var removed = previous.Skip(head).Take(previous.Count - head - tail).ToList();
        var added = incoming.Skip(head).Take(incoming.Count - head - tail).ToList();

        var builder = new StringBuilder();
        builder.AppendLine($"{path} · -{removed.Count} +{added.Count} lines");

        var contextBefore = Math.Max(0, head - ContextLines);
        for (var index = contextBefore; index < head; index++)
        {
            builder.Append("  ").AppendLine(Clip(previous[index]));
        }

        foreach (var line in removed.Take(MaxChangedLines))
        {
            builder.Append("- ").AppendLine(Clip(line));
        }

        if (removed.Count > MaxChangedLines)
        {
            builder.AppendLine($"… {removed.Count - MaxChangedLines} removed lines not shown");
        }

        foreach (var line in added.Take(MaxChangedLines))
        {
            builder.Append("+ ").AppendLine(Clip(line));
        }

        if (added.Count > MaxChangedLines)
        {
            builder.AppendLine($"… {added.Count - MaxChangedLines} added lines not shown");
        }

        var contextAfterStart = incoming.Count - tail;
        for (var index = contextAfterStart; index < Math.Min(incoming.Count, contextAfterStart + ContextLines); index++)
        {
            builder.Append("  ").AppendLine(Clip(incoming[index]));
        }

        return builder.ToString().TrimEnd();
    }

    private static List<string> SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();

    private static string Clip(string line)
        => line.Length <= MaxLineLength ? line : line[..MaxLineLength] + " …";
}
