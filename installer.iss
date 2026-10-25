; ===========================================================================================
;  AI CEO Orchestrator — custom Inno Setup script (v1.0.11)
;
;  Packages the published, self-contained single-file executable into one Setup.exe:
;
;      ISCC.exe installer.iss
;      ISCC.exe /DAppVersion=1.0.11 /DAppArch=x64 /DAppSourceExe=dist\AiCeo-win-x64.exe installer.iss
;
;  The release workflow downloads Inno Setup through Chocolatey, drops the published
;  AiCeo-win-x64.exe into dist\ and compiles this file; the resulting Setup.exe is attached to
;  the GitHub Release.
;
;  Design notes
;    * WizardStyle=modern with a hand-painted #1E1E2E palette: the wizard, notebook pages,
;      buttons and labels are recoloured in [Code] so the installer matches the app's dark UI.
;    * Custom wizard artwork (installer\wizard-large.bmp / wizard-small.bmp) instead of the
;      stock blue Inno bitmaps.
;    * Themed welcome / finished copy.
;    * A custom slim progress bar (track + accent fill + percentage) replaces the stock VCL
;      progress gauge, which cannot be recoloured.
;    * Per-user install into %LOCALAPPDATA%\Programs — no UAC prompt; an administrator can
;      still choose "install for all users" from the dialog.
;    * Desktop shortcut (opt-out task) and Start-menu shortcuts are always created.
;    * v1.0.11: the Microsoft Edge WebView2 Evergreen runtime is installed silently when the app's
;      web surface needs it and it is missing (the bootstrapper is embedded in Setup.exe when the
;      release workflow supplies it). Both shortcuts and the post-install launch run with
;      WorkingDir = {app}, because a self-contained single-file app resolves its extracted native
;      payload relative to the working directory on some Windows configurations.
; ===========================================================================================

#ifndef AppVersion
  #define AppVersion "1.0.11"
#endif

; The Evergreen WebView2 bootstrapper. The release workflow drops it next to the published
; executable; when it is absent (a bare local compile) the installer simply skips the optional
; prerequisite and points the user at the download page instead of failing to build.
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
  #define AppSourceExe "dist\AiCeo-win-" + AppArch + ".exe"
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

; The wizard look.
WizardStyle=modern
WizardImageFile=installer\wizard-large.bmp
WizardSmallImageFile=installer\wizard-small.bmp
WizardImageStretch=no
WizardResizable=no
DisableWelcomePage=no
DisableProgramGroupPage=no

; Output.
OutputDir=dist\installer
OutputBaseFilename=Setup-win-{#AppArch}
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

; .NET 10 (bundled in the executable) requires Windows 10 1809 / Server 2019 or newer.
MinVersion=10.0.17763
ArchitecturesAllowed={#AppArchAllowed}
#ifdef AppArch64Mode
ArchitecturesInstallIn64BitMode={#AppArch64Mode}
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to the AI CEO Orchestrator
WelcomeLabel2=This wizard installs {#AppName} {#AppVersion} — a native desktop AI orchestrator.%n%nYou brief one CEO agent. It plans the work, delegates to a single worker at a time inside the folder you choose, and reports back with the final answer.%n%nEverything installs into your own user profile: no administrator rights, no services, no registry sprawl.
FinishedHeadingLabel=Your AI CEO is installed
FinishedLabel={#AppName} is ready. Launch it from the Start menu or the desktop shortcut, then choose a workspace folder — that folder is the only place the workers may touch.%n%nYour data (agents, keys, task history, theme) lives in %APPDATA%\AiCeoOrchestrator.
SelectDirDesc=Where should {#AppName} live?
SelectDirLabel3=Setup will install {#AppName} into the following folder.
SelectProgramGroupDesc=Where should Setup place the Start-menu shortcuts?
SelectProgramGroupLabel=Setup will create the program shortcuts in the following Start-menu folder.
SelectTasksDesc=Which additional shortcuts do you want?
SelectTasksLabel2=Select the additional shortcuts Setup should create while installing {#AppName}.
ReadyLabel1=Setup is ready to install {#AppName}
ReadyLabel2a=Click Install to start the installation, or click Back to review or change any settings.
InstallingLabel=Installing {#AppName} — briefing the board…
FinishedLabelNoIcons=Setup has finished installing {#AppName} on your computer.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#AppSourceExe}"; DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
#ifdef IncludeWebView2Bootstrapper
; dontcopy: the bytes stay inside Setup.exe until ExtractTemporaryFile pulls them into {tmp}, so a
; machine that already has the runtime never pays for them.
Source: "{#WebView2Bootstrapper}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebView2Setup.exe"; Flags: dontcopy
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Comment: "Plan, delegate and deliver — one CEO agent, one worker at a time."
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; WorkingDir is explicit: the app extracts its bundled native libraries (Skia, HarfBuzz, the
; WebView2 loader) relative to the working directory, and Explorer/Inno otherwise start it in
; System32. Running it there is exactly the kind of thing that makes an installed build look dead.
Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\crash.log"
Type: files; Name: "{app}\startup.log"

[Code]
const
  { TColor is $00BBGGRR (reversed byte order), so the right-hand comments show the real hex.
    Do not write highlighted hashes here: ISPP would treat them as inline directives. }
  DarkBg      = $2E1E1E;   { app background: 30 30 46 }
  DarkPanel   = $1F1414;   { field background: 20 20 31 }
  DarkTrack   = $3C2A2A;   { progress groove: 42 42 60 }
  AccentColor = $B0E05E;   { mint accent }
  AccentDeep  = $444A1D;   { deep accent edge }
  TextPrimary = $FBF2EA;   { near-white }
  TextBody    = $E6D6C9;   { body text }
  TextMuted   = $BDA38E;   { muted text }

  { The Evergreen "WebView2 Runtime" product GUID. Its presence under EdgeUpdate\Clients with a
    non-empty pv value means the runtime is installed (per-machine or per-user). }
  WebView2ClientGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

var
  ProgressTrack: TPanel;
  ProgressFill: TPanel;
  ProgressLabel: TNewStaticText;
  WelcomeNote: TNewStaticText;

{ ------------------------------------------------------------------ WebView2 runtime }

function WebView2KeyHasVersion(RootKey: Integer; const SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey + '\' + WebView2ClientGuid, 'pv', Version)
            and (Version <> '');
end;

function IsWebView2RuntimeInstalled: Boolean;
begin
  { Per-user install (the common one on Windows 10/11 desktops). }
  Result := WebView2KeyHasVersion(HKCU, 'Software\Microsoft\EdgeUpdate\Clients');
  if Result then
    exit;

  { Per-machine installs: 64-bit view first, then the 32-bit / WOW6432Node view. }
  Result := WebView2KeyHasVersion(HKLM, 'Software\Microsoft\EdgeUpdate\Clients');
  if Result then
    exit;

  Result := WebView2KeyHasVersion(HKLM, 'Software\WOW6432Node\Microsoft\EdgeUpdate\Clients');
end;

{ Runs the bundled Evergreen bootstrapper. Returns its exit code, or -1 when it could not run.
  0 = installed, 1 = already installed, 2 = reboot required, 3 = failed. }
function InstallWebView2Runtime: Integer;
var
  ExitCode: Integer;
  Bootstrapper: String;
begin
  Result := -1;

#ifdef IncludeWebView2Bootstrapper
  Bootstrapper := ExpandConstant('{tmp}\MicrosoftEdgeWebView2Setup.exe');

  { The entry in [Files] is flagged dontcopy, so pull it out of Setup.exe on demand. }
  if not FileExists(Bootstrapper) then
    ExtractTemporaryFile('MicrosoftEdgeWebView2Setup.exe');

  if not FileExists(Bootstrapper) then
  begin
    Log('WebView2 runtime: the bootstrapper could not be extracted');
    exit;
  end;

  WizardForm.StatusLabel.Caption := 'Installing the Microsoft Edge WebView2 runtime…';

  if Exec(Bootstrapper, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := ExitCode
  else
    Log('WebView2 runtime: the bootstrapper could not be started');
#else
  Log('WebView2 runtime: no bootstrapper was bundled with this installer');
#endif
end;

{ ------------------------------------------------------------------ painting helpers }

procedure PaintWizard;
var
  Index: Integer;
begin
  with WizardForm do
  begin
    Color := DarkBg;
    Font.Name := 'Segoe UI';
    Font.Size := 10;

    MainPanel.Color := DarkBg;

    for Index := 0 to OuterNotebook.PageCount - 1 do
    begin
      OuterNotebook.Pages[Index].Color := DarkBg;
    end;

    { The inner notebook holds the modern style's content pages. }
    for Index := 0 to InnerNotebook.PageCount - 1 do
    begin
      InnerNotebook.Pages[Index].Color := DarkBg;
    end;

    { Belt and braces: name the pages explicitly as well. }
    WelcomePage.Color := DarkBg;
    InnerPage.Color := DarkBg;
    FinishedPage.Color := DarkBg;
    LicensePage.Color := DarkBg;
    PasswordPage.Color := DarkBg;
    InfoBeforePage.Color := DarkBg;
    UserInfoPage.Color := DarkBg;
    SelectDirPage.Color := DarkBg;
    SelectComponentsPage.Color := DarkBg;
    SelectProgramGroupPage.Color := DarkBg;
    SelectTasksPage.Color := DarkBg;
    ReadyPage.Color := DarkBg;
    PreparingPage.Color := DarkBg;
    InstallingPage.Color := DarkBg;
    InfoAfterPage.Color := DarkBg;

    PageNameLabel.Font.Color := TextPrimary;
    PageNameLabel.Font.Size := 15;
    PageDescriptionLabel.Font.Color := TextMuted;

    WelcomeLabel1.Font.Color := AccentColor;
    WelcomeLabel1.Font.Size := 18;
    WelcomeLabel1.Font.Style := [fsBold];
    WelcomeLabel2.Font.Color := TextBody;

    SelectDirLabel.Font.Color := TextBody;
    SelectDirBrowseLabel.Font.Color := AccentColor;
    SelectStartMenuFolderLabel.Font.Color := TextBody;
    SelectStartMenuFolderBrowseLabel.Font.Color := AccentColor;
    SelectTasksLabel.Font.Color := TextBody;
    ReadyLabel.Font.Color := TextBody;

    FinishedHeadingLabel.Font.Color := AccentColor;
    FinishedHeadingLabel.Font.Size := 16;
    FinishedHeadingLabel.Font.Style := [fsBold];
    FinishedLabel.Font.Color := TextBody;

    DiskSpaceLabel.Font.Color := TextMuted;
    StatusLabel.Font.Color := TextMuted;
    FilenameLabel.Font.Color := TextMuted;
    BeveledLabel.Font.Color := TextMuted;

    ReadyMemo.Color := DarkPanel;
    ReadyMemo.Font.Color := TextBody;

    DirEdit.Color := DarkPanel;
    DirEdit.Font.Color := TextPrimary;
    GroupEdit.Color := DarkPanel;
    GroupEdit.Font.Color := TextPrimary;
    ComponentsList.Color := DarkPanel;
    ComponentsList.Font.Color := TextBody;
    TasksList.Color := DarkPanel;
    TasksList.Font.Color := TextBody;
    RunList.Color := DarkPanel;
    RunList.Font.Color := TextBody;
    NoIconsCheck.Font.Color := TextBody;

    YesRadio.Font.Color := TextBody;
    NoRadio.Font.Color := TextBody;

    NextButton.Font.Color := TextPrimary;
    BackButton.Font.Color := TextPrimary;
    CancelButton.Font.Color := TextMuted;
  end;
end;

procedure BuildProgressBar;
begin
  { Track: the empty groove, sitting exactly where Inno's own gauge sits. }
  ProgressTrack := TPanel.Create(WizardForm);
  ProgressTrack.Parent := WizardForm.InstallingPage;
  ProgressTrack.BevelOuter := bvNone;
  ProgressTrack.BevelInner := bvNone;
  ProgressTrack.Color := DarkTrack;
  ProgressTrack.SetBounds(
    WizardForm.ProgressGauge.Left,
    WizardForm.ProgressGauge.Top,
    WizardForm.ProgressGauge.Width,
    10);

  { Fill: the accent bar that grows. }
  ProgressFill := TPanel.Create(WizardForm);
  ProgressFill.Parent := WizardForm.InstallingPage;
  ProgressFill.BevelOuter := bvNone;
  ProgressFill.BevelInner := bvNone;
  ProgressFill.Color := AccentColor;
  ProgressFill.SetBounds(
    WizardForm.ProgressGauge.Left,
    WizardForm.ProgressGauge.Top,
    0,
    10);

  { Percentage readout to the right of the bar. }
  ProgressLabel := TNewStaticText.Create(WizardForm);
  ProgressLabel.Parent := WizardForm.InstallingPage;
  ProgressLabel.AutoSize := False;
  ProgressLabel.Caption := '0 %';
  ProgressLabel.Color := TextMuted;
  ProgressLabel.Font.Color := AccentColor;
  ProgressLabel.SetBounds(
    WizardForm.ProgressGauge.Left + WizardForm.ProgressGauge.Width + 6,
    WizardForm.ProgressGauge.Top - 2,
    44,
    16);

  { The stock gauge cannot be recoloured, so it steps aside. }
  WizardForm.ProgressGauge.Visible := False;

  { A small signature line under the welcome copy. }
  WelcomeNote := TNewStaticText.Create(WizardForm);
  WelcomeNote.Parent := WizardForm.WelcomePage;
  WelcomeNote.AutoSize := False;
  WelcomeNote.Caption := 'AI CEO  ·  v1.0.11  ·  {#AppArchLabel}  ·  strictly one worker at a time';
  WelcomeNote.Font.Color := TextMuted;
  WelcomeNote.Font.Size := 8;
  WelcomeNote.SetBounds(0, WizardForm.WelcomeLabel2.Top + WizardForm.WelcomeLabel2.Height + 12, 420, 30);
  WelcomeNote.WordWrap := True;
end;

{ ------------------------------------------------------------------ wizard events }

procedure InitializeWizard;
begin
  PaintWizard;
  BuildProgressBar;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { Modern style repaints the notebook on page changes; re-apply our palette each time. }
  PaintWizard;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
var
  Percent: Integer;
begin
  if MaxProgress <= 0 then
  begin
    Percent := 0;
  end
  else
  begin
    Percent := (CurProgress * 100) div MaxProgress;
  end;

  if (ProgressTrack <> nil) and (ProgressFill <> nil) and (ProgressLabel <> nil) then
  begin
    ProgressFill.Width := (ProgressTrack.Width * Percent) div 100;
    ProgressLabel.Caption := IntToStr(Percent) + ' %';
  end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  { Never block the install: without the runtime the app still runs, on its native Avalonia shell. }
  Result := True;

  if IsWebView2RuntimeInstalled then
  begin
    Log('WebView2 runtime: already installed');
    exit;
  end;

#ifdef IncludeWebView2Bootstrapper
  Log('WebView2 runtime: missing; the bundled Evergreen bootstrapper installs it silently after the files are copied');
#else
  Log('WebView2 runtime: missing and no bootstrapper was bundled with this build');
  if not WizardSilent and (MsgBox('The Microsoft Edge WebView2 runtime is not installed.' + #13#10#13#10 +
      'AI CEO Orchestrator uses it to draw its Nexus interface. Without it the app still starts, ' +
      'but falls back to its simpler native interface.' + #13#10#13#10 +
      'Open Microsoft''s download page now?',
      mbConfirmation, MB_YESNO) = IDYES) then
  begin
    ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOW, ewNoWait, ErrorCode);
  end;
#endif
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;

  if IsWebView2RuntimeInstalled then
    exit;

  ResultCode := InstallWebView2Runtime;
  Log('WebView2 bootstrapper exit code: ' + IntToStr(ResultCode));

  if ResultCode = 3 then
  begin
    { Optional prerequisite: warn, never fail the installation. }
    if not WizardSilent then
      MsgBox('The WebView2 runtime could not be installed automatically.' + #13#10#13#10 +
             'The application is installed and will start, using its native interface. You can add ' +
             'the runtime later from https://developer.microsoft.com/microsoft-edge/webview2/',
             mbInformation, MB_OK);
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { Nothing to ask a single-user install about the program group beyond its name. }
  Result := False;
end;

procedure DeinitializeSetup;
begin
  { Controls are owned by the wizard form, so nothing to free here. }
end;
