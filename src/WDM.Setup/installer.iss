; WDM installer - per-machine (admin).
; Delta updates (patch-only, no wizard) are handled by Velopack (src/WDM/Services/VelopackUpdateService.cs)
; via GitHub Releases nupkg/RELEASES. This Inno installer remains for new users and as fallback
; for portable/dev builds where Velopack is not active. It installs per-machine to {autopf}\WDM
; (admin required) and preserves user data on updates (see CurStepChanged).
; USER DATA SAFETY: since the data-home move, tasks.json / settings.json / cookies /
; downloaded engines / WebView2 profile live in %LOCALAPPDATA%\WDM-Data (TaskStore.AppDir),
; OUTSIDE {app}. Uninstall wipes {app} only and must never touch WDM-Data.
; BROWSER POLICY (retired): the CRX force-install policy
; (ExtensionInstallForcelist + ExtensionInstallSources) is no longer written.
; The file:// policy install fought the manual Load unpacked install (same
; extension ID): on reboot Chrome re-ran the policy repair from the CRX and the
; unpacked extension disappeared. Load unpacked from the stable per-user folder
; %LOCALAPPDATA%\WDM\BrowserExtension (same pinned key = same ID, no update_url
; so no dead update check) is the only supported Chromium pathway; Firefox uses
; the AMO store listing. On install we REMOVE our own stale policy entries from
; previous versions so the old policy CRX stops shadowing the unpacked install.
; Uninstall still removes them via RemoveForceInstallPolicy. WriteForceInstallPolicy
; is kept (unused) for reference. The private key (*.pem) is NEVER shipped -
; [Files] excludes it.
; Requires Inno Setup 6 (https://jrsoftware.org/isinfo.php)
; Compile: ISCC.exe installer.iss
; Velopack pack (delta): dotnet publish -> vpk pack --packId WDM --packVersion 2.8.1 ...

#define MyAppName "WDM"
#define MyAppShortName "WDM"
; Command-line /dMyAppVersion=... (build-test-install.ps1) overrides this;
; script-level default stays for plain ISCC.exe runs.
#ifndef MyAppVersion
#define MyAppVersion "2.8.1.0"
#endif
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
DefaultDirName={autopf}\{#MyAppShortName}
DefaultGroupName={#MyAppShortName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
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
Name: "startwithwindows"; Description: "Start {#MyAppName} when Windows starts (for all users)"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Ship the complete publish output: the apphost, managed dlls, the
; Microsoft.Web.WebView2.* assemblies and runtimes\<arch>\native\WebView2Loader.dll
; (a previous file list that named only five root files silently dropped WebView2,
; which broke YouTube sign-in from installed copies).
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pem"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\bin"
Type: filesandordirs; Name: "{app}\BrowserExtension"
Type: filesandordirs; Name: "{app}\WebView2"
Type: filesandordirs; Name: "{app}\engines"
Type: files; Name: "{app}\*"
Type: filesandordirs; Name: "{app}"

[Registry]
; Browser force-install policies are written by [Code] WriteForceInstallPolicy
; (free-index search under HKLM Policies, so we never clobber another product's
; value "1"). Static entries are intentionally avoided here. Cleanup happens in
; CurUninstallStepChanged via RemoveForceInstallPolicy. Firefox stays on the
; AMO store listing + manual install (unsigned XPI cannot be policy-installed).
; Machine-wide auto-start (no per-user areas: the installer runs elevated, so a
; {userstartup} shortcut would land in the admin's profile, not the user's).
; Tied to the "startwithwindows" task; removed on uninstall (uninsdeletevalue +
; CleanRegistryKeys below). Note the in-app Options toggle manages a separate
; per-user HKCU Run value; both set = single-instance Mutex dedupes at logon.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppShortName}"; ValueData: """{app}\{#MyAppExeName}"" /minimized"; Tasks: startwithwindows; Flags: uninsdeletevalue

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WdmExtId = 'jehagbjolooaohcbmlhegpmjeaakonof';
  WdmChromePolicyKey = 'Software\Policies\Google\Chrome';
  WdmEdgePolicyKey = 'Software\Policies\Microsoft\Edge';

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

{ Converts a local path to a file:// URL (forward slashes, spaces encoded). }
function WdmFileUrl(const Path: String): String;
var
  S: String;
begin
  S := Path;
  StringChangeEx(S, '\', '/', True);
  StringChangeEx(S, ' ', '%20', True);
  Result := 'file:///' + S;
end;

{ Finds a value name under Subkey that already holds our entry (prefix match)
  or the first free numeric slot ("1".."99"). Returns '' when full. }
function FindPolicySlot(const Subkey, WantPrefix: String): String;
var
  I: Integer;
  Name, Val: String;
begin
  Result := '';
  for I := 1 to 99 do
  begin
    Name := IntToStr(I);
    if RegQueryStringValue(HKLM, Subkey, Name, Val) then
    begin
      if Pos(WantPrefix, Val) = 1 then
      begin
        Result := Name;
        Exit;
      end;
    end
    else
    begin
      Result := Name;
      Exit;
    end;
  end;
end;

procedure WriteForceInstallPolicy(const VendorKey: String);
var
  ListKey, SrcKey, Slot, ExtDirUrl: String;
begin
  ExtDirUrl := WdmFileUrl(ExpandConstant('{app}\BrowserExtension'));
  ListKey := VendorKey + '\ExtensionInstallForcelist';
  SrcKey := VendorKey + '\ExtensionInstallSources';
  Slot := FindPolicySlot(ListKey, WdmExtId + ';');
  if Slot <> '' then
    RegWriteStringValue(HKLM, ListKey, Slot, WdmExtId + ';' + ExtDirUrl + '/update.xml');
  Slot := FindPolicySlot(SrcKey, ExtDirUrl + '/');
  if Slot <> '' then
    RegWriteStringValue(HKLM, SrcKey, Slot, ExtDirUrl + '/*');
end;

{ Counts occurrences of Sub in S (Inno Pascal has no string-count helper). }
function CountOccurrences(const Sub, S: String): Integer;
var
  Rest: String;
  P: Integer;
begin
  Result := 0;
  Rest := S;
  P := Pos(Sub, Rest);
  while P > 0 do
  begin
    Result := Result + 1;
    Rest := Copy(Rest, P + Length(Sub), Length(Rest));
    P := Pos(Sub, Rest);
  end;
end;

{ ExtensionSettings is a single shared JSON value: delete it only when WDM is
  almost certainly the sole tenant (our ID present, at most one
  installation_mode block, i.e. the shape our own tooling wrote); a shared
  multi-product value is left for the admin rather than clobbered. }
procedure RemoveOurExtensionSettings(const Root: Integer; const VendorKey: String);
var
  Val: String;
begin
  if not RegQueryStringValue(Root, VendorKey, 'ExtensionSettings', Val) then
    Exit;
  if Pos(WdmExtId, Val) = 0 then
    Exit;
  if CountOccurrences('"installation_mode"', Val) <= 1 then
    RegDeleteValue(Root, VendorKey, 'ExtensionSettings');
end;

{ Deletes only values that belong to WDM (prefix/footprint match), leaving other
  products' policy entries untouched. Covers HKCU (old per-user/dev writes)
  plus the HKLM 64-bit and 32-bit views. }
procedure RemoveForceInstallPolicyAt(const Root: Integer; const VendorKey: String);
var
  ListKey, SrcKey, Val: String;
  Names: TArrayOfString;
  I: Integer;
begin
  ListKey := VendorKey + '\ExtensionInstallForcelist';
  SrcKey := VendorKey + '\ExtensionInstallSources';
  if RegGetValueNames(Root, ListKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(Root, ListKey, Names[I], Val) then
        if Pos(WdmExtId + ';', Val) = 1 then
          RegDeleteValue(Root, ListKey, Names[I]);
  if RegGetValueNames(Root, SrcKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(Root, SrcKey, Names[I], Val) then
        { Path-independent footprint match: old installs pointed at
          %LocalAppData%\WDM, new ones at {app}; matching the current URL
          would miss exactly the stale entries that shadow the unpacked load. }
        if (Pos('WDM', Val) > 0) and (Pos('BrowserExtension', Val) > 0) then
          RegDeleteValue(Root, SrcKey, Names[I]);
  RemoveOurExtensionSettings(Root, VendorKey);
end;

procedure RemoveForceInstallPolicy(const VendorKey: String);
begin
  RemoveForceInstallPolicyAt(HKCU, VendorKey);
  RemoveForceInstallPolicyAt(HKLM, VendorKey);
  RemoveForceInstallPolicyAt(HKLM32, VendorKey);
end;

{ One-time move from the legacy per-user install (%LocalAppData%\WDM).
  Removes old binaries + the old per-user Add/Remove entry. User data
  (*.json), engines and the deployed BrowserExtension copy are preserved:
  the app re-deploys/refreshes them from the install dir on next launch. }
procedure MigrateOldPerUserInstall;
var
  OldRoot, NewRoot: String;
begin
  NewRoot := ExpandConstant('{app}');
  OldRoot := ExpandConstant('{localappdata}\WDM');
  if (CompareText(OldRoot, NewRoot) = 0) or (not DirExists(OldRoot)) then
    Exit;
  DeleteFile(OldRoot + '\WDM.exe');
  DeleteFile(OldRoot + '\unins000.exe');
  DeleteFile(OldRoot + '\unins000.dat');
  DelTree(OldRoot + '\runtimes', True, True, True);
  DelTree(OldRoot + '\bin', True, True, True);
  DelTree(OldRoot + '\WebView2', True, True, True);
  RegDeleteKeyIncludingSubkeys(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4F3B2C0A-8D2E-4B7A-9C1E-6A5B4D3E2F10}_is1');
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
    MigrateOldPerUserInstall;
    // Do NOT delete AppDir on updates!
    // Binaries ship from [Files]; user data (tasks.json, settings.json, engines,
    // WebView2 profile) lives OUTSIDE {app} in %LOCALAPPDATA%\WDM-Data, so an
    // install or uninstall can no longer take the download list with it.
    // (The {app}\bin / WebView2 entries under [UninstallDelete] only clean up
    // leftovers from pre-move installs and bundled content.)
  end;
  if CurStep = ssPostInstall then
  begin
    // Unpacked-only pathway: remove our own stale force-install policy entries
    // from previous versions (same extension ID) so the policy CRX no longer
    // shadows/repairs-over the manual Load unpacked install on reboot.
    RemoveForceInstallPolicy(WdmChromePolicyKey);
    RemoveForceInstallPolicy(WdmEdgePolicyKey);
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
    RemoveForceInstallPolicy(WdmChromePolicyKey);
    RemoveForceInstallPolicy(WdmEdgePolicyKey);
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
