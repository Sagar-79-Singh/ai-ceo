namespace AiCeo.Models;

/// <summary>Body of <c>POST /api/task</c>: <c>{ "prompt": "..." }</c>.</summary>
public sealed record TaskRequest(string? Prompt);
