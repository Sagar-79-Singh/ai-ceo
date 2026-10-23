namespace AiCeo.Models;

/// <summary>Small immutable facts surfaced in the UI footer.</summary>
public sealed record AppRuntimeInfo(string DatabasePath, string Version);
