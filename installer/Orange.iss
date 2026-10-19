; Inno Setup script: per-user install, no admin rights, no services, no drivers.
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\Orange"
#endif

[Setup]
ChangesEnvironment=yes
AppId={{6B1E0C55-8F7A-4D8E-9C1E-0A7E0F0A0001}
AppName=Orange
AppVersion={#AppVersion}
AppPublisher=Orange contributors
DefaultDirName={localappdata}\Programs\Orange
DefaultGroupName=Orange
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\out
OutputBaseFilename=Orange-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\Orange.App\Assets\orange.ico
UninstallDisplayIcon={app}\Orange.exe
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=4
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE

[Tasks]
Name: "sendto"; Description: "Add Orange to the Send to menu"
Name: "contextmenu"; Description: "Add ""Convert with Orange"" to the right-click menu"
Name: "clipath"; Description: "Add the orange command to PATH (for terminals and scripts)"; Flags: unchecked
Name: "autostart"; Description: "Start Orange when I sign in (needed for Shift-drag)"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{userprograms}\Orange"; Filename: "{app}\Orange.exe"
Name: "{usersendto}\Orange (convert)"; Filename: "{app}\Orange.exe"; Parameters: "--wheel"; Tasks: sendto

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\cli"; Tasks: clipath; Check: NeedsCliPath(ExpandConstant('{app}\cli'))
Root: HKCU; Subkey: "Software\Classes\*\shell\Orange"; ValueType: string; ValueName: ""; ValueData: "Convert with Orange"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Orange"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Orange.exe"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Orange"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Orange\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Orange.exe"" --wheel ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Orange"; ValueData: """{app}\Orange.exe"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\Orange.exe"; Description: "Start Orange"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/im Orange.exe /f"; Flags: runhidden; RunOnceId: "StopOrange"

[UninstallDelete]
Type: files; Name: "{usersendto}\Orange (convert).lnk"

[Code]
function NeedsCliPath(Dir: string): Boolean;
var Current: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Current) then Current := '';
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Current) + ';') = 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Current, Dir: string; P: Integer;
begin
  if CurUninstallStep <> usPostUninstall then exit;
  Dir := ExpandConstant('{app}\cli');
  if RegQueryStringValue(HKCU, 'Environment', 'Path', Current) then
  begin
    P := Pos(';' + Uppercase(Dir), Uppercase(Current));
    if P > 0 then
    begin
      Delete(Current, P, Length(Dir) + 1);
      RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Current);
    end;
  end;
end;
