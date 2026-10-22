# Architecture

## Tech Stack
- **Backend**: C# (.NET 8) + ASP.NET Core Web API.
- **Frontend**: Razor Pages or Blazor Server (keep it simple, single project).
- **Database**: SQLite with Entity Framework Core (Code-First).
- **LLM SDK**: `OpenAI-DotNet` NuGet package (pointed to OmniRoute).
- **Agent Logic**: Custom C# orchestrator service (No Python, No Node.js).

## Database Schema (Entity Framework Core)
```csharp
public class Agent {
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Role { get; set; }
    public string SystemPrompt { get; set; }
    public string ModelId { get; set; } // e.g., "gh/gpt-5.4-nano"
    public bool IsManager { get; set; }
}

public class Task {
    public Guid Id { get; set; }
    public string UserPrompt { get; set; }
    public string CeoPlan { get; set; } // JSON string
    public string FinalOutput { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<TaskLog> Logs { get; set; }
}

public class TaskLog {
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string AgentName { get; set; }
    public string Action { get; set; } // "delegated", "result", "error"
    public string Content { get; set; }
    public DateTime CreatedAt { get; set; }
}

Execution Flow
POST /api/task with { prompt: "..." }.

Backend retrieves CEO Agent from DB.

CEO LLM generates a JSON array: [{ "role": "Researcher", "instructions": "..." }].

Backend iterates sequentially using a foreach loop (No Parallel tasks).

For each item, call the matching Worker Agent's LLM and save to TaskLog.

Feed all Worker outputs back to CEO LLM for final synthesis.

Return final output to UI.


---
