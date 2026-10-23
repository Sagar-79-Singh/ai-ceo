; Inno Setup script for the AI CEO Orchestrator desktop app.
;
; It wraps the self-contained MAUI publish output into a single per-user setup .exe, so the app can
; be installed without administrator rights and without a code-signing certificate.
;
; Compile from CI with:
;   ISCC.exe /DAppVersion=1.0.3 /DSourceDir=<publish dir> /DOutputDir=<out dir> AiCeo.iss

#define AppName "AI CEO Orchestrator"
#define AppExeName "AiCeo.Maui.exe"
#define AppPublisher "AI CEO Orchestrator"
#define AppUrl "https://github.com/Sagar-79-Singh/ai-ceo"

#ifndef AppVersion
  #define AppVersion "1.0.3"
#endif

#ifndef SourceDir
  #define SourceDir "..\..\artifacts\portable"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\artifacts\out"
#endif

[Setup]
; A stable AppId keeps upgrades in place.
AppId={{7C4B2E51-9A3D-4F6E-8B21-5D9F0A6C3E74}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
DefaultDirName={localappdata}\AI CEO Orchestrator
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=AI-CEO-Orchestrator-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
