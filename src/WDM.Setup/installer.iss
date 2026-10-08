; WDM installer - per-machine (admin).
; Delta updates (patch-only, no wizard) are handled by Velopack (src/WDM.App/Services/VelopackUpdateService.cs)
; via GitHub Releases nupkg/RELEASES. This Inno installer remains for new users and as fallback
; for portable/dev builds where Velopack is not active. It installs per-machine to {autopf}\WDM
; (admin required).
; INSTALL preserves user data (see CurStepChanged): tasks.json / settings.json / cookies /
; downloaded engines / WebView2 profile live in %LOCALAPPDATA%\WDM-Data (TaskStore.AppDir),
; OUTSIDE {app}, so updates never take the download list with them.
; UNINSTALL IS A FULL WIPE: uninstall deletes {app} AND every WDM trace on the machine —
; %LOCALAPPDATA%\WDM + %LOCALAPPDATA%\WDM-Data (+ roaming counterparts) for EVERY local
; profile (uninstall runs elevated, so {localappdata} alone would miss the real user),
; per-user temp artifacts, all WDM registry values (Run, App Paths, Software\WDM,
; tracing keys, uninstall entries) across HKCU/HKLM/32-bit views and every user hive,
; plus our own browser policy entries. There is no opt-out: uninstall means gone.
; BROWSER POLICY (retired): the CRX force-install policy
; (ExtensionInstallForcelist + ExtensionInstallSources) is no longer written.
; The file:// policy install fought the manual Load unpacked install (same
; extension ID): on reboot Chrome re-ran the policy repair from the CRX and the
; unpacked extension disappeared. Load unpacked from the stable per-user folder
; %LOCALAPPDATA%\WDM\BrowserExtension (same pinned key = same ID, no update_url
; so no dead update check) is the only supported Chromium pathway; Firefox uses
; the AMO store listing. On install we REMOVE our own stale policy entries from
; previous versions so the old policy CRX stops shadowing the unpacked install.
; Uninstall still removes them via RemoveForceInstallPolicy. The write path is
; gone (no CRX is built or referenced anywhere); only the removal side remains.
; The private key (*.pem) is NEVER shipped - [Files] excludes it.
; Requires Inno Setup 6 (https://jrsoftware.org/isinfo.php)
; Compile: ISCC.exe installer.iss
; Velopack pack (delta): dotnet publish -> vpk pack --packId WDM --packVersion 2.8.1 ...

#define MyAppName "WDM"
#define MyAppShortName "WDM"
; Command-line /dMyAppVersion=... (build-test-install.ps1) overrides this;
; script-level default stays for plain ISCC.exe runs.
#ifndef MyAppVersion
#define MyAppVersion "2.8.10.0"
#endif
#define MyAppPublisher "WDM Team"
#define MyAppExeName "WDM.exe"
#define MyAppIcon "..\WDM.App\Assets\WDM.ico"
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
; No force-install policy is written anymore (Load unpacked is the only Chromium
; pathway). Stale entries from previous versions are removed by [Code]
; RemoveForceInstallPolicy on install and uninstall. Firefox stays on the
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

; Explorer verb (registry command, no COM): right-click any file →
; "Download with WDM" imports it as a managed local copy. Removed on
; uninstall (uninsdeletekey + CleanRegistryKeys belt-and-suspenders).
[Registry]
Root: HKCR; Subkey: "*\shell\WDM.Download"; ValueType: string; ValueName: ""; ValueData: "Download with WDM"; Flags: uninsdeletekey
Root: HKCR; Subkey: "*\shell\WDM.Download"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"; Flags: uninsdeletekey
Root: HKCR; Subkey: "*\shell\WDM.Download\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey

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
  KillProcess('WDM.BrowserHost.exe');
  KillProcess('CefSharp.BrowserSubprocess.exe');
  KillProcess('yt-dlp.exe');
  KillProcess('ffmpeg.exe');
  KillProcess('ffprobe.exe');
  KillProcess('qjs.exe');
end;

{ Best-effort recursive delete: clear R/H/S attributes first (engines, WebView2
  and CEF profiles ship read-only files that DelTree would otherwise leave). }
procedure WipeDir(const Dir: String);
var
  ResultCode: Integer;
begin
  if (Dir = '') or (not DirExists(Dir)) then
    Exit;
  Exec('attrib.exe', '-r -h -s "' + Dir + '\*.*" /s /d', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  DelTree(Dir, True, True, True);
  RemoveDir(Dir);
end;

{ Deletes every file/dir in TempDir matching Pattern (dirs recursed via WipeDir). }
procedure WipeTempMatch(const TempDir, Pattern: String);
var
  FindRec: TFindRec;
  P: String;
begin
  if (TempDir = '') or (not DirExists(TempDir)) then
    Exit;
  if FindFirst(TempDir + '\' + Pattern, FindRec) then
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        P := TempDir + '\' + FindRec.Name;
        if DirExists(P) then
          WipeDir(P)
        else
          DeleteFile(P);
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

{ Our temp footprint: BrowserHost CEF profiles (wdm_bh_*), capture/chrome
  helpers (wdm-browsercatch, wdm-chrome-*), setup exes, fallback activity log.
  The wdm_*/WDM_* prefix is product-specific (Windows matching is
  case-insensitive, so one pattern would do — both are listed for clarity). }
procedure WipeTempDir(const TempDir: String);
begin
  if (TempDir = '') or (not DirExists(TempDir)) then
    Exit;
  WipeTempMatch(TempDir, 'WDM_Setup_*.exe');
  WipeTempMatch(TempDir, 'WDM-Setup-*.exe');
  WipeTempMatch(TempDir, 'WDM-User-Setup-*.exe');
  WipeTempMatch(TempDir, 'WDM-Full-Setup-*.exe');
  WipeTempMatch(TempDir, 'WDM-activity.log*');
  WipeTempMatch(TempDir, 'wdm_*');
  WipeTempMatch(TempDir, 'WDM_*');
end;

{ Wipes all WDM data for one profile: deployed extension + token (WDM),
  tasks/settings/cookies/engines/WebView2/token (WDM-Data), roaming
  counterparts (unused today, swept for completeness), temp artifacts. }
procedure WipeProfileWdmData(const LocalData, RoamingData, TempDir: String);
begin
  WipeDir(LocalData + '\WDM');
  WipeDir(LocalData + '\WDM-Data');
  WipeDir(RoamingData + '\WDM');
  WipeDir(RoamingData + '\WDM-Data');
  WipeTempDir(TempDir);
end;

{ Full data wipe across every local profile. Uninstall runs elevated as admin,
  so LOCALAPPDATA alone points at the admin profile and would miss the real
  user's data — enumerate C:\Users instead. Skips shared/template profiles. }
procedure WipeAllProfilesWdmData;
var
  UsersRoot, Profile, LocalData: String;
  EnvData: String;
  FindRec: TFindRec;
begin
  { Current context first (covers the elevating admin): }
  WipeProfileWdmData(ExpandConstant('{localappdata}'),
    ExpandConstant('{userappdata}'), GetTempDir);
  { Custom data home (AppPaths honors WDM_DATA_DIR when set): }
  EnvData := ExpandConstant('{%WDM_DATA_DIR|}');
  if (EnvData <> '') and DirExists(EnvData) then
    WipeDir(EnvData);
  { Every local profile: }
  UsersRoot := ExpandConstant('{sd}\Users');
  if not DirExists(UsersRoot) then
    UsersRoot := 'C:\Users';
  if FindFirst(UsersRoot + '\*', FindRec) then
  try
    repeat
      if (FindRec.Name = '.') or (FindRec.Name = '..') then
      begin
        { skip dot entries }
      end
      else if (CompareText(FindRec.Name, 'Public') = 0) or
        (CompareText(FindRec.Name, 'Default') = 0) or
        (CompareText(FindRec.Name, 'Default User') = 0) or
        (CompareText(FindRec.Name, 'All Users') = 0) then
      begin
        { skip shared/template profiles }
      end
      else
      begin
        Profile := UsersRoot + '\' + FindRec.Name;
        LocalData := Profile + '\AppData\Local';
        if DirExists(LocalData) then
          WipeProfileWdmData(LocalData, Profile + '\AppData\Roaming',
            LocalData + '\Temp');
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

{ Surgical removal of our own ExtensionSettings entry. ExtensionSettings is a
  single shared JSON object keyed by extension ID, potentially owned by other
  products or enterprise policy. Never delete the whole value when other
  tenants exist: cut out only our key (brace-matched, string-aware) and write
  the   remainder back; delete the value only when nothing (or an empty object) remains.
  On any parse uncertainty, leave the value for the admin (returns False). }
function TryRemoveWdmKey(const Val, ExtId: String; var NewVal: String): Boolean;
var
  KeyQuoted: String;
  KeyPos, ColonPos, ObjStart, ObjEnd, SliceStart, SliceEnd: Integer;
  I, Depth: Integer;
  InStr, Esc: Boolean;
  Ch: Char;
begin
  Result := False;
  NewVal := Val;
  KeyQuoted := '"' + ExtId + '"';
  KeyPos := Pos(KeyQuoted, Val);
  if KeyPos = 0 then
    Exit;
  ColonPos := 0;
  for I := KeyPos + Length(KeyQuoted) to Length(Val) do
  begin
    if Val[I] = ':' then
    begin
      ColonPos := I;
      Break;
    end;
    if (Val[I] <> ' ') and (Val[I] <> #9) and (Val[I] <> #13) and (Val[I] <> #10) then
      Exit;
  end;
  if ColonPos = 0 then
    Exit;
  ObjStart := 0;
  for I := ColonPos + 1 to Length(Val) do
  begin
    if Val[I] = '{' then
    begin
      ObjStart := I;
      Break;
    end;
    if (Val[I] <> ' ') and (Val[I] <> #9) and (Val[I] <> #13) and (Val[I] <> #10) then
      Exit;
  end;
  if ObjStart = 0 then
    Exit;
  Depth := 0;
  InStr := False;
  Esc := False;
  ObjEnd := 0;
  for I := ObjStart to Length(Val) do
  begin
    Ch := Val[I];
    if InStr then
    begin
      if Esc then
        Esc := False
      else if Ch = '\' then
        Esc := True
      else if Ch = '"' then
        InStr := False;
    end
    else
    begin
      if Ch = '"' then
        InStr := True
      else if Ch = '{' then
        Depth := Depth + 1
      else if Ch = '}' then
      begin
        Depth := Depth - 1;
        if Depth = 0 then
        begin
          ObjEnd := I;
          Break;
        end;
      end;
    end;
  end;
  if ObjEnd = 0 then
    Exit;
  SliceStart := KeyPos;
  while (SliceStart > 1) and ((Val[SliceStart - 1] = ' ') or (Val[SliceStart - 1] = #9) or (Val[SliceStart - 1] = #13) or (Val[SliceStart - 1] = #10)) do
    SliceStart := SliceStart - 1;
  SliceEnd := ObjEnd;
  I := SliceEnd + 1;
  while (I <= Length(Val)) and ((Val[I] = ' ') or (Val[I] = #9) or (Val[I] = #13) or (Val[I] = #10)) do
    I := I + 1;
  if (I <= Length(Val)) and (Val[I] = ',') then
    SliceEnd := I
  else
  begin
    I := SliceStart - 1;
    while (I >= 1) and ((Val[I] = ' ') or (Val[I] = #9) or (Val[I] = #13) or (Val[I] = #10)) do
      I := I - 1;
    if (I >= 1) and (Val[I] = ',') then
      SliceStart := I;
  end;
  NewVal := Copy(Val, 1, SliceStart - 1) + Copy(Val, SliceEnd + 1, Length(Val));
  Result := True;
end;

procedure RemoveOurExtensionSettings(const Root: Integer; const VendorKey: String);
var
  Val, NewVal, Trimmed: String;
begin
  if not RegQueryStringValue(Root, VendorKey, 'ExtensionSettings', Val) then
    Exit;
  if Pos(WdmExtId, Val) = 0 then
    Exit;
  { Uncertain JSON shape -> leave for the admin, never clobber. }
  if not TryRemoveWdmKey(Val, WdmExtId, NewVal) then
    Exit;
  Trimmed := Trim(NewVal);
  if (Trimmed = '') or (Trimmed = '{}') then
    RegDeleteValue(Root, VendorKey, 'ExtensionSettings')
  else
    RegWriteStringValue(Root, VendorKey, 'ExtensionSettings', NewVal);
end;

{ Numeric forcelist slot helpers: Chrome reads ExtensionInstallForcelist as a
  contiguous 1..N list. Deleting our slot without compacting leaves a gap
  (e.g. only "2" remains) that can hide other products' entries, looking like
  we deleted them. Compact back to 1..N after our removal. }
function SlotNum(const Name: String; var N: Integer): Boolean;
var
  I, V: Integer;
begin
  Result := False;
  N := 0;
  if Length(Name) = 0 then
    Exit;
  V := 0;
  for I := 1 to Length(Name) do
  begin
    if (Name[I] < '0') or (Name[I] > '9') then
      Exit;
    V := V * 10 + (Ord(Name[I]) - Ord('0'));
    if V > 99 then
      Exit;
  end;
  if V < 1 then
    Exit;
  N := V;
  Result := True;
end;

procedure CompactForcelist(const Root: Integer; const ListKey: String);
var
  Names: TArrayOfString;
  Nums: array of Integer;
  Vals: array of String;
  I, J, N, TmpN: Integer;
  TmpV, Vv: String;
  Contig: Boolean;
begin
  if not RegGetValueNames(Root, ListKey, Names) then
    Exit;
  SetArrayLength(Nums, 0);
  SetArrayLength(Vals, 0);
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    if SlotNum(Names[I], N) then
      if RegQueryStringValue(Root, ListKey, Names[I], Vv) then
      begin
        J := GetArrayLength(Nums);
        SetArrayLength(Nums, J + 1);
        SetArrayLength(Vals, J + 1);
        Nums[J] := N;
        Vals[J] := Vv;
      end;
  end;
  N := GetArrayLength(Nums);
  if N = 0 then
    Exit;
  for I := 1 to N - 1 do
  begin
    TmpN := Nums[I];
    TmpV := Vals[I];
    J := I - 1;
    while (J >= 0) and (Nums[J] > TmpN) do
    begin
      Nums[J + 1] := Nums[J];
      Vals[J + 1] := Vals[J];
      J := J - 1;
    end;
    Nums[J + 1] := TmpN;
    Vals[J + 1] := TmpV;
  end;
  Contig := True;
  for I := 0 to N - 1 do
    if Nums[I] <> I + 1 then
    begin
      Contig := False;
      Break;
    end;
  if Contig then
    Exit;
  for I := 0 to N - 1 do
    RegDeleteValue(Root, ListKey, IntToStr(Nums[I]));
  for I := 0 to N - 1 do
    RegWriteStringValue(Root, ListKey, IntToStr(I + 1), Vals[I]);
end;

{ WDM source-URL footprint: require the joined path segment so unrelated paths
  that merely contain both words elsewhere never match. Covers filesystem
  (WDM\BrowserExtension) and file:// URL (WDM/BrowserExtension) forms, both
  the legacy %LocalAppData%\WDM and the per-machine install dir. }
function IsWdmSourceUrl(const Val: String): Boolean;
begin
  Result := (Pos('WDM\BrowserExtension', Val) > 0) or (Pos('WDM/BrowserExtension', Val) > 0);
end;

{ Deletes only values that belong to WDM (prefix/footprint match), leaving other
  products' policy entries untouched. Covers HKCU (old per-user/dev writes)
  plus the HKLM 64-bit and 32-bit views. }
procedure RemoveForceInstallPolicyAt(const Root: Integer; const VendorKey: String);
var
  ListKey, SrcKey, Val: String;
  Names: TArrayOfString;
  I: Integer;
  DeletedForcelist: Boolean;
begin
  ListKey := VendorKey + '\ExtensionInstallForcelist';
  SrcKey := VendorKey + '\ExtensionInstallSources';
  DeletedForcelist := False;
  if RegGetValueNames(Root, ListKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(Root, ListKey, Names[I], Val) then
        if Pos(WdmExtId + ';', Val) = 1 then
        begin
          RegDeleteValue(Root, ListKey, Names[I]);
          DeletedForcelist := True;
        end;
  { Keep the list contiguous so remaining third-party entries still apply. }
  if DeletedForcelist then
    CompactForcelist(Root, ListKey);
  if RegGetValueNames(Root, SrcKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(Root, SrcKey, Names[I], Val) then
        { Path-independent footprint match: old installs pointed at
          %LocalAppData%\WDM, new ones at the install dir; matching the current URL
          would miss exactly the stale entries that shadow the unpacked load. }
        if IsWdmSourceUrl(Val) then
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
  // Startup Run values (installer task + in-app Options toggle share value 'WDM').
  // Per-user hives (HKCU + every HKEY_USERS SID) are handled in CleanAllUserHives.
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'WDM');
  RegDeleteValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Run', 'WDM');
  RegDeleteValue(HKLM32, 'Software\Microsoft\Windows\CurrentVersion\Run', 'WDM');

  // App Paths
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\App Paths\WDM.exe');
  RegDeleteKeyIncludingSubkeys(HKLM, 'Software\Microsoft\Windows\CurrentVersion\App Paths\WDM.exe');
  RegDeleteKeyIncludingSubkeys(HKLM32, 'Software\Microsoft\Windows\CurrentVersion\App Paths\WDM.exe');

  // WDM Software keys (both views)
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\WDM');
  RegDeleteKeyIncludingSubkeys(HKLM, 'Software\WDM');
  RegDeleteKeyIncludingSubkeys(HKLM32, 'Software\WDM');

  // WMI / Wbem tracing keys (both views + HKCU)
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\Microsoft\Wbem\WDM');
  RegDeleteKeyIncludingSubkeys(HKLM32, 'SOFTWARE\Microsoft\Wbem\WDM');
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\Microsoft\Wbem\CORS\WDM');
  RegDeleteKeyIncludingSubkeys(HKLM32, 'SOFTWARE\Microsoft\Wbem\CORS\WDM');
  RegDeleteKeyIncludingSubkeys(HKCU, 'SOFTWARE\Microsoft\Wbem\WDM');

  // RADAR / AppID / Error Reporting traces (both views + HKCU)
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\Microsoft\RADAR\HeapLeakDetection\DiagnosedApplications\WDM.exe');
  RegDeleteKeyIncludingSubkeys(HKLM32, 'SOFTWARE\Microsoft\RADAR\HeapLeakDetection\DiagnosedApplications\WDM.exe');
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\Classes\AppID\WDM.exe');
  RegDeleteKeyIncludingSubkeys(HKLM32, 'SOFTWARE\Classes\AppID\WDM.exe');
  RegDeleteKeyIncludingSubkeys(HKCU, 'SOFTWARE\Classes\AppID\WDM.exe');

  // Explorer verb (registry command, no COM).
  RegDeleteKeyIncludingSubkeys(HKCR, '*\shell\WDM.Download');

  // Inno Setup Uninstall registry keys (per-user legacy + both machine views)
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4F3B2C0A-8D2E-4B7A-9C1E-6A5B4D3E2F10}_is1');
  RegDeleteKeyIncludingSubkeys(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{4F3B2C0A-8D2E-4B7A-9C1E-6A5B4D3E2F10}_is1');
  RegDeleteKeyIncludingSubkeys(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{4F3B2C0A-8D2E-4B7A-9C1E-6A5B4D3E2F10}_is1');
end;

{ Per-user registry cleanup under one hive: Root=HKCU with Prefix='' for the
  current user, or Root=HKU with Prefix='<SID>\' for every enumerated user.
  Covers the in-app Run-at-startup toggle, Software\WDM, App Paths, tracing
  keys and the legacy per-user uninstall entry. }
procedure CleanOneUserHive(const Root: Integer; const Prefix: String);
begin
  RegDeleteValue(Root, Prefix + 'Software\Microsoft\Windows\CurrentVersion\Run', 'WDM');
  RegDeleteKeyIncludingSubkeys(Root, Prefix + 'Software\Microsoft\Windows\CurrentVersion\App Paths\WDM.exe');
  RegDeleteKeyIncludingSubkeys(Root, Prefix + 'Software\WDM');
  RegDeleteKeyIncludingSubkeys(Root, Prefix + 'SOFTWARE\Microsoft\Wbem\WDM');
  RegDeleteKeyIncludingSubkeys(Root, Prefix + 'SOFTWARE\Classes\AppID\WDM.exe');
  RegDeleteKeyIncludingSubkeys(Root, Prefix + 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4F3B2C0A-8D2E-4B7A-9C1E-6A5B4D3E2F10}_is1');
end;

{ Cleans every user hive: the elevated admin's HKCU plus each HKEY_USERS SID
  (the admin's HKCU is NOT the installing user's hive). Also removes our own
  browser policy entries per-SID — HKCU-only removal would miss the real user. }
procedure CleanAllUserHives;
var
  Names: TArrayOfString;
  I: Integer;
  Sid: String;
begin
  CleanOneUserHive(HKCU, '');
  if not RegGetSubkeyNames(HKU, '', Names) then
    Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    Sid := Names[I];
    if (Sid = '.DEFAULT') or (Pos('_Classes', Sid) > 0) then
    begin
      { skip the default profile and COM class views }
    end
    else
    begin
      CleanOneUserHive(HKU, Sid + '\');
      RemoveForceInstallPolicyAt(HKU, Sid + '\Software\Policies\Google\Chrome');
      RemoveForceInstallPolicyAt(HKU, Sid + '\Software\Policies\Microsoft\Edge');
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    KillAllProcesses;
    MigrateOldPerUserInstall;
    // Do NOT delete user data on install/updates!
    // Binaries ship from [Files]; user data (tasks.json, settings.json, engines,
    // WebView2 profile) lives OUTSIDE {app} in %LOCALAPPDATA%\WDM-Data, so an
    // install or update can no longer take the download list with it.
    // (The {app}\bin / WebView2 entries under [UninstallDelete] only clean up
    // leftovers from pre-move installs and bundled content.)
    // Full wipe happens ONLY in CurUninstallStepChanged (see WipeAllProfilesWdmData).
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
    CleanAllUserHives;
    RemoveForceInstallPolicy(WdmChromePolicyKey);
    RemoveForceInstallPolicy(WdmEdgePolicyKey);
    { FULL WIPE, no opt-out: every WDM trace on this machine — all profiles'
      WDM/WDM-Data dirs, temp artifacts, per-user registry — is deleted. }
    WipeAllProfilesWdmData;
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

    // Clean up temporary setup installers in %TEMP% (all naming generations)
    TempDir := GetTempDir;
    WipeTempMatch(TempDir, 'WDM_Setup_*.exe');
    WipeTempMatch(TempDir, 'WDM-Setup-*.exe');
    WipeTempMatch(TempDir, 'WDM-User-Setup-*.exe');
    WipeTempMatch(TempDir, 'WDM-Full-Setup-*.exe');

    // Additional safeguard: If AppDir still exists (e.g. unins000.exe was executing inside it),
    // launch a background cmd process to remove AppDir 2 seconds after unins000.exe terminates.
    if DirExists(AppDir) then
    begin
      CmdArgs := '/c timeout /t 2 /nobreak >nul & rmdir /s /q "' + AppDir + '"';
      Exec('cmd.exe', CmdArgs, '', SW_HIDE, ewNoWait, ResultCode);
    end;
  end;
end;
