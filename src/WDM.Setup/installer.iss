; WDM (Windows Download Manager) installer
; Requires Inno Setup 6 (https://jrsoftware.org/isinfo.php)
; Compile: ISCC.exe installer.iss

#define MyAppName "Windows Download Manager"
#define MyAppShortName "WDM"
#define MyAppVersion "1.2.1.0"
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
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=yes
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
Source: "{#StagingDir}\WDM.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\WDM.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\WDM.pdb"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\WDM.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\WDM.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\BrowserExtension\*"; DestDir: "{app}\BrowserExtension"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; Firefox: register the WDM Download Catcher via the per-user enterprise policy so
; Firefox installs it automatically on next launch. Points at the XPI bundled next
; to the deployed extension (built during publish into BrowserExtension\wdm-catcher.xpi).
; Note: Firefox release builds require the XPI to be signed by Mozilla (submit once to
; addons.mozilla.org as a self-distributed add-on) or the add-on will show as blocked.
Root: HKCU; Subkey: "Software\Policies\Mozilla\Firefox"; ValueType: string; ValueName: "ExtensionSettings"; ValueData: "{{""wdm-catcher@wdm.app"":{{""installation_mode"":""normal_installed"",""install_url"":""file:///{localappdata}\WDM\BrowserExtension\wdm-catcher.xpi""}}}}"; Flags: uninsdeletevalue

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "/minimized"; Tasks: startwithwindows

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent