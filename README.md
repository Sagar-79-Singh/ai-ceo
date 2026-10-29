# AI CEO Orchestrator

A **cross-platform desktop** AI orchestrator written in **C# / .NET 10**, hosted by **PhotinoX**: a
native OS window around the **Nexus** interface, a live HTML/WebGL console rendered by the operating
system's own WebView (WebView2 on Windows, WKWebView on macOS, WebKitGTK 4.1 on Linux) — no browser
tab, no local web server, no bundled UI toolkit, no Node.js. It runs a strict star topology of agents
that can read, write and build inside a folder **you** choose — and, since v2.0.0, **nothing is
written and no command is run until you press Approve**. Windows users can grab the native
installer, and the whole console can be re-themed (dark / light / system, five accent colours, three
text sizes) from the settings modal.

```
You  ──▶  CEO  ──▶  worker 1  ──▶  CEO  ──▶  worker 2  ──▶  CEO  ──▶  You
                 (one at a time, never peer-to-peer)
```

---

## What's new in v2.0.0 — human-in-the-loop approvals, persistent workspaces, clean releases

| Area | Change |
| --- | --- |
| **Approve before it touches anything** | Every `write_file` and `run_command` a worker asks for now **parks the orchestrator** before it executes. `ApprovalCoordinator` publishes a `PendingApproval` (tool, agent, target, and the arguments), the Nexus page renders an amber **Approval Card** with a real line diff (or the exact command line), and the run continues only when you press **Approve**. |
| **Reject with a reason** | **Reject** aborts that single call — nothing is written, nothing is executed — and the reason travels back to the worker as the tool result, with a "do not retry unchanged" instruction. `ComposeWorkerOutput` also appends an *Actions the user rejected* block, so the CEO always learns what was refused and plans around it. |
| **Arm / disarm** | Workspace panel → *Ask before every write or command* (on by default) posts `setApprovals`; with it off the swarm behaves exactly as it did in v1.0.16. Stopping a run or closing the window resolves every parked call, so a rejection reason always reaches the model instead of hanging. |
| **Persistent workspaces ("Spaces")** | `Space` + `SpaceService` store a **name**, a **folder** and the **whole conversation** (prompt, final answer, every feed line, plan steps) in SQLite. The header gains a space dropdown and a **Workspace manager** to create a space (with the current folder or the native picker), switch between them and delete one. |
| **Reopen where you left off** | On launch the last active space is restored — its folder becomes the sandbox root again and its feed is repainted from `SpaceFeedEntries`, so a conversation survives closing the app. Deleting the active space promotes the most recently used survivor. |
| **Cleaner releases** | The release body is now a short summary, the download table and the SmartScreen note. GitHub's generated commit changelog is switched off (`generate_release_notes: false`), and the Claude Code / `Co-Authored-By` trailer stays in the **commit**, not in the published notes. |
| **Version** | `2.0.0` in both `.csproj` files, the assembly/file version, `installer.iss`, the Nexus badge (`v2.0.0`), `release.yml`'s `PRODUCT_VERSION` and every asset name. |

### Older releases
- **v1.0.16** — no hardcoded key or model, blocking first-run connection sheet, readable feed/final-answer surfaces, versioned asset names.
- **v1.0.15** — Avalonia removed; PhotinoX hosts the OS WebView (WebView2 / WKWebView / WebKitGTK 4.1), native messaging, native folder picker, no VC++ prerequisite.
- **v1.0.13** — the window always appears: window built and assigned first, `PresentWindow()` show/activate contract, fully asynchronous WebView2 initialisation.
- **v1.0.12** — start-up reverted to the v1.0.8 code; stock installer wizard with OS-aware WebView2 (109 on Windows 7/8/8.1) and VC++ checks; `.deb` depends on `libgtk-3-0` + `libwebkit2gtk-4.1-0`; `.dmg` targets macOS 10.13+.
- **v1.0.11** — explicit show/activate start-up path and Claude release attribution (the window path was reverted in v1.0.12).
- **v1.0.10** — installer launch fixes: bundled WebView2 prerequisite, `WorkingDir={app}`, native libraries inside the single-file bundle, `crash.log` diagnostics.
- **v1.0.9** — native OS window chrome (no more fake traffic lights) and the taskbar/dock icon fix.
- **v1.0.8** — gear-shaped Settings button, `Enter` to dispatch (`Shift` + `Enter` for a newline, IME-safe), amber provider `CHECKING…` state.
- **v1.0.7** — portable build *and* native installer on every platform: 3 Inno Setup installers, 2 `.deb`, 2 `.dmg`.
- **v1.0.6** — Nexus interface: embedded HTML/Tailwind/WebGL surface, settings modal, graceful fallback.
- **v1.0.5** — custom dark Inno Setup installer, settings page (theme / accent / text size) persisted in `UserSettings`.
- **v1.0.4** — Avalonia UI replaces MAUI (Avalonia itself is gone in v1.0.15); Linux/macOS builds; single-file executables; `crash.log`.
- **v1.0.3** — MAUI Blazor Hybrid desktop app, local workspace, tool calling, guardrail, agentic loop.
- **v1.0.2** — multi-provider LLM layer and health badges.

---

## Install

Download one file from the **Releases** page and run it.

| Platform | Installer | Portable |
| --- | --- | --- |
| **Windows x64 (recommended)** | `Setup-win-x64-v2.0.0.exe` — double-click, per-user, Start-menu + desktop shortcuts | `AiCeo-win-x64-v2.0.0.exe` |
| Windows x86 | `Setup-win-x86-v2.0.0.exe` | `AiCeo-win-x86-v2.0.0.exe` |
| Windows ARM64 | `Setup-win-arm64-v2.0.0.exe` | `AiCeo-win-arm64-v2.0.0.exe` |
| Debian / Ubuntu x64 | `sudo apt install ./AiCeo-linux-x64-v2.0.0.deb` (depends on `libgtk-3-0`, `libwebkit2gtk-4.1-0`, `libnotify4`) | `AiCeo-linux-x64-v2.0.0.tar.gz` |
| Debian / Ubuntu ARM64 | `sudo apt install ./AiCeo-linux-arm64-v2.0.0.deb` | `AiCeo-linux-arm64-v2.0.0.tar.gz` |
| macOS Intel | `AiCeo-osx-x64-v2.0.0.dmg` — open, drag the app to Applications | `AiCeo-osx-x64-v2.0.0.tar.gz` |
| macOS Apple Silicon | `AiCeo-osx-arm64-v2.0.0.dmg` | `AiCeo-osx-arm64-v2.0.0.tar.gz` |

Portable Linux/macOS builds unpack with `tar -xzf <file>.tar.gz && ./AiCeo`.

`Setup-win-<arch>-v2.0.0.exe` checks its prerequisites before it copies anything:

- **Microsoft Edge WebView2 Runtime** — the Nexus interface draws itself with it. Windows 10/11 get
  the current Evergreen bootstrapper; Windows 7 / 8 / 8.1 get the **109** line (109.0.1518.x), the
  last runtime that supports them. Setup asks first, and **cancels the installation if you decline**.
- **Microsoft Visual C++ 2015-2022 Redistributable** — no longer checked (v1.0.15): PhotinoX uses
  the WebView the system provides, so the redistributable is not a dependency of this build.
- **.NET is not checked**: the executable is published with `--self-contained true`.

> **32-bit Windows:** `AiCeo-win-x86-v2.0.0.exe` / `Setup-win-x86-v2.0.0.exe` still build and install, but the
> native window/WebView bridge PhotinoX ships is x64/ARM64 only, so the 32-bit build cannot open the
> Nexus window — it says so in `crash.log` instead of failing obscurely. A 32-bit Windows 10
> installation running on 64-bit hardware can and should use the **x64** build.

Then, in the sidebar, click **Select Workspace Folder** and choose the folder agents may work in.

The binaries are self-contained (the .NET runtime is inside), so nothing else has to be installed.
They are not code-signed: on Windows choose **More info → Run anyway** if SmartScreen complains; on
macOS use right-click → **Open** the first time; on Linux `chmod +x AiCeo` if your tar extractor
dropped the bit.

**Requirements:** Windows 10/11 (x64, x86 or ARM64) with the WebView2 runtime (the installer adds it
when missing), a modern glibc Linux with `libgtk-3-0` + `libwebkit2gtk-4.1-0` + `libnotify4` (the
`.deb` declares all three), or macOS 10.13+ (Intel or Apple Silicon).

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

`AICEO_DB_PATH` overrides the database location. A startup failure writes `crash.log` to
`%APPDATA%\AiCeoOrchestrator` (or the equivalent per-user folder), beside the executable and in the
temp directory, together with a `startup.log` breadcrumb trail — so an app that exits without a
window still leaves a record of how far it got.

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

Every backend is OpenAI-compatible, so one client drives all of them.

**First run asks for the connection** (nothing is compiled into the build): the console is blocked by
a setup sheet until you save a **provider**, an optional **base URL**, an **API key** and a **model**.
The four values are written to the `UserSettings` row of `aiceo.db`
(`Provider`, `BaseUrl`, `ApiKey`, `ModelId`), and every agent, the health probe and the orchestrator
read them through `LlmSetupProvider`. The same sheet is reachable later from
**Settings → Connection**; a blank key keeps the stored one, a blank base URL uses the provider
default, and a per-agent key (optional) makes that one agent self-contained.

| Provider | Default base URL | Key environment variable |
| --- | --- | --- |
| **OmniRoute** (default) | `http://localhost:20128/v1` | `OMNIROUTE_KEY` |
| **OpenAI** | `https://api.openai.com/v1` | `OPENAI_API_KEY` |
| **Gemini** | `https://generativelanguage.googleapis.com/v1beta/openai` | `GEMINI_API_KEY` |
| **Custom** | any base URL you supply (required) | `CUSTOM_API_KEY` |

Precedence, in order: an agent's own key (self-contained connection) → the connection saved in the
setup sheet → the provider environment variable → (OmniRoute only) `OmniRoute:ApiKey` from
configuration. Model: an agent's own `ModelId` (only when it also owns a key) → the model saved in
the setup sheet. API keys are **never** sent to the UI or an API: the page receives only a mask such
as `sk-1…cdef` plus where the key came from (`stored`, `settings`, `env:OPENAI_API_KEY`, …).

---

## Repository layout

```
src/AiCeo.Core/            portable class library (no UI)
  Models/                  Agent, AgentView, TaskItem, TaskLog, WorkspaceSettings, UserSettings,
                           UiTheme (tokens + dark/light palettes + CSS export), …
  Data/                    AppDbContext, DbSeeder, SchemaUpgrader, DatabasePaths
  Services/                AgentOrchestrator, LlmGateway, LlmConnections, LlmSetupProvider,
                           AgentDirectory, WorkspaceService, UserSettingsService, …
  Tools/                   WorkspaceGuard, CommandGuard, ToolCatalog, ToolService, ToolResult
src/AiCeo.Desktop/         PhotinoX desktop head (Windows · Linux · macOS) — no XAML at all
  Program.cs               entry point: PhotinoApplication.Run, the NexusShell window, crash.log
  UiThread.cs              the only file that knows about PhotinoDispatcher (+ SynchronizationContext)
  PhotinoWorkspacePicker   native folder picker through PhotinoWindow.ShowOpenFolder
  Nexus/                   NexusAssets (reads the embedded page, inlines its stylesheet),
                           NexusPresenter (state snapshots + command router), NexusState (JSON contract)
  ViewModels/              MainViewModel, AgentEditorViewModel, cards, feed items (no UI toolkit types)
  AppBootstrap.cs          DI container, database preparation, --self-test
  Assets/                  nexus.html + nexus.css (the Nexus console), aiceo.ico, aiceo.png, theme.css
installer.iss              Inno Setup script (root) → Setup-win-<arch>-v<version>.exe (x64 / x86 / arm64)
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
- v1.0.15 → v1.0.16: adds `UserSettings.Provider`, `BaseUrl`, `ApiKey`, `ModelId` — the LLM
  connection the first-run setup writes (an existing row keeps its theme and starts unconfigured)
- v1.0.16 → v2.0.0: creates `Spaces` (name, folder, last prompt/answer, active flag) and
  `SpaceFeedEntries` (each space's conversation), plus the indexes that list and restore them

Existing agents, tasks and logs are preserved.

---

## Build from source

```bash
# prerequisites: the .NET 10 SDK (no workload needed — PhotinoX is plain NuGet).
# On Linux the system WebView is needed too:
# sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0 libnotify4
dotnet restore src/AiCeo.Desktop/AiCeo.Desktop.csproj

# run it
dotnet run --project src/AiCeo.Desktop/AiCeo.Desktop.csproj

# tests (the portable core)
dotnet test tests/AiCeo.Tests/AiCeo.Tests.csproj

# the single-file executable users download
dotnet publish src/AiCeo.Desktop/AiCeo.Desktop.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# the Windows installers (need Inno Setup 6.3+ on Windows: choco install innosetup)
mkdir -p dist
for arch in x64 x86 arm64; do
  cp "src/AiCeo.Desktop/bin/Release/net10.0/win-$arch/publish/AiCeo.exe" "dist/AiCeo-win-$arch-v2.0.0.exe"
  ISCC.exe "/DAppVersion=2.0.0" "/DAppArch=$arch" "/DAppSourceExe=dist\AiCeo-win-$arch-v2.0.0.exe" installer.iss
done                      # → dist\installer\Setup-win-<arch>-v2.0.0.exe (x64, x86, arm64)

# the Debian package (needs dpkg-deb, i.e. any Debian/Ubuntu host)
dpkg-deb --root-owner-group --build pkgroot AiCeo-linux-x64-v2.0.0.deb

# the macOS disk image (needs macOS: hdiutil + iconutil)
hdiutil create -volname "AI CEO Orchestrator" -srcfolder "AI CEO Orchestrator.app" -ov -format UDZO AiCeo-osx-arm64-v2.0.0.dmg
```

Swap `-r` for `linux-x64`, `linux-arm64`, `win-x86`, `win-arm64`, `osx-x64` or `osx-arm64`. Cross-
publishing to Linux/macOS from any host works because PhotinoX is a plain NuGet package (its native
library is compiled per RID); only the final executable has to match the target OS.

### Theming

The Nexus page owns every pixel, so theming happens in CSS custom properties on `<html>`:
`data-theme` (`dark` / `light`, or the OS preference for `System`), `data-accent` and `--ui-scale`.
Choosing a theme, accent or text size in the settings modal repaints immediately and posts
`{"type":"settings",…}` to the host, which stores it in the `UserSettings` table. On the next start
`NexusPresenter` renders the saved values into the first state snapshot, so the page comes up in the
chosen palette.

`UiTheme` in `AiCeo.Core` still holds the token set and `ThemePalette.ToCss()`, which is what
`Assets/theme.css` contains (a unit test keeps the two in sync); it is plain data, not a framework
dependency, so the palette can be exported anywhere.

### The host bridge

| Direction | Mechanism |
| --- | --- |
| C# → page | `PhotinoWindow.SendWebMessage("{\"type\":\"state\",\"state\":{…}}")` → the page's `window.external.receiveMessage` callback → `window.nexusReceive(state)`; toasts use `{"type":"toast","text":…,"isError":…}` → `window.nexusToast(…)`. |
| page → C# | `window.external.sendMessage("{\"type\":\"dispatch\",…}")` → `PhotinoWindow.RegisterWebMessageReceivedHandler` → `NexusPresenter.HandleMessageAsync`. |

`window.chrome.webview.postMessage` is still understood as a fallback, so the same page also works in
a plain WebView2 host. Command types: `ready`, `refresh`, `selectWorkspace`, `clearWorkspace`,
`refreshHealth`, `setTools`, `dispatch`, `stop`, `settings`, `saveAgent`, `deleteAgent`, `makeCeo`.

### Verifying a build without a GUI

Every published binary accepts `--self-test`. It opens the database, prints the resolved paths and
exercises the workspace guardrail (relative path allowed, `..` and absolute escapes refused), then
exits with `0`. `--smoke-ui` additionally opens the real native window on a display (Xvfb in CI),
waits for the page's `ready` handshake over the native web-message bridge, round-trips a settings
command through the host, then (v2.0.0) creates a **space**, asserts it was persisted, published and
that its conversation comes back after a reload, and parks a synthetic `run_command` to prove the
approval gate waits and returns the rejection reason. It exits `0` only if all of that worked. CI runs exactly that on the real
single-file binary, so a build that cannot start — or whose WebView cannot render the console —
never reaches a release.

### CI / release

- `ci.yml` — core library + unit tests on Linux; a real Linux single-file publish that is **run**
  (`--self-test`) and **smoke-tested on a virtual display** (`--smoke-ui`: native window, WebKitGTK
  web view, page→host handshake, settings round-trip); a Windows single-file publish whose `.exe` is also
  run and whose `installer.iss` is compiled for x64, x86 and arm64 with Inno Setup on every push
  (the cross-architecture builds use stub executables, which still exercises the preprocessor
  branches and Inno's ARM64 support); and a grep that fails the
  build if `Task.WhenAll`, `Task.WhenAny` or `Parallel.ForEachAsync` ever appear in the core.
- `release.yml` — on a `v*` tag: publishes all seven RIDs as self-contained single files (Windows →
  `.exe`, Linux/macOS → `.tar.gz` with a `README.md` inside), smoke-tests every binary it can execute
  natively, then fans out into three installer jobs — Inno Setup ×3 on a Windows runner (installed
  through Chocolatey), `dpkg-deb` ×2 on Ubuntu, `hdiutil` ×2 on macOS — and finally creates the
  **v2.0.0** release with all 14 deliverables plus `SHA256SUMS.txt`. The release job gates on the
  presence and file magic of every asset, so a missing or truncated installer fails the run instead
  of shipping.

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
- **No heavyweight UI runtime.** PhotinoX targets plain `net10.0` and draws with the WebView the OS
  already ships, with the .NET runtime inside the single file — so there is no UI toolkit to install
  and nothing of ours that can be missing at startup.
- **One chokepoint.** All path decisions live in `WorkspaceGuard`; all command decisions live in
  `CommandGuard`; all tool dispatch lives in `ToolService`. Nothing else opens a file or a process.
- **Bounded.** Tool rounds, tool calls, file sizes, command output, command time and list sizes all
  have hard ceilings; the transcript is trimmed without orphaning tool messages.
- **Testable core.** `ILlmGateway` is faked in tests, so topology, sequencing, the tool loop and the
  guardrail are asserted without a network or a provider key.
