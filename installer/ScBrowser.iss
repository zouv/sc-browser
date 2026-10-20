; ============================================================================
;  ScBrowser 安装程序（Inno Setup 7）
;
;  由 package 脚本调用：
;    ISCC.exe --define=MyAppVersion=<版本> installer\ScBrowser.iss
;
;  安装形态：
;    · 按用户安装到 %LOCALAPPDATA%\Programs\ScBrowser，不需要管理员权限；
;    · 安装 release unpack 目录的全部文件（自包含，目标机无需 .NET 运行时）；
;    · 自带卸载程序，开始菜单 / 可选桌面快捷方式；
;    · 不触碰 %LOCALAPPDATA%\sc-browser 下的书签、历史、Cookie 等用户数据。
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "ScBrowser"
#define MyAppExeName "ScBrowser.exe"
#define MyAppPublisher "zouv"
#define MyAppUrl "https://github.com/zouv/sc-browser"

[Setup]
; AppId 必须保持不变，否则升级安装会被当成另一个产品。
AppId={{F5640EEB-0A68-44BD-AA16-5CA7AD2881B9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}

; 按用户安装：与 VS Code 用户版 / Tauri 默认安装器一致，避免 UAC。
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UsePreviousAppDir=yes

; 产物直接落在 dist\release，与 unpack 目录 / zip 并排。
OutputDir=..\dist\release
OutputBaseFilename={#MyAppName}_{#MyAppVersion}_x64-setup
SetupIconFile=..\src\ScBrowser\Assets\sc-browser.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupArchitecture=x64
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; unpack 目录已包含主程序、运行库、内部页面（Assets）、LICENSE 与 README.txt。
Source: "..\dist\release\unpack\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
