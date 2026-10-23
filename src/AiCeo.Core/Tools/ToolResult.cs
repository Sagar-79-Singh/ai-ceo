using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCeo.Tools;

/// <summary>Outcome of one tool call. Always serialisable back to the model as text.</summary>
public sealed class ToolResult
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("tool")]
    public string Tool { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    public static ToolResult Success(string tool, string content, bool truncated = false)
        => new() { Ok = true, Tool = tool, Content = content, Truncated = truncated };

    public static ToolResult Failure(string tool, string error)
        => new() { Ok = false, Tool = tool, Content = error, Error = error };

    /// <summary>What the model sees as the <c>tool</c> message content.</summary>
    public string ToToolContent()
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);

        // Keep tool messages bounded: a huge file read must not blow the context window.
        const int max = 24_000;
        return json.Length <= max ? json : json[..max] + "…(tool output truncated)";
    }

    public override string ToString() => ToToolContent();
}
