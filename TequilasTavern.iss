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

// ---- VB-CABLE virtual audio cable (for TTS -> Discord) --------------------
// If the cable driver isn't present, download it from vb-audio.com during
// install/update and run its setup silently (one UAC prompt — driver installs
// require admin). Any failure (offline, UAC declined, URL moved) is ignored so
// the app install itself never breaks. The cable appears after a reboot.

function VbCableInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:VBCABLE {87459874-1236-4469}')
    or DirExists(ExpandConstant('{commonpf64}\VB\CABLE'));
end;

procedure InstallVbCable();
var
  Zip, Dir: String;
  Rc: Integer;
begin
  if VbCableInstalled() then exit;
  // Driver installs need admin. Only proceed when setup is ALREADY elevated so the
  // whole thing stays silent — never pop a surprise UAC prompt (e.g. mid-stream
  // during an auto-update). Unelevated runs just skip; a later elevated install
  // or update picks it up.
  if not IsAdmin() then exit;
  try
    DownloadTemporaryFile('https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip',
      'vbcable.zip', '', nil);
  except
    exit; // offline or URL changed — skip quietly
  end;
  Zip := ExpandConstant('{tmp}\vbcable.zip');
  Dir := ExpandConstant('{tmp}\vbcable');
  if not Exec('powershell.exe',
      '-NoProfile -Command "Expand-Archive -LiteralPath ''' + Zip + ''' -DestinationPath ''' + Dir + ''' -Force"',
      '', SW_HIDE, ewWaitUntilTerminated, Rc) then exit;
  if Rc <> 0 then exit;
  // Already elevated: fully silent driver install, no windows, no prompts.
  Exec(Dir + '\VBCABLE_Setup_x64.exe', '-i -h', Dir, SW_HIDE, ewWaitUntilTerminated, Rc);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallVbCable();
end;
