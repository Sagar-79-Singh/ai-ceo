# AI CEO Orchestrator

A Star-topology AI orchestrator: **you talk only to the CEO agent**, the CEO plans and delegates
sequentially to worker agents, workers report only to the CEO, and the CEO writes the answer you
read. Built as a single ASP.NET Core Web App (Blazor Server) on **.NET 10** with
**Entity Framework Core + SQLite** and the official **`OpenAI` .NET client** pointed at a local
**OmniRoute** gateway.

> The brief asked for .NET 10, so `net10.0` is the target (the architecture doc's ".NET 8" line is
> superseded by the build request). Everything else follows `docs/PRD.md`, `docs/ARCHITECTURE.md`,
> `docs/RULES.md` and `docs/UI_SPEC.md`.

---

## Rules this implementation enforces

| Rule | Where |
| --- | --- |
| User ↔ CEO only | `Services/AgentOrchestrator.cs` (the only entry point is the CEO) |
| CEO plans, workers execute, CE0 synthesizes | `BuildPlanAsync` → `foreach` delegations → `SynthesizeAsync` |
| Strictly sequential workers (no `Task.WhenAll`, no `Parallel`) | plain `foreach` in `RunAsync` |
| Strict JSON parsing with graceful failure | `Services/TaskPlanParser.cs` + fallback plan |
| No agent framework / no Python / no Node | custom C# orchestrator only |
| All LLM calls via OmniRoute | `Services/OmniRouteChatService.cs` |
| EF Core + SQLite, code-first | `Data/AppDbContext.cs`, `Data/DbSeeder.cs` |

### The one LLM entry point

```csharp
var client = new OpenAIClient(
    new ApiKeyCredential(Environment.GetEnvironmentVariable("OMNIROUTE_KEY")
        ?? "sk-48f9a7b2051199e0-64bc90-6a0ec9ee"),
    new OpenAIClientOptions() { Endpoint = new Uri("http://localhost:20128/v1") }
);
```

`OmniRouteChatService` builds exactly this client (endpoint/key overridable through
`OmniRoute:Endpoint`, `OmniRoute:ApiKey` or `OMNIROUTE_KEY`) and calls
`GetChatClient(modelId).CompleteChatAsync(...)`. No other code talks to an LLM.

---

## Run it

```bash
# 1. Start OmniRoute on http://localhost:20128/v1
# 2. Then:
dotnet run
```

The app listens on <http://localhost:5088> (override with `ASPNETCORE_URLS`).
On first start it creates the SQLite database and seeds a CEO (**Atlas**) plus three workers
(**Scout** researcher, **Vector** analyst, **Quill** writer).

### Configuration

| Setting | Default | Notes |
| --- | --- | --- |
| `OmniRoute:Endpoint` | `http://localhost:20128/v1` | OmniRoute gateway base URL |
| `OMNIROUTE_KEY` (env) | `OmniRoute:ApiKey` → built-in local key | Win over everything else |
| `OmniRoute:ModelId` | `gh/gpt-5.4-nano` | Default in the "Create agent" modal |
| `OmniRoute:TimeoutSeconds` | `300` | Per-call ceiling |
| `OmniRoute:MaxOutputTokens` | `0` (gateway default) | Optional cap |
| `AICEO_DB_PATH` (env) / `Database:Path` | `%LOCALAPPDATA%\AiCeoOrchestrator\aiceo.db` | SQLite file |

User data lives outside the install folder on purpose: the published release is a single `.exe`
that may sit in a read-only directory.

### UI (Blazor Server, dark by default)

- **Sidebar (25%)** — agent list (name, role, `CEO` badge, model), `＋ New` modal with
  Name / Role / System Prompt / Model ID, edit, delete-with-inline-confirm, and "make CEO".
- **Main feed (75%)** — chat-style: your prompt, the CEO's plan, `[Researcher] is thinking…`
  delegations, worker results, errors, and the final CEO answer.
- Live streaming of every step through an orchestrator callback; `Ctrl`+`Enter` sends,
  **Stop** cancels a run.

### REST API

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/health` | OmniRoute reachability |
| `GET` | `/api/agents` | list agents |
| `POST` | `/api/agents` | create agent |
| `PUT` | `/api/agents/{id}` | update agent |
| `DELETE` | `/api/agents/{id}` | delete agent |
| `POST` | `/api/agents/{id}/manager` | promote to CEO |
| `GET` | `/api/tasks` | last 25 runs |
| `GET` | `/api/tasks/{id}` | one run + logs |
| `POST` | `/api/task` | run the CEO: `{ "prompt": "..." }` |

```bash
curl -X POST http://localhost:5088/api/task \
  -H "Content-Type: application/json" \
  -d '{"prompt":"Research and recommend a pricing model for a desktop AI notes app"}'
```

---

## Database

Code-first EF Core. The schema mirrors `docs/ARCHITECTURE.md`; the run entity is named
`TaskItem` (table **`Tasks`**) only to avoid colliding with `System.Threading.Tasks.Task`.

```
Tasks        Id, UserPrompt, CeoPlan (JSON), FinalOutput, CreatedAt
TaskLogs     Id, TaskId, AgentName, Action (plan|delegated|result|error), Content, CreatedAt
Agents       Id, Name, Role, SystemPrompt, ModelId, IsManager
```

The database is created automatically at startup. To use migrations instead:

```bash
dotnet ef migrations add InitialCreate
dotnet ef database update
```

`DbSeeder` runs `Migrate()` when migrations exist and `EnsureCreated()` otherwise.

---

## Publish a Windows executable

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

The working directory gets `publish/AiCeo.exe`. Double-click it (or run it) and browse to
<http://localhost:5088>.

### GitHub Releases

`.github/workflows/release.yml` runs on **push to `main`** or on a **`v*` tag** (and manually via
`workflow_dispatch`):

1. `windows-latest`, installs the .NET 10 SDK,
2. restores + builds,
3. runs the exact publish command above,
4. verifies `./publish/AiCeo.exe` exists,
5. creates a GitHub Release with `softprops/action-gh-release@v2` and uploads `AiCeo.exe`.

Main pushes get an automatic tag `v1.0.<run number>`; pushing a tag like `v1.2.0` releases that tag.

`.github/workflows/ci.yml` builds every branch/PR and smoke-tests the published binary
(the page must return 200 and `_framework/blazor.web.js` must be served).

---

## Project layout

```
AiCeo.csproj                     .NET 10 web SDK, EF Core SQLite, OpenAI
Program.cs                       host, DI, DB bootstrap, Blazor + REST endpoints
Data/                            AppDbContext, seeder, SQLite path resolution, EF design-time factory
Models/                          Agent, TaskItem, TaskLog, PlanStep, log actions, form models
Options/OmniRouteOptions.cs      gateway endpoint/key/model + defaults from RULES.md
Services/                        OmniRoute client, orchestrator, plan parser, agent CRUD
Components/App.razor             HTML shell + dark theme CSS + blazor.web.js
Components/Pages/Home.razor      sidebar + execution feed + composer
Components/Shared/               AgentEditor modal, ExecutionFeed renderer
.github/workflows/               release.yml (Windows .exe release), ci.yml (build + smoke test)
```

## Notes

- All LLM traffic goes to the local gateway; the fallback key is the one specified in
  `docs/RULES.md` and is only meaningful for that local gateway.
- The UI holds no conversation state across runs: each run persists a `Tasks` row plus an
  ordered `TaskLogs` trail, so the feed is fully reproducible from the database.
- There is no telemetry, no background worker, and no external service beyond OmniRoute.
