; ===========================================================================================
;  AI CEO Orchestrator — Inno Setup script (v2.0.0)
;
;  Packages the published, self-contained single-file executable into one Setup.exe:
;
;      ISCC.exe installer.iss
;      ISCC.exe /DAppVersion=2.0.0 /DAppArch=x64 /DAppSourceExe=dist\AiCeo-win-x64-v2.0.0.exe installer.iss
;
;  v2.0.0 design
;    * A stock Inno Setup wizard — no custom palette, no hand-painted controls, no custom
;      artwork, no altered [Messages]. It looks and behaves exactly like the native installer
;      every other Windows program uses, which is also what makes it accessible and DPI-safe.
;    * One prerequisite, checked before any file is copied:
;        - Microsoft Edge WebView2 Runtime. v1.0.15 draws the whole interface with the operating
;          system's own WebView through PhotinoX, so this is the only native component the app
;          cannot ship itself. If it is missing the version of Windows decides which bootstrapper
;          is used: Windows 10/11 get the current Evergreen bootstrapper, while Windows 7 / 8 / 8.1
;          get the 109 line (109.0.1518.x, the last runtime that supports those systems). The user
;          is asked; declining aborts the installation.
;        - The Microsoft Visual C++ 2015-2022 Redistributable is NO LONGER checked (v1.0.15).
;          PhotinoX talks to the WebView the OS already provides and the app is self-contained, so
;          the redistributable is not a dependency of anything that ships here. Removing the check
;          removes one prompt and one download from every install.
;        - .NET is deliberately NOT checked: the executable is published with
;          --self-contained true and carries the runtime inside it.
;    * Per-user install into %LOCALAPPDATA%\Programs — no UAC prompt; an administrator can still
;      choose "install for all users" from the dialog.
;    * Desktop shortcut (opt-out task) and Start-menu shortcuts are always created.
;    * Both shortcuts and the post-install launch run with WorkingDir = {app}, because a
;      self-contained single-file app resolves its extracted native payload relative to the
;      working directory.
; ===========================================================================================

#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif

; The Evergreen WebView2 bootstrapper. The release workflow drops it next to the published
; executable; when it is absent (a bare local compile) the prerequisite step downloads one
; instead, and a machine that already has the runtime never pays for either.
#ifndef WebView2Bootstrapper
  #define WebView2Bootstrapper "dist\MicrosoftEdgeWebView2Setup.exe"
#endif

; `#ifexist` resolves a relative path against this script's folder, so a release build that
; dropped the bootstrapper into dist\ embeds it, and a plain local compile still succeeds.
#ifndef IncludeWebView2Bootstrapper
  #ifexist "dist\MicrosoftEdgeWebView2Setup.exe"
    #define IncludeWebView2Bootstrapper
  #endif
#endif

; The architecture this installer targets: x64 (default), x86 or arm64. The release workflow
; compiles all three, each against its own published single-file executable.
#ifndef AppArch
  #define AppArch "x64"
#endif

; Inno Setup needs different platform guards per architecture. A 32-bit build installs on any
; x86-compatible machine (including x64), while the x64 and arm64 builds stay in native mode.
#if AppArch == "x86"
  #define AppArchAllowed  "x86compatible"
  #define AppArchLabel    "32-bit (x86)"
#elif AppArch == "arm64"
  #define AppArchAllowed  "arm64"
  #define AppArch64Mode   "arm64"
  #define AppArchLabel    "ARM64"
#else
  #define AppArchAllowed  "x64compatible"
  #define AppArch64Mode   "x64compatible"
  #define AppArchLabel    "64-bit (x64)"
#endif

; The published single-file executable handed to the compiler.
#ifndef AppSourceExe
  #define AppSourceExe "dist\AiCeo-win-" + AppArch + "-v" + AppVersion + ".exe"
#endif

#define AppName        "AI CEO Orchestrator"
#define AppShortName   "AI CEO"
#define AppPublisher   "Sagar Singh"
#define AppExeName     "AiCeo.exe"
#define AppUrl         "https://github.com/Sagar-79-Singh/ai-ceo"
#define AppReleases    AppUrl + "/releases"

[Setup]
; A stable AppId keeps upgrades/uninstalls tied to the same entry in Apps & features.
AppId={{8C1E4F42-2C6A-4E4E-9C3B-7A1D9B5B7E10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppReleases}
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

; Per-user by default: no admin rights needed, installs under %LOCALAPPDATA%\Programs.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; The stock modern wizard. No WizardImageFile / WizardSmallImageFile override, no recolouring:
; this is the same native shell Windows users see for every other application.
WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=no

; Output.
OutputDir=dist\installer
OutputBaseFilename=Setup-win-{#AppArch}-v{#AppVersion}
SetupIconFile=src\AiCeo.Desktop\Assets\aiceo.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

Compression=lzma2/max
SolidCompression=yes
InternalCompressLevel=max
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
AllowNoIcons=yes

; Windows 7 SP1 is the floor for the *installer* so the legacy branch below can actually run
; there and install the WebView2 109 runtime. The application itself is .NET 10 and therefore
; needs Windows 10 1809 or newer; the prerequisite step says so in plain language on anything
; older instead of failing silently later.
MinVersion=6.1sp1
ArchitecturesAllowed={#AppArchAllowed}
#ifdef AppArch64Mode
ArchitecturesInstallIn64BitMode={#AppArch64Mode}
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#AppSourceExe}"; DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
#ifdef IncludeWebView2Bootstrapper
; dontcopy: the bytes stay inside Setup.exe until they are needed, so a machine that already has
; the runtime never pays for them.
Source: "{#WebView2Bootstrapper}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebView2Setup.exe"; Flags: dontcopy
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; WorkingDir is explicit: the app extracts its bundled native libraries (the PhotinoX native
; window/WebView bridge and the WebView2 loader) relative to the working directory, and
; Explorer/Inno otherwise start it in System32.
Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\crash.log"
Type: files; Name: "{app}\startup.log"

[Code]
const
  { The Evergreen "WebView2 Runtime" product GUID. A non-empty pv value under
    EdgeUpdate\Clients\<GUID> means the runtime is installed (per-machine or per-user). }
  WebView2ClientGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

  { Windows 10 / 11: the current Evergreen bootstrapper. }
  WebView2LatestUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

  { Windows 7 / 8 / 8.1: the same Evergreen bootstrapper, which Microsoft pins to the 109 line
    (109.0.1518.x) on those systems — 109 is the last WebView2 runtime that supports them, and
    fixed versions newer than 109 refuse to start there. Microsoft's announcement of the 109
    cut-off is why this branch exists at all. }
  WebView2LegacyUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2LegacyLabel = '109.0.1518.x (the last WebView2 runtime that supports Windows 7 / 8 / 8.1)';

  WebView2BootstrapperName = 'MicrosoftEdgeWebView2Setup.exe';
  DownloadPageUrl = 'https://developer.microsoft.com/microsoft-edge/webview2/';

var
  { Filled in by the prerequisite step; kept in a global so the helpers stay small. }
  WebView2InstallerPath: String;

{ ------------------------------------------------------------------ detection }

function RegHasVersion(RootKey: Integer; const SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey + '\' + WebView2ClientGuid, 'pv', Version)
            and (Version <> '');
  if Result then
    Log('WebView2 runtime found under ' + SubKey + ': ' + Version);
end;

function IsWebView2RuntimeInstalled: Boolean;
begin
  { Per-user install — the common one on Windows 10/11 desktops. }
  Result := RegHasVersion(HKCU, 'Software\Microsoft\EdgeUpdate\Clients');
  if Result then
    exit;

  { Per-machine installs: the native view first, then the 32-bit view. }
  Result := RegHasVersion(HKLM, 'Software\Microsoft\EdgeUpdate\Clients');
  if Result then
    exit;

  Result := RegHasVersion(HKLM, 'Software\WOW6432Node\Microsoft\EdgeUpdate\Clients');
end;

function WindowsMajor: Integer;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := Version.Major;
end;

function WindowsMinor: Integer;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := Version.Minor;
end;

{ True on Windows 10 and 11 (and Server 2016+), false on 7 / 8 / 8.1 and older. }
function IsModernWindows: Boolean;
begin
  Result := WindowsMajor >= 10;
end;

{ True below Windows 10 1809 (build 17763), where the .NET 10 runtime inside the app cannot
  start. Nothing here blocks the install; it only tells the truth early. }
function IsOlderThanSupportedWindows: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major < 10)
            or ((Version.Major = 10) and (Version.Build < 17763));
end;

{ ------------------------------------------------------------------ acquisition }

{ Returns the path of the WebView2 bootstrapper in the temporary folder, extracting the copy embedded in
  Setup.exe when the release workflow supplied one and downloading it otherwise. }
function AcquireWebView2Bootstrapper(const Url: String): Boolean;
begin
  WebView2InstallerPath := ExpandConstant('{tmp}\') + WebView2BootstrapperName;
  Result := FileExists(WebView2InstallerPath);

#ifdef IncludeWebView2Bootstrapper
  if not Result then
  begin
    try
      ExtractTemporaryFile(WebView2BootstrapperName);
    except
      Log('WebView2 runtime: the bundled bootstrapper could not be extracted: ' + GetExceptionMessage);
    end;

    Result := FileExists(WebView2InstallerPath);
  end;
#endif

  if not Result then
  begin
    try
      Log('WebView2 runtime: downloading the bootstrapper from ' + Url);
      DownloadTemporaryFile(Url, WebView2BootstrapperName, '', nil);
    except
      Log('WebView2 runtime: the download failed: ' + GetExceptionMessage);
    end;

    Result := FileExists(WebView2InstallerPath);
  end;
end;

{ ------------------------------------------------------------------ prerequisites }

{ Returns True when the installation may continue. Declining the WebView2 prompt aborts, as
  requested; every other outcome keeps the installer usable. }
function EnsureWebView2: Boolean;
var
  Url, RuntimeLabel, Question: String;
  ResultCode: Integer;
begin
  Result := True;

  if IsWebView2RuntimeInstalled then
  begin
    Log('WebView2 runtime: already installed');
    exit;
  end;

  if IsModernWindows then
  begin
    Url := WebView2LatestUrl;
    RuntimeLabel := 'the current WebView2 runtime';
  end
  else
  begin
    { Windows 7 / 8 / 8.1: 109 is the last runtime line that runs there. }
    Url := WebView2LegacyUrl;
    RuntimeLabel := WebView2LegacyLabel;
    Log('WebView2 runtime: legacy Windows detected (' + IntToStr(WindowsMajor) + '.' + IntToStr(WindowsMinor) + '), using the 109 line');
  end;

  if WizardSilent then
  begin
    Log('WebView2 runtime: missing; installing ' + RuntimeLabel + ' without prompting');
  end
  else
  begin
    Question :=
      'Microsoft Edge WebView2 Runtime is required.' + #13#10#13#10 +
      '{#AppName} draws its Nexus interface with it. It is not installed on this computer, so ' +
      'Setup will install ' + RuntimeLabel + ' now.' + #13#10#13#10 +
      'Download size is a few megabytes. Continue?' + #13#10#13#10 +
      'Choosing No cancels the installation.';

    if MsgBox(Question, mbConfirmation, MB_YESNO) = IDNO then
    begin
      Log('WebView2 runtime: the user declined the prerequisite');
      Result := False;
      exit;
    end;
  end;

  if not AcquireWebView2Bootstrapper(Url) then
  begin
    Log('WebView2 runtime: the bootstrapper could not be obtained');
    Result := False;

    if not WizardSilent then
      MsgBox(
        'The WebView2 runtime could not be downloaded.' + #13#10#13#10 +
        'Install it manually from ' + DownloadPageUrl + ' and run this setup again.',
        mbError, MB_OK);

    exit;
  end;

  if not Exec(WebView2InstallerPath, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('WebView2 runtime: the bootstrapper could not be started');
    Result := False;
    exit;
  end;

  Log('WebView2 bootstrapper exit code: ' + IntToStr(ResultCode));

  { 0 = installed, 1 = already installed and up to date, 2 = installed, reboot required. }
  if (ResultCode = 0) or (ResultCode = 1) or (ResultCode = 2) then
  begin
    Result := True;
    exit;
  end;

  Result := IsWebView2RuntimeInstalled;

  if Result then
    Log('WebView2 runtime: present after the bootstrapper ran, despite exit code ' + IntToStr(ResultCode))
  else
    Log('WebView2 runtime: the bootstrapper reported failure (' + IntToStr(ResultCode) + ')');
end;

{ ------------------------------------------------------------------ wizard events }

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Notice: String;
begin
  Result := '';

  if IsOlderThanSupportedWindows and not WizardSilent then
  begin
    Notice :=
      'This computer runs an older version of Windows.' + #13#10#13#10 +
      '{#AppName} is built on .NET 10 and needs Windows 10 version 1809 (build 17763) or newer. ' +
      'Setup will install the version of the WebView2 runtime that this system supports, but the ' +
      'application itself may not start here.';
    MsgBox(Notice, mbInformation, MB_OK);
  end;

  if not EnsureWebView2 then
  begin
    Result :=
      'The Microsoft Edge WebView2 Runtime is required and was not installed, so Setup stopped. '
      + 'Install it from ' + DownloadPageUrl + ' and run this setup again.';
    exit;
  end;
end;
