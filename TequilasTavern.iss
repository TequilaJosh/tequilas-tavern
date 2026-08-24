; Inno Setup script for Tequilas' Tavern
; Build with: ISCC.exe TequilasTavern.iss  (or just run build-installer.bat)

#define MyAppName "Tequilas' Tavern"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "TequilaJosh"
#define MyAppExeName "TequilasTavern.exe"
#define MyPublishDir "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{4C7D91B8-52E0-47AF-B7D2-3AC0E61F7A55}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Tequilas Tavern
DefaultGroupName={#MyAppName}
OutputDir=installer
OutputBaseFilename=Tequilas-Tavern-Setup-{#MyAppVersion}
SetupIconFile=tt-chat.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableDirPage=no
DisableProgramGroupPage=yes
; Close the running app during a silent auto-update; we relaunch it ourselves.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";              Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}";    Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";        Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Interactive install: offer to launch from the finish page.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
; Silent install (auto-update): relaunch the app automatically.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: IsSilentRun

[Code]
function IsSilentRun(): Boolean;
begin
  Result := WizardSilent();
end;

