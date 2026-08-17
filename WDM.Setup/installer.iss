; WDM (Windows Download Manager) installer
; Requires Inno Setup 6 (https://jrsoftware.org/isinfo.php)
; Compile: ISCC.exe installer.iss

#define MyAppName "Windows Download Manager"
#define MyAppShortName "WDM"
#define MyAppVersion "1.0.0.0"
#define MyAppPublisher "WDM Team"
#define MyAppExeName "WDM.exe"
#define MyAppIcon "..\WDM\Assets\WDM.ico"
#define StagingDir "..\staging"

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
OutputDir=..\output
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
Name: "startmenuicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#StagingDir}\WDM.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\WDM.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\WDM.pdb"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\WDM.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\WDM.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\BrowserExtension\*"; DestDir: "{app}\BrowserExtension"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent