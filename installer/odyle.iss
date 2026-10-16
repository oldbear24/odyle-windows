; Per-user installer for the self-contained win-x64 build. It needs no administrator
; rights and installs into %LOCALAPPDATA%\Programs\Odyle, a folder the app's own updater
; can write to, so an installed Odyle keeps updating itself from GitHub releases.
; scripts/publish.ps1 -SelfContained -Installer compiles it with Inno Setup 6:
;   iscc /DAppVersion=<version> /DPublishDir=<published files> /DOutputDir=<folder> /DOutputName=<file name> installer\odyle.iss
; Saved as UTF-8 with a byte order mark, so the Czech texts compile as written.
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
; The welcome page carries the scope notice (WelcomeLabel2 below).
DisableWelcomePage=no
Compression=lzma2/max
SolidCompression=yes
; A running Odyle is closed for the file copy; the last page offers to start it again.
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "cs"; MessagesFile: "compiler:Languages\Czech.isl"

[Messages]
; What Odyle is and is not: the same notice as Settings -> Packet reader and the dashboard.
en.WelcomeLabel2=This will install [name/ver] on your computer.%n%nOdyle is an unofficial fan project, not affiliated with or endorsed by NCSOFT. The Windows reader only listens to your own game connection: it never reads game memory, changes or sends game traffic, automates play or collects combat data. NCSOFT's rules ban unauthorized programs, so use Odyle at your own risk.
cs.WelcomeLabel2=Průvodce nainstaluje [name/ver] do tohoto počítače.%n%nOdyle je neoficiální fanouškovský projekt, který není spojen se společností NCSOFT ani jí schválen. Čtečka pro Windows jen naslouchá spojení vaší vlastní hry: nikdy nečte paměť hry, nemění ani neposílá herní provoz, neautomatizuje hraní a nesbírá data o boji. Pravidla NCSOFT zakazují neautorizované programy, Odyle proto používáte na vlastní riziko.

[CustomMessages]
en.AutostartTask=Start Odyle with Windows (it waits in the tray)
cs.AutostartTask=Spouštět Odyle se systémem Windows (čeká v oznamovací oblasti)
en.StartupGroup=When Windows starts:
cs.StartupGroup=Po spuštění Windows:
; The page after the tasks while Npcap is missing. Odyle may not bundle Npcap, so it links to its download page.
en.NpcapCaption=Npcap is needed to read the game
cs.NpcapCaption=Ke čtení hry je potřeba Npcap
en.NpcapDescription=Odyle reads the game's packets through Npcap, which is installed separately.
cs.NpcapDescription=Odyle čte pakety hry přes Npcap, který se instaluje zvlášť.
en.NpcapText=Npcap was not found on this PC. Download it from its official page and install it with its default options; leave "Restrict Npcap driver's access to Administrators only" unchecked. Odyle may not include Npcap itself.%n%nYou can also install it later: Odyle starts reading by itself once Npcap is there.
cs.NpcapText=Npcap na tomto počítači nebyl nalezen. Stáhni ho z jeho oficiální stránky a nainstaluj s výchozími volbami; volbu „Restrict Npcap driver's access to Administrators only“ nech nezaškrtnutou. Odyle Npcap přibalit nesmí.%n%nNainstalovat ho můžeš i později: jakmile bude Npcap k dispozici, Odyle začne číst samo.
en.NpcapButton=Open the Npcap download page
cs.NpcapButton=Otevřít stránku pro stažení Npcap
en.NpcapRun=Open the Npcap download page (npcap.com)
cs.NpcapRun=Otevřít stránku pro stažení Npcap (npcap.com)
; Asked by the uninstaller while this PC is paired.
en.UnpairPrompt=Also disconnect this PC from your Odyle account?%n%nYes forgets this PC's pairing on the server and on this PC, like Disconnect in Odyle's settings. No keeps it, so a later install is still paired.
cs.UnpairPrompt=Odpojit tento počítač i od tvého účtu Odyle?%n%nAno zapomene spárování tohoto počítače na serveru i v tomto počítači, stejně jako Odpojit v nastavení Odyle. Ne ho ponechá, takže pozdější instalace zůstane spárovaná.

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
Filename: "https://npcap.com/#download"; Description: "{cm:NpcapRun}"; Flags: shellexec nowait postinstall skipifsilent; Check: not NpcapInstalled

[UninstallDelete]
; Files the updater added or moved aside (Odyle.exe.old) and its downloaded packages.
; The pairing, preferences and log in %LOCALAPPDATA%\Odyle stay for a later install,
; unless the uninstaller is asked to disconnect this PC (below).
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\Odyle\updates"

[Code]
var
  NpcapPage: TOutputMsgWizardPage;

// Npcap's own installer writes HKLM\SOFTWARE\Npcap. It is a 32-bit program, so the key
// may sit in either registry view; both are checked, like the app does.
function NpcapInstalled: Boolean;
begin
  Result := RegKeyExists(HKEY_LOCAL_MACHINE_32, 'SOFTWARE\Npcap');
  if not Result and IsWin64 then
    Result := RegKeyExists(HKEY_LOCAL_MACHINE_64, 'SOFTWARE\Npcap');
end;

procedure OpenNpcapDownload(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://npcap.com/#download', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

// A page after the tasks, shown only while Npcap is missing, with a button to its download page.
procedure InitializeWizard;
var
  DownloadButton: TNewButton;
begin
  NpcapPage := CreateOutputMsgPage(wpSelectTasks, CustomMessage('NpcapCaption'), CustomMessage('NpcapDescription'), CustomMessage('NpcapText'));
  DownloadButton := TNewButton.Create(NpcapPage);
  DownloadButton.Parent := NpcapPage.Surface;
  DownloadButton.Caption := CustomMessage('NpcapButton');
  DownloadButton.Left := 0;
  DownloadButton.Top := NpcapPage.MsgLabel.Top + NpcapPage.MsgLabel.Height + ScaleY(16);
  DownloadButton.Width := ScaleX(260);
  DownloadButton.Height := ScaleY(23);
  DownloadButton.OnClick := @OpenNpcapDownload;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = NpcapPage.ID then
    Result := NpcapInstalled;
end;

// Settings can switch Start with Windows on without the installer, so uninstalling
// always removes the Run entry, whoever wrote it. While this PC is paired, the uninstaller
// asks whether to disconnect it too: "Odyle.exe --unpair" then closes a running Odyle,
// forgets the device key on the server and deletes the saved pairing. A silent uninstall
// keeps the pairing.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if not UninstallSilent and FileExists(ExpandConstant('{localappdata}\Odyle\session.bin')) then
      if MsgBox(CustomMessage('UnpairPrompt'), mbConfirmation, MB_YESNO) = IDYES then
        Exec(ExpandConstant('{app}\Odyle.exe'), '--unpair', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Odyle');
  end;
end;
