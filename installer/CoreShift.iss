; CoreShift — Inno Setup script
; Produces a single, commercial-ready installer (CoreShift-Setup-<ver>.exe).
; Build with:  build\build-installer.ps1   (or)   iscc installer\CoreShift.iss

#define MyAppName "CoreShift"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "CoreShift"
#define MyAppExeName "CoreShift.exe"
#define MyAppId "{{B7E6F3A2-2D4B-4C1A-9E77-A1B2C3D4E5F6}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir=..\installer_output
OutputBaseFilename=CoreShift-Setup-{#MyAppVersion}
SetupIconFile=..\src\CoreShift\Assets\CoreShift.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\dist\CoreShift.exe";                 DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\CoreShift\Assets\CoreShift.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";          Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the auto-start entry and per-user marker on uninstall (best-effort).
Filename: "{cmd}"; Parameters: "/c reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v CoreShift /f"; Flags: runhidden; RunOnceId: "DelRun"
Filename: "{cmd}"; Parameters: "/c reg delete ""HKCU\Software\CoreShift"" /f";                                          Flags: runhidden; RunOnceId: "DelApp"

[Code]
{ ---- .NET 8 Desktop Runtime detection & on-demand install ---- }

function IsDotNetDesktop8Installed(): Boolean;
var
  FindRec: TFindRec;
  BasePath: String;
begin
  Result := False;
  BasePath := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(BasePath + '\8.*', FindRec) then
  begin
    try
      Result := True;
    finally
      FindClose(FindRec);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Url, TempExe, PsCmd: String;
begin
  Result := '';
  NeedsRestart := False;

  if IsDotNetDesktop8Installed() then
    Exit;

  if MsgBox('CoreShift needs the Microsoft .NET 8 Desktop Runtime, which is not installed on this PC.'
      + #13#10#13#10 + 'Download and install it now (about 55 MB)?',
      mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Result := 'CoreShift cannot run without the .NET 8 Desktop Runtime.';
    Exit;
  end;

  Url     := 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  TempExe := ExpandConstant('{tmp}\windowsdesktop-runtime-8-x64.exe');
  PsCmd   := '-NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri ''' + Url + ''' -OutFile ''' + TempExe + '''"';

  if not (Exec('powershell.exe', PsCmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
  begin
    Result := 'Could not download the .NET 8 Desktop Runtime. Install it manually from'
      + #13#10 + 'https://dotnet.microsoft.com/download/dotnet/8.0 and run this setup again.';
    Exit;
  end;

  if Exec(TempExe, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    if (ResultCode <> 0) and (ResultCode <> 3010) then
      Result := 'The .NET 8 Desktop Runtime installer reported error code ' + IntToStr(ResultCode) + '.';
    if ResultCode = 3010 then
      NeedsRestart := True;
  end
  else
    Result := 'Failed to launch the .NET 8 Desktop Runtime installer.';
end;
