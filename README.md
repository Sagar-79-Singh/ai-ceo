# AI CEO Orchestrator · v1.0.2

A Star-topology AI orchestrator in a single ASP.NET Core app (.NET 10, Blazor Server):
**you talk only to the CEO**, the CEO plans and delegates **sequentially** to worker agents,
workers report only to the CEO, and the CEO writes the answer you read.

Every agent carries its own provider: **OmniRoute**, **OpenAI**, **Gemini**, or any **Custom**
OpenAI-compatible base URL (Ollama, vLLM, Azure proxy…). One `OpenAI` .NET client drives all four.

---

## Star topology (enforced, not just documented)

```
User ──► CEO ──► Worker A ──► CEO ──► Worker B ──► CEO ──► User
```

| Rule | Implementation |
| --- | --- |
| Only the CEO sees the user prompt | `AgentOrchestrator.RunAsync` is the only entry point |
| CEO plans, workers execute, CEO synthesizes | `BuildPlanAsync` → `foreach` delegations → `SynthesizeAsync` |
| Strictly sequential — no parallel workers | a plain `foreach` (no `Task.WhenAll`, no `Parallel.ForEachAsync`); a test asserts max concurrency is 1 |
| Workers never talk to each other | each worker gets only its own persona + its own assignment (+ read-only prior *results*) |
| Workers cannot spawn tasks | workers have no orchestration tools; the loop is the only scheduler |
| Plans are parsed strictly | `System.Text.Json` in `TaskPlanParser`, fenced/prose tolerant, graceful fallback |
| No Python / Node / LangChain | 100% C#, no external agent framework |

## Multi-provider support (v1.0.2)

| Provider | Base URL | Key source | Default model |
| --- | --- | --- | --- |
| **OmniRoute** | `http://localhost:20128/v1` | `OMNIROUTE_KEY` → `OmniRoute:ApiKey` → local fallback | `gh/gpt-5.4-nano` |
| **OpenAI** | `https://api.openai.com/v1` | agent key → `OPENAI_API_KEY` | `gpt-4o-mini` |
| **Gemini** | `https://generativelanguage.googleapis.com/v1beta/openai` | agent key → `GEMINI_API_KEY` | `gemini-2.5-flash` |
| **Custom** | user supplied (required) | agent key → `CUSTOM_API_KEY` (optional) | user supplied |

Provider defaults live in `Models/LlmProvider.cs` (`LlmProviderCatalog`), so the UI, the API and
the resolver never disagree. All calls go through `LlmGateway` (`Services/LlmGateway.cs`), which
builds an `OpenAIClient` per `(baseUrl, key)` pair:

```csharp
new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
);
```

**API keys never reach the browser.** `GET /api/agents` returns an `AgentView` with a mask
(`sk-1…cdef`) and the key *source*; saving with a blank key keeps the stored secret.

## Database (EF Core + SQLite)

```
Agents     Id, Name, Role, SystemPrompt, ModelId, Provider, BaseUrl, ApiKey, IsManager
Tasks      Id, UserPrompt, CeoPlan (JSON), FinalOutput, CreatedAt
TaskLogs   Id, TaskId, AgentName, Action (plan|delegated|result|error), Content, CreatedAt
```

`Provider` is stored as text (`OmniRoute`, `OpenAI`, `Gemini`, `Custom`) so the SQLite file stays
readable. A database created by v1.0.x is upgraded in place on first start —
`Data/SchemaUpgrader.cs` adds the three new columns with `ALTER TABLE` and keeps your agents.

## UI (Blazor Server, dark)

- **Sidebar (25%)** — agents with a **CEO** badge, a provider badge, model id and key status.
  `＋ Create agent` opens a modal with **Name**, **Role**, **System Prompt**, **Provider**
  dropdown (OpenAI / OmniRoute / Gemini / Custom), **Base URL**, **API Key** and **Model ID**.
  Switching provider swaps in that provider's default URL and model without clobbering your edits.
- **Main feed (75%)** — your prompt, the CEO's plan, `[Researcher] is thinking…` delegations,
  each worker result, errors, then the final synthesized answer.
- Provider health badge in the sidebar probes the CEO's endpoint server-side.

## REST API

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/providers` | provider catalogue (defaults, env vars) |
| `GET` | `/api/health` | health of the CEO's provider |
| `GET` | `/api/agents` | agents (keys masked) |
| `POST` | `/api/agents` | create agent |
| `PUT` | `/api/agents/{id}` | update agent (blank key keeps the stored one) |
| `DELETE` | `/api/agents/{id}` | delete agent |
| `POST` | `/api/agents/{id}/manager` | promote to CEO |
| `GET` | `/api/agents/{id}/health` | probe one agent's provider |
| `GET` | `/api/tasks` · `/api/tasks/{id}` | recent runs / one run with logs |
| `POST` | `/api/task` | run the CEO: `{ "prompt": "..." }` |
| `GET` | `/api/diagnostics` | paths, version, embedded assets |

```bash
curl -X POST http://localhost:5088/api/task \
  -H "Content-Type: application/json" \
  -d '{"prompt":"Research and recommend a pricing model for a desktop AI notes app"}'
```

---

## Run locally

```bash
dotnet run
# → http://localhost:5088
```

First start creates the SQLite file and seeds a CEO (**Atlas**) plus **Scout** (researcher),
**Vector** (analyst) and **Quill** (writer), all on OmniRoute. Edit any of them to switch provider.

| Setting | Default |
| --- | --- |
| `OmniRoute:Endpoint` | `http://localhost:20128/v1` |
| `OmniRoute:TimeoutSeconds` | `300` |
| `OmniRoute:MaxOutputTokens` | `0` (provider default) |
| `AICEO_DB_PATH` | `%LOCALAPPDATA%\AiCeoOrchestrator\aiceo.db` |
| `ASPNETCORE_URLS` | `http://localhost:5088` |
| `AICEO_NO_BROWSER` | unset (auto-opens the browser on Windows) |

## Tests

```bash
dotnet test tests/AiCeo.Tests/AiCeo.Tests.csproj
```

Covers: plan parsing (prose/fences/garbage/caps), provider defaults and key precedence, key
masking, the sequential topology (max 1 concurrent call, ordering plan → workers → synthesis,
worker isolation), worker failure isolation, the JSON fallback path, agent CRUD keep/clear key
semantics, and the v1.0.x → v1.0.2 schema upgrade.

## Releases (7 platforms, tag `v1.0.2`)

`.github/workflows/release.yml` runs on every push to `main` (and on `v*` tags / manual dispatch):

1. **Matrix build** — `win-x64`, `win-x86`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`,
   `osx-arm64`, each with:
   ```bash
   dotnet publish -c Release -r <RID> --self-contained true \
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
   ```
2. Each binary is renamed `AiCeo-<RID>[.exe]`, marked executable on Unix, checksummed and
   uploaded as an artifact.
3. A single **release job** downloads all seven, asserts none is missing, writes `SHA256SUMS.txt`
   and publishes the GitHub Release **tagged `v1.0.2`** with every binary attached.

`.github/workflows/ci.yml` runs the unit tests, boots the published single-file binary twice
(in place, and copied alone into a foreign directory), verifies `/api/providers` and that no key
leaks through `/api/agents`, upgrades a real v1.0.x SQLite database, and dry-runs the publish for
all seven RIDs so a broken platform shows up before a release, not during one.

## Project layout

```
AiCeo.csproj                     .NET 10 web SDK, EF Core SQLite, OpenAI
Program.cs                       host, DI, DB bootstrap, Blazor + REST endpoints
Data/                            AppDbContext, seeder, SchemaUpgrader, paths, design-time factory
Models/                          Agent, AgentView, LlmProvider(+catalog), TaskItem, TaskLog, PlanStep
Options/OmniRouteOptions.cs      gateway endpoint/key/timeouts
Services/                        LlmGateway, LlmConnections, AgentOrchestrator, TaskPlanParser,
                                 AgentDirectory, AgentHealthService, FrameworkAssetStore
Components/                      dark shell (App.razor), Home.razor, AgentEditor, ExecutionFeed
tests/AiCeo.Tests/               xunit suite (topology, providers, parsing, migration)
.github/workflows/               release.yml (7-RID matrix → v1.0.2), ci.yml
```
