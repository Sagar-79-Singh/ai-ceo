; ===========================================================================================
;  AI CEO Orchestrator — custom Inno Setup script (v1.0.6)
;
;  Packages the published, self-contained single-file executable into one Setup.exe:
;
;      ISCC.exe installer.iss
;      ISCC.exe /DAppVersion=1.0.6 /DAppSourceExe=dist\AiCeo-win-x64.exe installer.iss
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
; ===========================================================================================

#ifndef AppVersion
  #define AppVersion "1.0.6"
#endif

; The published single-file executable handed to the compiler.
#ifndef AppSourceExe
  #define AppSourceExe "dist\AiCeo-win-x64.exe"
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
OutputBaseFilename=AI-CEO-Orchestrator-{#AppVersion}-win-x64-Setup
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
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

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

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Comment: "Plan, delegate and deliver — one CEO agent, one worker at a time."
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"

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

var
  ProgressTrack: TPanel;
  ProgressFill: TPanel;
  ProgressLabel: TNewStaticText;
  WelcomeNote: TNewStaticText;

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
  WelcomeNote.Caption := 'AI CEO  ·  v1.0.6  ·  native desktop  ·  strictly one worker at a time';
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
begin
  Result := True;
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
