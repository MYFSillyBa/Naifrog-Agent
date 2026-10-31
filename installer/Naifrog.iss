#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir SourcePath + "..\output\installer-payload"
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir SourcePath + "..\output\installer"
#endif
#define AppName "奶蛙Agent"
#define AppExe "Naifrog.exe"

[Setup]
AppId={{93DDF209-D23E-4C4C-A719-427346238D2A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=MYFSillyBa
AppPublisherURL=https://github.com/MYFSillyBa/Naifrog-Agent
AppSupportURL=https://github.com/MYFSillyBa/Naifrog-Agent/issues
AppUpdatesURL=https://github.com/MYFSillyBa/Naifrog-Agent/releases/latest
DefaultDirName={localappdata}\Programs\Naifrog
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableDirPage=no
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\Naifrog\Assets\naifrog.ico
OutputDir={#InstallerOutputDir}
OutputBaseFilename=Naifrog-Agent-Setup-{#AppVersion}-win-x64
VersionInfoVersion={#AppVersion}
VersionInfoDescription=奶蛙Agent 安装程序
VersionInfoProductName={#AppName}
VersionInfoCompany=MYFSillyBa
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
SetupLogging=yes
CloseApplications=yes
CloseApplicationsFilter=Naifrog.exe
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.DesktopShortcut=创建桌面快捷方式
chinesesimplified.LaunchApp=启动奶蛙Agent
english.DesktopShortcut=Create a desktop shortcut
english.LaunchApp=Launch Naifrog Agent

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
  InstalledExe: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    InstalledExe := ExpandConstant('{app}\{#AppExe}');
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'Naifrog', StartupCommand) then
    begin
      if (CompareText(StartupCommand, '"' + InstalledExe + '"') = 0) or
         (CompareText(StartupCommand, InstalledExe) = 0) then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Naifrog');
    end;
  end;
end;
