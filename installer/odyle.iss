; Per-user installer for the self-contained win-x64 build. It needs no administrator
; rights and installs into %LOCALAPPDATA%\Programs\Odyle, a folder the app's own updater
; can write to, so an installed Odyle keeps updating itself from GitHub releases.
; scripts/publish.ps1 -SelfContained -Installer compiles it with Inno Setup 6:
;   iscc /DAppVersion=<version> /DPublishDir=<published files> /DOutputDir=<folder> /DOutputName=<file name> installer\odyle.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #error PublishDir must name the folder with the published self-contained win-x64 files.
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
#ifndef OutputName
  #define OutputName "odyle-" + AppVersion + "-win-x64-setup"
#endif

[Setup]
AppId={{8D3F6A52-1C7B-4E9A-B6D2-5F0A9C4E7B13}
AppName=Odyle
AppVersion={#AppVersion}
AppVerName=Odyle {#AppVersion}
AppPublisher=oldbear24
AppPublisherURL=https://github.com/oldbear24/odyle-windows
AppSupportURL=https://github.com/oldbear24/odyle-windows/issues
AppUpdatesURL=https://github.com/oldbear24/odyle-windows/releases/latest
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Odyle
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile=..\src\Odyle.Reader\Assets\odyle.ico
UninstallDisplayIcon={app}\Odyle.exe
UninstallDisplayName=Odyle
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; A running Odyle is closed for the file copy; the last page offers to start it again.
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "cs"; MessagesFile: "compiler:Languages\Czech.isl"

[CustomMessages]
en.AutostartTask=Start Odyle with Windows (it waits in the tray)
cs.AutostartTask=Spouštět Odyle se systémem Windows (čeká v oznamovací oblasti)
en.StartupGroup=When Windows starts:
cs.StartupGroup=Po spuštění Windows:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:StartupGroup}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Odyle"; Filename: "{app}\Odyle.exe"
Name: "{autodesktop}\Odyle"; Filename: "{app}\Odyle.exe"; Tasks: desktopicon

[Registry]
; The same entry as Settings -> Start with Windows: "<Odyle.exe>" --tray.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Odyle"; ValueData: """{app}\Odyle.exe"" --tray"; Tasks: autostart

[Run]
Filename: "{app}\Odyle.exe"; Description: "{cm:LaunchProgram,Odyle}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Files the updater added or moved aside (Odyle.exe.old) and its downloaded packages.
; The pairing, preferences and log in %LOCALAPPDATA%\Odyle stay for a later install.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\Odyle\updates"

[Code]
// Settings can switch Start with Windows on without the installer, so uninstalling
// always removes the Run entry, whoever wrote it.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Odyle');
end;
