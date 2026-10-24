# AI CEO Orchestrator

A **cross-platform desktop** AI orchestrator written in **C# / .NET 10**, with an **Avalonia** shell
that hosts the **Nexus** interface: a live HTML/WebGL console rendered by the platform's own web
engine inside the app window — no browser tab, no local web server, no Node.js. It runs a strict star
topology of agents that can read, write and build inside a folder **you** choose. Windows users can
grab the optional `Setup.exe`, and the whole console can be re-themed (dark / light / system, five
accent colours, three text sizes) from the settings modal.

```
You  ──▶  CEO  ──▶  worker 1  ──▶  CEO  ──▶  worker 2  ──▶  CEO  ──▶  You
                 (one at a time, never peer-to-peer)
```

---

## What's new in v1.0.6 — Nexus

| Area | Change |
| --- | --- |
| **Nexus interface** | The stock Avalonia layout is replaced by the Nexus console: obsidian shell, swarm matrix on the left, execution feed in the middle with the gyroscope emblem, the breadcrumb `You → CEO → workers → CEO → You` and the neural-wave telemetry strip, and the glowing console dock across the bottom. |
| **Embedded HTML surface** | The page is rendered by the platform web engine through `Avalonia.Controls.WebView` 11.4.1 — Edge WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux — inside the same native window. |
| **Offline by design** | Tailwind (precompiled, ~120 KB) and the Font Awesome subset are bundled inside the executable and extracted next to the database on first launch. Nothing is fetched from a CDN, so the interface renders identically with the network unplugged; the Google Fonts link is a progressive enhancement over the system stack. |
| **Holographic shader** | The animated WebGL core (ring filaments, noise aura, grid, vignette) runs on a canvas behind the feed at 60 fps and follows the mouse. |
| **Wiring** | *Select Workspace* opens the native folder picker and updates the sidebar chip; *Dispatch Swarm* sends the directive to the CEO through the unchanged orchestrator; the swarm matrix can deploy, edit, delete and promote agents; the feed streams the real plan, delegations, tool calls and final answer. |
| **Settings modal** | Theme (Dark / Light / System), five accent colours and three text sizes, applied instantly in the page and in the Avalonia shell, persisted in the `UserSettings` table. |
| **Graceful fallback** | On a machine without a usable web engine the app opens the previous Avalonia interface and offers a one-click switch, so orchestration, agents and the sandbox never depend on the renderer. |
| **Installer** | `AI-CEO-Orchestrator-1.0.6-win-x64-Setup.exe` is rebuilt from `installer.iss` with the same dark modern wizard, refreshed for this version. |
| **Version** | `1.0.6` in both `.csproj` files, the assembly/file version and the installer metadata. |

### Older releases
- **v1.0.5** — custom dark Inno Setup installer, settings page (theme / accent / text size) persisted in `UserSettings`.
- **v1.0.4** — Avalonia UI replaces MAUI; Linux/macOS builds; single-file executables; `crash.log`.
- **v1.0.3** — MAUI Blazor Hybrid desktop app, local workspace, tool calling, guardrail, agentic loop.
- **v1.0.2** — multi-provider LLM layer and health badges.

---

## Install

Download one file from the **Releases** page and run it.

| Platform | File | How to run |
| --- | --- | --- |
| **Windows x64 (recommended)** | `AI-CEO-Orchestrator-1.0.6-win-x64-Setup.exe` | Double-click the installer. Per-user, no admin prompt, Start-menu + desktop shortcuts, clean uninstall. |
| **Windows x64 (portable)** | `AiCeo-win-x64.exe` | Double-click. No installer, no runtime to install. |
| **Windows x86** | `AiCeo-win-x86.exe` | For 32-bit Windows. |
| **Windows ARM64** | `AiCeo-win-arm64.exe` | Surface / Snapdragon PCs. |
| **Linux x64 / ARM64** | `AiCeo-linux-*.tar.gz` | `tar -xzf AiCeo-linux-x64.tar.gz && ./AiCeo` |
| **macOS Intel / Apple Silicon** | `AiCeo-osx-*.tar.gz` | `tar -xzf AiCeo-osx-arm64.tar.gz && ./AiCeo` |

Then, in the sidebar, click **Select Workspace Folder** and choose the folder agents may work in.

The binaries are self-contained (the .NET runtime is inside), so nothing else has to be installed.
They are not code-signed: on Windows choose **More info → Run anyway** if SmartScreen complains; on
macOS use right-click → **Open** the first time; on Linux `chmod +x AiCeo` if your tar extractor
dropped the bit.

**Requirements:** Windows 10/11 (x64, x86 or ARM64), a modern glibc Linux (x64 or ARM64), or macOS
13+ (Intel or Apple Silicon).

> **Windows 7 / 8 / 8.1:** the app itself has no Windows-specific dependencies any more (that was the
> MAUI crash), but .NET 10 — the runtime baked into the executable — officially supports Windows 10
> 1809 and newer only. On Windows 7/8 the process will fail before our code runs, so `crash.log`
> cannot be written. Use the `win-x86` build on old 32-bit Windows 10 machines.

### Where your data lives

Nothing is written next to the executable — it may sit in `~/Downloads` or on a USB stick.

| OS | Database | App log |
| --- | --- | --- |
| Windows | `%APPDATA%\AiCeoOrchestrator\aiceo.db` | `%APPDATA%\AiCeoOrchestrator\logs\aiceo.log` |
| Linux | `~/.local/share/AiCeoOrchestrator/aiceo.db` | `…/logs/aiceo.log` |
| macOS | `~/Library/Application Support/AiCeoOrchestrator/aiceo.db` | `…/logs/aiceo.log` |

`AICEO_DB_PATH` overrides the database location. A startup failure writes `crash.log` beside the
executable (falling back to the temp directory when that folder is read-only).

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
  Models/                  Agent, AgentView, TaskItem, TaskLog, WorkspaceSettings, UserSettings,
                           UiTheme (tokens + dark/light palettes + CSS export), …
  Data/                    AppDbContext, DbSeeder, SchemaUpgrader, DatabasePaths
  Services/                AgentOrchestrator, LlmGateway, AgentDirectory, WorkspaceService,
                           UserSettingsService, …
  Tools/                   WorkspaceGuard, CommandGuard, ToolCatalog, ToolService, ToolResult
src/AiCeo.Desktop/         Avalonia UI desktop head (Windows · Linux · macOS)
  Program.cs               entry point: global try/catch + crash.log
  App.axaml(.cs)           token-based styles, DI bootstrap, startup-failure window
  Theme/ThemeManager.cs    writes a ThemePalette into the application resources
  Views/NexusWindow.axaml  the v1.0.6 window: hosts the embedded HTML surface
  Views/MainWindow.axaml   the previous native shell (fallback, and CI smoke tests)
  Nexus/                   NexusAssets (extracts the page), NexusPresenter (state + commands),
                           NexusAvailability (is a web engine usable?), NexusState (the JSON contract)
  ViewModels/              MainViewModel, SettingsViewModel, AgentEditorViewModel, cards, feed items
  AvaloniaWorkspacePicker  native folder picker through IStorageProvider
  Assets/                  nexus.html + nexus.css (the Nexus console), aiceo.ico, aiceo.png, theme.css
installer.iss              Inno Setup script (root) → AI-CEO-Orchestrator-<version>-win-x64-Setup.exe
installer/                 wizard-large.bmp / wizard-small.bmp artwork for the installer
tests/AiCeo.Tests/         xunit: topology, guardrail, tools, schema upgrade, provider, theme tokens
```

### Database

SQLite, created on first launch in the per-user data folder (`%APPDATA%\AiCeoOrchestrator` on
Windows, `~/.local/share/AiCeoOrchestrator` on Linux, `~/Library/Application Support/AiCeoOrchestrator`
on macOS). Override with the `AICEO_DB_PATH` environment variable.

`SchemaUpgrader` runs on every start and is idempotent:

- v1.0.1 → v1.0.2: adds `Agents.Provider`, `Agents.BaseUrl`, `Agents.ApiKey`
- v1.0.2 → v1.0.3: adds `Agents.CanUseTools`, `TaskLogs.ToolName`, creates `WorkspaceSettings`
- v1.0.4 → v1.0.5: creates `UserSettings` (`Theme`, `AccentColor`, `FontSize` stored as readable text)

Existing agents, tasks and logs are preserved.

---

## Build from source

```bash
# prerequisites: the .NET 10 SDK (no workload needed — Avalonia is plain NuGet)
dotnet restore src/AiCeo.Desktop/AiCeo.Desktop.csproj

# run it
dotnet run --project src/AiCeo.Desktop/AiCeo.Desktop.csproj

# tests (the portable core)
dotnet test tests/AiCeo.Tests/AiCeo.Tests.csproj

# the single-file executable users download
dotnet publish src/AiCeo.Desktop/AiCeo.Desktop.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# the Windows installer (needs Inno Setup 6 on Windows: choco install innosetup)
mkdir -p dist && cp src/AiCeo.Desktop/bin/Release/net10.0/win-x64/publish/AiCeo.exe dist/AiCeo-win-x64.exe
ISCC.exe installer.iss        # → dist\installer\AI-CEO-Orchestrator-1.0.6-win-x64-Setup.exe
```

Swap `-r` for `linux-x64`, `linux-arm64`, `win-x86`, `win-arm64`, `osx-x64` or `osx-arm64`. Cross-
publishing to Linux/macOS from any host works because Avalonia is a plain NuGet dependency; only the
final executable has to match the target OS.

### Theming

Every colour and font size in the UI is a *dynamic resource* keyed by a token name (`AppBg`,
`Accent`, `CardBg`, `FontBase`, …). On startup — and whenever the settings panel changes anything —
`ThemeManager` builds a `ThemePalette` from `UserSettings` (`UiTheme.Build`) and writes it into
`Application.Resources`; every style in `App.axaml` refers to those keys with
`{DynamicResource …}`, so the whole window repaints without a restart. `ThemePalette.ToCss()` emits
the same tokens as `--color-*` / `--font-*` custom properties, and `Assets/theme.css` is that output
for dark and light (a unit test keeps it in sync).

The Nexus surface consumes the same choices through a second contract: `NexusPresenter` renders the
view model as one JSON snapshot (`window.nexusReceive(…)`), and the page answers with commands
(`dispatch`, `selectWorkspace`, `settings`, `saveAgent`, …) over `window.chrome.webview.postMessage`.
Theme, accent and text size are CSS custom properties on `<html>`, so a change is a repaint, not a
reload. `AICEO_UI=classic` forces the native shell; `AICEO_UI=nexus` forces the web surface.

### Verifying a build without a GUI

Every published binary accepts `--self-test`. It opens the database, prints the resolved paths and
exercises the workspace guardrail (relative path allowed, `..` and absolute escapes refused), then
exits with `0`. `--smoke-ui` additionally checks that the bundled Nexus page and stylesheet are
present and that they can be extracted to disk. CI runs exactly that on the real single-file binaries, so a build that cannot start
never reaches a release.

### CI / release

- `ci.yml` — core library + unit tests on Linux; a real Linux single-file publish that is **run**
  (`--self-test`) and **smoke-tested on a virtual display** (`--smoke-ui`, which also opens the
  settings panel and switches theme/accent/size); a Windows single-file publish whose `.exe` is also
  run and whose `installer.iss` is compiled with Inno Setup on every push; and a grep that fails the
  build if `Task.WhenAll`, `Task.WhenAny` or `Parallel.ForEachAsync` ever appear in the core.
- `release.yml` — on a `v*` tag: publishes all seven RIDs as self-contained single files (Windows →
  `.exe`, Linux/macOS → `.tar.gz` with a `README.md` inside), builds `Setup.exe` with Inno Setup
  (installed through Chocolatey) on a Windows runner, smoke-tests every binary it can execute
  natively, then creates the **v1.0.6** release with all assets plus `SHA256SUMS.txt`.

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
- **No heavyweight UI runtime.** Avalonia targets plain `net10.0` with the .NET runtime inside the
  single file, so there is nothing else to install and nothing that can be missing at startup.
- **One chokepoint.** All path decisions live in `WorkspaceGuard`; all command decisions live in
  `CommandGuard`; all tool dispatch lives in `ToolService`. Nothing else opens a file or a process.
- **Bounded.** Tool rounds, tool calls, file sizes, command output, command time and list sizes all
  have hard ceilings; the transcript is trimmed without orphaning tool messages.
- **Testable core.** `ILlmGateway` is faked in tests, so topology, sequencing, the tool loop and the
  guardrail are asserted without a network or a provider key.
