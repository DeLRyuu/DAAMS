; DAAMS installer - Inno Setup 6.x
; Location: <repo root>\installer\DAAMS.iss  (run build.ps1 first)
;
; Data model (already in your code): everything the apps write lives in %LOCALAPPDATA%\DAAMS
;   data\daams.db, data\protected_assets.json, ...   logs\...   serviceAccountKey.json
; The Engine, the right-click helper and the Control Center all use it, so they must run as the SAME
; Windows user. Install while signed in as that user (and, if UAC asks for credentials, use that same account).

#define AppName    "DAAMS"
#define AppVersion "1.0.0"
#define EngineExe  "DAAMSEngine.exe"
#define ProtectExe "DAAMSProtect.exe"
#define CCExe      "DAAMS.ControlCenter.exe"
#define TaskName   "DAAMS Monitoring Engine"

[Setup]
AppId={{D704EF8B-FCF9-453F-9606-A62200C3833F}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=DAAMS Capstone Team
DefaultDirName={autopf}\DAAMS
DefaultGroupName=DAAMS
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=DAAMS-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
UninstallDisplayName=DAAMS
SetupIconFile=daams.ico
UninstallDisplayIcon={app}\daams.ico

[Types]
Name: "full";    Description: "Full installation (Monitoring Engine + Control Center)"
Name: "engine";  Description: "DAAMS Monitoring Engine only (monitored PC)"
Name: "console"; Description: "DAAMS Control Center only (admin PC)"
Name: "custom";  Description: "Custom installation"; Flags: iscustom

[Components]
Name: "engine";        Description: "DAAMS Monitoring Engine (includes right-click helper and Clean Local Data tool)"; Types: full engine custom
Name: "controlcenter"; Description: "DAAMS Control Center";                                                          Types: full console custom

[Tasks]
Name: "startup";     Description: "Start the DAAMS Monitoring Engine automatically when I sign in";                 Components: engine
Name: "contextmenu"; Description: "Add the DAAMS right-click menu (Protect This File / Folder)";               Components: engine
Name: "desktopicon"; Description: "Create a desktop shortcut for the Control Center";                          Components: controlcenter

[Files]
Source: "daams.ico"; DestDir: "{app}"; Flags: ignoreversion
; Keep these two folders side by side: the Control Center finds ..\Engine\DAAMSResetLocal.exe.
Source: "build\engine\*";        DestDir: "{app}\Engine";        Components: engine;        Flags: recursesubdirs ignoreversion
Source: "build\controlcenter\*"; DestDir: "{app}\ControlCenter"; Components: controlcenter; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\DAAMS Control Center"; Filename: "{app}\ControlCenter\{#CCExe}"; WorkingDir: "{app}\ControlCenter"; IconFilename: "{app}\daams.ico"; Components: controlcenter
; Runs the elevated scheduled task: starts the Engine with admin rights without a UAC prompt each time.
Name: "{group}\Start DAAMS Monitoring Engine"; Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#TaskName}"""; IconFilename: "{app}\daams.ico"; Components: engine
; The Engine is hidden (no window), so give the user a way to stop it. Ends the same scheduled task.
Name: "{group}\Stop DAAMS Monitoring Engine"; Filename: "{sys}\schtasks.exe"; Parameters: "/End /TN ""{#TaskName}"""; IconFilename: "{app}\daams.ico"; Components: engine
Name: "{group}\Uninstall DAAMS";      Filename: "{uninstallexe}"
Name: "{autodesktop}\DAAMS Control Center"; Filename: "{app}\ControlCenter\{#CCExe}"; WorkingDir: "{app}\ControlCenter"; IconFilename: "{app}\daams.ico"; Tasks: desktopicon

; Same structure as windows\context_menu_setup.py (cascading "DAAMS" submenu), but machine-wide (HKLM)
; and pointing to the .exe instead of pythonw + .py.
; On your dev PC run "python windows\context_menu_setup.py uninstall" first: its per-user (HKCU) entries
; take precedence over these.
[Registry]
Root: HKLM; Subkey: "Software\Classes\*\shell\DAAMS"; Flags: uninsdeletekey; Tasks: contextmenu; ValueType: string; ValueName: "MUIVerb"; ValueData: "DAAMS"
Root: HKLM; Subkey: "Software\Classes\*\shell\DAAMS"; Tasks: contextmenu; ValueType: string; ValueName: "SubCommands"; ValueData: ""
Root: HKLM; Subkey: "Software\Classes\*\shell\DAAMS\shell\01ProtectAsset"; Tasks: contextmenu; ValueType: string; ValueName: ""; ValueData: "Protect This File"
Root: HKLM; Subkey: "Software\Classes\*\shell\DAAMS\shell\01ProtectAsset\command"; Tasks: contextmenu; ValueType: string; ValueName: ""; ValueData: """{app}\Engine\{#ProtectExe}"" ""%1"""
Root: HKLM; Subkey: "Software\Classes\Directory\shell\DAAMS"; Flags: uninsdeletekey; Tasks: contextmenu; ValueType: string; ValueName: "MUIVerb"; ValueData: "DAAMS"
Root: HKLM; Subkey: "Software\Classes\Directory\shell\DAAMS"; Tasks: contextmenu; ValueType: string; ValueName: "SubCommands"; ValueData: ""
Root: HKLM; Subkey: "Software\Classes\Directory\shell\DAAMS\shell\01ProtectAsset"; Tasks: contextmenu; ValueType: string; ValueName: ""; ValueData: "Protect This Folder"
Root: HKLM; Subkey: "Software\Classes\Directory\shell\DAAMS\shell\01ProtectAsset\command"; Tasks: contextmenu; ValueType: string; ValueName: ""; ValueData: """{app}\Engine\{#ProtectExe}"" ""%1"""

[Run]
; Engine scheduled task: elevated (needed to read the Security log for "Open" detection) and in the
; signed-in user's interactive session (needed by the clipboard watcher). Always registered when the Engine
; is installed; it only gets a sign-in trigger if the "startup" task was ticked.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$a = New-ScheduledTaskAction -Execute '{app}\Engine\{#EngineExe}' -WorkingDirectory '{app}\Engine'; $t = New-ScheduledTaskTrigger -AtLogOn -User '{username}'; $p = New-ScheduledTaskPrincipal -UserId '{username}' -LogonType Interactive -RunLevel Highest; $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero); Register-ScheduledTask -TaskName '{#TaskName}' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null"""; \
  Flags: runhidden; Components: engine; Tasks: startup; StatusMsg: "Registering Engine startup task..."
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$a = New-ScheduledTaskAction -Execute '{app}\Engine\{#EngineExe}' -WorkingDirectory '{app}\Engine'; $p = New-ScheduledTaskPrincipal -UserId '{username}' -LogonType Interactive -RunLevel Highest; $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero); Register-ScheduledTask -TaskName '{#TaskName}' -Action $a -Principal $p -Settings $s -Force | Out-Null"""; \
  Flags: runhidden; Components: engine; Tasks: not startup; StatusMsg: "Registering Engine task..."
Filename: "{app}\ControlCenter\{#CCExe}"; Description: "Launch DAAMS Control Center"; Flags: nowait postinstall skipifsilent; Components: controlcenter

[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/End /TN ""{#TaskName}"""; Flags: runhidden; RunOnceId: "EndTask"
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""{#TaskName}"" /F"; Flags: runhidden; RunOnceId: "DelTask"
Filename: "taskkill.exe"; Parameters: "/IM {#EngineExe} /F"; Flags: runhidden; RunOnceId: "KillEngine"
Filename: "taskkill.exe"; Parameters: "/IM {#CCExe} /F";     Flags: runhidden; RunOnceId: "KillCC"
; NOTE: %LOCALAPPDATA%\DAAMS (database, protected asset list, PIN, logs, key) is deliberately kept on uninstall.

[Code]
var
  KeyPage: TInputFileWizardPage;

procedure InitializeWizard;
begin
  KeyPage := CreateInputFilePage(wpSelectTasks,
    'Cloud sync (optional)',
    'Firebase service account key',
    'Select serviceAccountKey.json to enable Firestore sync. Leave blank to run local-only; DAAMS still works offline, and you can add the file later by copying it to %LOCALAPPDATA%\DAAMS.');
  KeyPage.Add('Service account key file:', 'JSON files|*.json', '.json');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Path, Name: String;
  Content: AnsiString;
begin
  Result := True;
  if CurPageID = KeyPage.ID then
  begin
    Path := KeyPage.Values[0];
    if Path = '' then Exit;

    Name := LowerCase(ExtractFileName(Path));
    if Pos('.json.json', Name) > 0 then
    begin
      MsgBox('That file name ends in .json.json (hidden extension). Please pick the real key file.', mbError, MB_OK);
      Result := False; Exit;
    end;
    if (not LoadStringFromFile(Path, Content)) or (Pos('"private_key"', String(Content)) = 0) then
    begin
      MsgBox('This does not look like a Firebase service account key.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Src, Dst: String;
begin
  if CurStep = ssPostInstall then
  begin
    Src := KeyPage.Values[0];
    if Src <> '' then
    begin
      { One copy in the user's DAAMS folder: the Engine and the Control Center both look here on their own. }
      Dst := ExpandConstant('{localappdata}\DAAMS\serviceAccountKey.json');
      ForceDirectories(ExtractFilePath(Dst));
      if not FileCopy(Src, Dst, False) then
        MsgBox('Could not copy the key file. DAAMS will run local-only until you copy it to ' + ExtractFilePath(Dst), mbInformation, MB_OK);
    end;
  end;
end;
