; WDM installer
; Requires Inno Setup 6 (https://jrsoftware.org/isinfo.php)
; Compile: ISCC.exe installer.iss

#define MyAppName "WDM"
#define MyAppShortName "WDM"
#define MyAppVersion "2.5.2.0"
#define MyAppPublisher "WDM Team"
#define MyAppExeName "WDM.exe"
#define MyAppIcon "..\WDM\Assets\WDM.ico"
#define StagingDir "..\..\staging"

[Setup]
AppId={{4F3B2C0A-8D2E-4B7A-9C1E-6A5B4D3E2F10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\{#MyAppShortName}
DefaultGroupName={#MyAppShortName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\..\output
OutputBaseFilename=WDM_Setup_{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#MyAppIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CloseApplications=force
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
AlwaysRestart=no
RestartIfNeededByRun=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startwithwindows"; Description: "Start {#MyAppName} when Windows starts (minimized to the system tray)"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Ship the complete publish output: the apphost, managed dlls, the
; Microsoft.Web.WebView2.* assemblies and runtimes\<arch>\native\WebView2Loader.dll
; (a previous file list that named only five root files silently dropped WebView2,
; which broke YouTube sign-in from installed copies).
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[UninstallDelete]
Type: filesandordirs; Name: "{app}\bin"
Type: filesandordirs; Name: "{app}\BrowserExtension"
Type: filesandordirs; Name: "{app}\WebView2"
Type: filesandordirs; Name: "{app}\engines"
Type: files; Name: "{app}\*"
Type: filesandordirs; Name: "{app}"

[Registry]
; No registry entries. Chrome/Edge: manual "Load unpacked" install (see the in-app
; step-by-step guide). Firefox: install from the add-ons store (AMO) once approved.

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "/minimized"; Tasks: startwithwindows

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure KillProcess(const ExeName: String);
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/f /im ' + ExeName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure KillAllProcesses;
begin
  KillProcess('WDM.exe');
  KillProcess('yt-dlp.exe');
  KillProcess('ffmpeg.exe');
  KillProcess('ffprobe.exe');
  KillProcess('qjs.exe');
end;

procedure CleanRegistryKeys;
begin
  // Startup Run values
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'WDM');
  RegDeleteValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Run', 'WDM');

  // App Paths
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\App Paths\WDM.exe');
  RegDeleteKeyIncludingSubkeys(HKLM, 'Software\Microsoft\Windows\CurrentVersion\App Paths\WDM.exe');

  // WDM Software keys
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\WDM');
  RegDeleteKeyIncludingSubkeys(HKLM, 'Software\WDM');
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\WOW6432Node\WDM');

  // WMI / Wbem tracing keys
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\Microsoft\Wbem\WDM');
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\Microsoft\Wbem\CORS\WDM');
  RegDeleteKeyIncludingSubkeys(HKCU, 'SOFTWARE\Microsoft\Wbem\WDM');

  // RADAR / AppID / Error Reporting traces
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\Microsoft\RADAR\HeapLeakDetection\DiagnosedApplications\WDM.exe');
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\Classes\AppID\WDM.exe');
  RegDeleteKeyIncludingSubkeys(HKCU, 'SOFTWARE\Classes\AppID\WDM.exe');

  // Inno Setup Uninstall registry keys
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4F3B2C0A-8D2E-4B7A-9C1E-6A5B4D3E2F10}_is1');
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{4F3B2C0A-8D2E-4B7A-9C1E-6A5B4D3E2F10}_is1');
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{4F3B2C0A-8D2E-4B7A-9C1E-6A5B4D3E2F10}_is1');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    KillAllProcesses;
    // Do NOT delete AppDir on updates!
    // AppDir ({app}) holds runtime user data: YouTube sign-in profile (WebView2),
    // YouTube cookies (youtube_cookies.txt), downloaded engine plugins (bin/yt-dlp.exe, etc.),
    // and user tasks/settings (tasks.json/settings.json).
    // Inno Setup safely overwrites app binaries from [Files] while leaving user data untouched.
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir: String;
  FindRec: TFindRec;
  TempDir: String;
  ResultCode: Integer;
  CmdArgs: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    KillAllProcesses;
    CleanRegistryKeys;
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    AppDir := ExpandConstant('{app}');
    
    // Clear read-only/system/hidden attributes on any leftover files in AppDir
    if DirExists(AppDir) then
    begin
      Exec('attrib.exe', '-r -h -s "' + AppDir + '\*.*" /s /d', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      DelTree(AppDir, True, True, True);
      RemoveDir(AppDir);
    end;

    // Clean up temporary setup installers in %TEMP%
    TempDir := GetTempDir;
    if FindFirst(TempDir + 'WDM_Setup_*.exe', FindRec) then
    begin
      try
        repeat
          DeleteFile(TempDir + FindRec.Name);
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;

    // Additional safeguard: If AppDir still exists (e.g. unins000.exe was executing inside it),
    // launch a background cmd process to remove AppDir 2 seconds after unins000.exe terminates.
    if DirExists(AppDir) then
    begin
      CmdArgs := '/c timeout /t 2 /nobreak >nul & rmdir /s /q "' + AppDir + '"';
      Exec('cmd.exe', CmdArgs, '', SW_HIDE, ewNoWait, ResultCode);
    end;
  end;
end;