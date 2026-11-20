; Lumina's installer. Installs per user into %LocalAppData%\Programs\Lumina (no admin prompt), so the
; built-in updater can keep swapping Lumina.exe in place exactly like it does for the zip builds.
;
; Build after publishing the self-contained exe to publish\sc (see the README):
;   ISCC /DAppVersion=0.6.0 installer\Lumina.iss
; which writes publish\Lumina-0.6.0-alpha-setup.exe.

#ifndef AppVersion
  #error Pass the version, for example: ISCC /DAppVersion=0.6.0 installer\Lumina.iss
#endif
#ifndef VersionLabel
  #define VersionLabel AppVersion + "-alpha"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\sc\Lumina.exe"
#endif

[Setup]
; Never change the AppId: Lumina looks up its Apps & features entry by it (Services\Installation.cs).
AppId={{54F4DC59-5994-4EBD-84E9-70F32C0109CF}
AppName=Lumina
AppVersion={#AppVersion}
AppVerName=Lumina {#AppVersion}
AppPublisher=Pixxel
AppPublisherURL=https://github.com/itspixxel/Lumina
AppSupportURL=https://github.com/itspixxel/Lumina/issues
AppUpdatesURL=https://github.com/itspixxel/Lumina/releases
DefaultDirName={autopf}\Lumina
DefaultGroupName=Lumina
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\src\Lumina\Assets\lumina.ico
UninstallDisplayIcon={app}\Lumina.exe
UninstallDisplayName=Lumina
OutputDir=..\publish
OutputBaseFilename=Lumina-{#VersionLabel}-setup
Compression=lzma2/max
SolidCompression=yes
; Lumina is asked to quit politely first (see PrepareToInstall); this only catches stragglers.
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Lumina
VersionInfoDescription=Lumina Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Lumina when I sign in, so Win+Shift+S is always ready"; Check: not IsUpgrade
Name: "desktopicon"; Description: "Add a desktop shortcut"; Flags: unchecked; Check: not IsUpgrade

[InstallDelete]
; Leftovers from a self-update that was waiting for a restart.
Type: files; Name: "{app}\Lumina.exe.old"
Type: files; Name: "{app}\Lumina.exe.new"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Lumina"; Filename: "{app}\Lumina.exe"
Name: "{autodesktop}\Lumina"; Filename: "{app}\Lumina.exe"; Tasks: desktopicon

[Registry]
; Same value Lumina's own "Start with Windows" setting writes, so the two stay in step.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Lumina"; \
    ValueData: """{app}\Lumina.exe"" --background"; Tasks: startup

[UninstallDelete]
Type: files; Name: "{app}\Lumina.exe.old"
Type: files; Name: "{app}\Lumina.exe.new"

[Run]
Filename: "{app}\Lumina.exe"; Description: "Open Lumina"; Flags: nowait postinstall skipifsilent

[Code]
const
  LuminaMutex = 'Lumina.SingleInstance';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function IsUpgrade: Boolean;
begin
  Result := RegValueExists(HKCU,
    ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1'),
    'UninstallString');
end;

{ Asks a running Lumina to quit (any copy of Lumina.exe can deliver the message) and waits for it to go. }
procedure QuitLumina(const Exe: String);
var
  ResultCode, I: Integer;
begin
  if not CheckForMutexes(LuminaMutex) then Exit;
  if not FileExists(Exe) then Exit;
  Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for I := 1 to 50 do
  begin
    if not CheckForMutexes(LuminaMutex) then Exit;
    Sleep(200);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitLumina(ExpandConstant('{app}\Lumina.exe'));
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  { A copy from a zip may still be running from somewhere else; the new exe can ask it to quit. }
  QuitLumina(ExpandConstant('{app}\Lumina.exe'));
  { Versions before 0.6 don't understand --quit, so close them, or the new Lumina would only bring them forward. }
  if CheckForMutexes(LuminaMutex) then
  begin
    if not WizardSilent then
      MsgBox('An older Lumina is still running. Setup will close it so the new version can start.',
             mbInformation, MB_OK);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Lumina.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value, DataDir: String;
begin
  if CurUninstallStep = usUninstall then
    QuitLumina(ExpandConstant('{app}\Lumina.exe'));

  if CurUninstallStep = usPostUninstall then
  begin
    if RegQueryStringValue(HKCU, RunKey, 'Lumina', Value) and
       (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'Lumina');

    DataDir := ExpandConstant('{userappdata}\Lumina');
    if (not UninstallSilent) and DirExists(DataDir) then
      if MsgBox('Also remove Lumina''s settings, edit history and the FFmpeg it downloaded?' + #13#10#13#10 +
                'Your screenshots and recordings stay where they are.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
