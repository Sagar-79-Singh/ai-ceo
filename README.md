# AI CEO Orchestrator

A **Windows desktop** AI orchestrator written in **C# / .NET 10**. It opens in its own native
window — no browser, no local web server — and runs a strict star topology of agents that can
read, write and build inside a folder **you** choose.

```
You  ──▶  CEO  ──▶  worker 1  ──▶  CEO  ──▶  worker 2  ──▶  CEO  ──▶  You
                 (one at a time, never peer-to-peer)
```

---

## What's new in v1.0.3

| Area | Change |
| --- | --- |
| **Desktop app** | .NET **MAUI Blazor Hybrid** (`net10.0-windows10.0.19041.0`). The Blazor UI renders inside a `BlazorWebView` in a WinUI window. The Kestrel web host, `Program.cs` and the minimal-API endpoints are gone. |
| **Installer** | `release.yml` builds a Windows **`.msix`** package (plus a portable `.zip`) on `windows-latest` and attaches both, with SHA-256 checksums, to the GitHub Release. |
| **Local workspace** | A **Select Workspace Folder** button in the sidebar opens the native OS folder picker and stores the chosen path in SQLite (single row, `WorkspaceSettings`). |
| **Tool calling** | Workers marked *tools* get four function-calling tools: `list_files`, `read_file`, `write_file`, `run_command`. |
| **Security guardrail** | **Every** file path and every terminal path token is validated against the workspace root before anything touches the disk. Anything outside is refused. |
| **Agentic loop** | A tool-enabled worker iterates (inspect → write → build → read errors → fix) for up to 8 rounds / 24 tool calls, then reports a plain-text result to the CEO, which writes the final answer. |

---

## Install

1. Open the **Releases** page and download `AI-CEO-Orchestrator-<version>-win-x64.msix`.
2. Double-click it → **Install**. The build is not code-signed, so Windows may show a SmartScreen
   warning: **More info → Install anyway**.
3. Launch **AI CEO Orchestrator** from the Start menu.
4. In the sidebar, click **Select Workspace Folder** and choose the folder agents may work in.

Prefer no installer? Download the `-portable.zip`, unzip it anywhere and run `AiCeo.Maui.exe`.

**Requirements:** Windows 10 1809 (build 17763) or newer, x64.

---

## How a run works

1. **You → CEO.** Your prompt goes to the agent marked `CEO`. Nothing else sees it first.
2. **CEO plans.** The CEO returns a JSON plan: 1–6 sequential assignments, each with a role from
   the roster. The plan is shown in the execution feed.
3. **CEO → one worker at a time.** Each assignment is delegated with a `foreach` — there is no
   `Task.WhenAll` and no `Parallel.ForEachAsync` anywhere in the codebase (CI greps for them).
   A worker never sees another worker's conversation, only the finished text of earlier steps.
4. **Workers work.** A plain worker answers in one round trip. A **tool-enabled** worker enters the
   agentic loop: the model asks for a tool, the tool runs, the result is fed back, and the loop
   continues until the model stops asking and writes a summary.
5. **CEO synthesizes.** All worker outputs go back to the CEO, which resolves conflicts and writes
   the answer you see. If a worker fails, the run continues and the CEO reports the gap.

Only the CEO is ever shown to you. Workers have no channel to each other and cannot create tasks.

---

## The workspace and the guardrail

Selecting a folder stores its absolute path in SQLite (`WorkspaceSettings`, row id 1). Every tool
call is then gated by `WorkspaceGuard`:

| Attack | Defence |
| --- | --- |
| `../secrets.txt`, `..\..\Windows\System32` | `Path.GetFullPath` normalises first; the result must be inside the root |
| `C:\Windows\win.ini`, `/etc/passwd` | Absolute paths are accepted only if they are under the root |
| `C:\Work` vs `C:\WorkspaceEvil` | Prefix check uses `root + separator`, never a bare string prefix |
| Mixed separators `sub\..\..\x` | Normalised before the check |
| Symlink/junction inside the workspace pointing out | `ResolveLinkTarget` walks to the final target and re-validates |
| `run_command` with an outside path | Every absolute path token in the command is resolved and checked |
| Destructive commands (`format`, `diskpart`, `shutdown`, `reg add`, `mkfs`, `rm -rf /`, `curl … \| sh`, …) | Denylist in `CommandGuard` |

`run_command` always executes with the **workspace as the working directory** and a 90 s timeout
(300 s max); stdout, stderr and the exit code come back to the model, so a compiler error is
information the agent can act on.

> Honest limits: a shell can do what its user can do. `CommandGuard` is a strong safety net, not an
> OS sandbox. Agents run as you, so treat the workspace you pick as the blast radius.

Toggle everything off at any time with *Allow agents to use files & terminal* in the sidebar.

---

## Tools exposed to the model

| Tool | Arguments | Behaviour |
| --- | --- | --- |
| `list_files` | `directoryPath` (optional) | Lists files/folders, workspace-relative, up to 300 entries |
| `read_file` | `filePath` | Reads a UTF-8 text file (200 000 character ceiling) |
| `write_file` | `filePath`, `content` | Creates/overwrites, creating parent folders (2 000 000 character ceiling) |
| `run_command` | `command` | Runs in the workspace via `cmd.exe` (or `/bin/sh`), captures stdout/stderr/exit code |

Function calling uses the official [`OpenAI`](https://github.com/openai/openai-dotnet) .NET client:
`ChatTool.CreateFunctionTool`, `ChatCompletionOptions.Tools`, `ChatFinishReason.ToolCalls`,
`AssistantChatMessage(toolCalls)` and `ToolChatMessage(toolCallId, content)`.

---

## Providers

Every backend is OpenAI-compatible, so one client drives all of them; each agent carries its own
`Provider`, `BaseUrl`, `ApiKey` and `ModelId`.

| Provider | Base URL | Key environment variable |
| --- | --- | --- |
| **OmniRoute** (default) | `http://localhost:20128/v1` | `OMNIROUTE_KEY` |
| **OpenAI** | `https://api.openai.com/v1` | `OPENAI_API_KEY` |
| **Gemini** | `https://generativelanguage.googleapis.com/v1beta/openai` | `GEMINI_API_KEY` |
| **Custom** | any base URL you supply | `CUSTOM_API_KEY` |

Key precedence: the agent's stored key → the provider environment variable → (OmniRoute only) the
configured/fallback key. API keys are **never** sent to the UI or an API: `AgentView` exposes only a
mask such as `sk-1…cdef` plus where the key came from (`stored`, `env:OPENAI_API_KEY`, …).

---

## Repository layout

```
src/AiCeo.Core/            portable class library (no UI)
  Models/                  Agent, AgentView, TaskItem, TaskLog, WorkspaceSettings, …
  Data/                    AppDbContext, DbSeeder, SchemaUpgrader, DatabasePaths
  Services/                AgentOrchestrator, LlmGateway, AgentDirectory, WorkspaceService, …
  Tools/                   WorkspaceGuard, CommandGuard, ToolCatalog, ToolService, ToolResult
src/AiCeo.Maui/            .NET MAUI Blazor Hybrid desktop head (Windows)
  MainPage.xaml            the BlazorWebView that hosts the UI
  App.xaml(.cs)            Window: 1440×940 native window
  MauiProgram.cs           DI + database bootstrap
  Services/                LocalFolderPicker (native folder picker)
  Components/              Main.razor (router), Pages/Home.razor, Shared/AgentEditor.razor, …
  wwwroot/                 index.html, css/app.css, js/app.js
tests/AiCeo.Tests/         xunit: topology, guardrail, tools, schema upgrade, provider resolution
```

### Database

SQLite, created on first launch in the app's private data folder
(`%LOCALAPPDATA%\Packages\com.aiceo.orchestrator\LocalState\aiceo.db` for the MSIX build).
Override with the `AICEO_DB_PATH` environment variable.

`SchemaUpgrader` runs on every start and is idempotent:

- v1.0.1 → v1.0.2: adds `Agents.Provider`, `Agents.BaseUrl`, `Agents.ApiKey`
- v1.0.2 → v1.0.3: adds `Agents.CanUseTools`, `TaskLogs.ToolName`, creates `WorkspaceSettings`

Existing agents, tasks and logs are preserved.

---

## Build from source

```powershell
# prerequisites: .NET 10 SDK
dotnet workload install maui-windows

dotnet restore src/AiCeo.Maui/AiCeo.Maui.csproj -r win-x64
dotnet build   src/AiCeo.Maui/AiCeo.Maui.csproj -c Release -r win-x64

# tests (portable core, any OS)
dotnet test tests/AiCeo.Tests/AiCeo.Tests.csproj

# the installer
dotnet publish src/AiCeo.Maui/AiCeo.Maui.csproj -c Release -r win-x64 `
  -p:WindowsPackageType=MSIX -p:AppxPackageSigningEnabled=false `
  -p:GenerateAppxPackageOnBuild=true -o ./artifacts/msix
```

MAUI cannot cross-compile to Windows from Linux or macOS: the desktop head and the MSIX are built on
`windows-latest`, which is exactly what CI does.

### CI / release

- `ci.yml` — core library + unit tests on Linux, plus the real Windows desktop build and MSIX
  packaging on `windows-latest`, and a grep that fails the build if `Task.WhenAll`,
  `Task.WhenAny` or `Parallel.ForEachAsync` ever appear in the core.
- `release.yml` — on a `v*` tag: MAUI workload, MSIX + portable zip, SHA-256 checksums, GitHub
  Release with install instructions.

---

## Default team

Seeded on first run (edit or delete any of them in the sidebar):

| Agent | Role | Tools |
| --- | --- | --- |
| **Atlas** | Chief Executive Officer | — (the CEO coordinates only) |
| **Scout** | Researcher | — |
| **Vector** | Analyst | — |
| **Quill** | Writer | — |
| **Forge** | Software Engineer | files + terminal |

---

## Design notes

- **C# only.** No Python, no Node.js, no LangChain/CrewAI — the orchestration, the plan parser and
  the tool loop are ~600 lines of straight C#.
- **One chokepoint.** All path decisions live in `WorkspaceGuard`; all command decisions live in
  `CommandGuard`; all tool dispatch lives in `ToolService`. Nothing else opens a file or a process.
- **Bounded.** Tool rounds, tool calls, file sizes, command output, command time and list sizes all
  have hard ceilings; the transcript is trimmed without orphaning tool messages.
- **Testable core.** `ILlmGateway` is faked in tests, so topology, sequencing, the tool loop and the
  guardrail are asserted without a network or a provider key.
