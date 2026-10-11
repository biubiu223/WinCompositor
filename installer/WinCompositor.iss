; WinCompositor (Compositor for Windows) — the installer (Inno Setup 6.7+).
; Build it with installer\build-installer.ps1, which publishes the self-contained program to dist\WinCompositor first and
; passes the version from Directory.Build.props. Output: dist\installer\WinCompositor-Setup-<version>.exe
;
; What it installs: WinCompositor.exe (the .NET 8 runtime inside, nothing else needed on the machine) and the Models
; folder beside it. The Stable Diffusion model for AI generation is an optional component (about 1.8 GB).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "WinCompositor"
#define AppExe "WinCompositor.exe"
#define Dist "..\dist\WinCompositor"

[Setup]
; Fixed for good: lets later versions upgrade this installation in place.
AppId={{94D14BC2-1E3A-41FB-89AF-369266998074}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Compositor for Windows
AppCopyright=MIT License · Original © 2026 Wonder Assembly LLC
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup

; Per-user by default (no administrator needed, installs to %LOCALAPPDATA%\Programs\Compositor); the first page
; offers installing for all users into Program Files instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes

; 64-bit Windows 10 1809 or later, as the program itself needs (Windows ML).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; Looks: the dark wizard in the program's own colors, its icon and pictures.
WizardStyle=modern dark
WizardBackColor=#1F1F1F
WizardSizePercent=110
DisableWelcomePage=no
SetupIconFile=..\src\Compositor.App\Assets\Compositor.ico
WizardImageFile=images\wizard-100.bmp,images\wizard-150.bmp,images\wizard-200.bmp
WizardSmallImageFile=images\small-100.bmp,images\small-150.bmp,images\small-200.bmp
WizardImageAlphaFormat=defined
WizardImageStretch=no
; Simplified Chinese or English, picked from Windows' display language; any other language gets the choice.
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage
; The MIT license with its paragraphs unwrapped, so the license box wraps them evenly.
LicenseFile=license.txt

UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; A running Compositor is closed (after asking) before files are replaced or removed.
CloseApplications=yes
RestartApplications=no

; The models barely compress (they are already dense numbers), so fast LZMA2 per file: the setup is hardly bigger
; than with maximum compression, builds in minutes, and leaving out the optional model skips its data quickly.
Compression=lzma2/fast
SolidCompression=no
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=8
OutputDir=..\dist\installer
OutputBaseFilename=WinCompositor-Setup-{#AppVersion}

[Languages]
Name: "chs"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chs.TypeFull=完整安装（含 AI 生成模型，约 2.4 GB）
en.TypeFull=Full installation (with the AI generation model, about 2.4 GB)
chs.TypeCompact=精简安装（不含 AI 生成模型，约 0.5 GB）
en.TypeCompact=Compact installation (without the AI generation model, about 0.5 GB)
chs.TypeCustom=自定义安装
en.TypeCustom=Custom installation
chs.ComponentMain=Compositor 程序与基础 AI 模型（选择主体、神经重建、抹除工具）
en.ComponentMain=Compositor and its core AI models (Select Subject, Neural Reconstruction, Remove Tool)
chs.ComponentGenerate=AI 生成模型 Stable Diffusion 1.5 Inpainting（可选，占用显存较大）
en.ComponentGenerate=AI generation model: Stable Diffusion 1.5 Inpainting (optional, uses a lot of graphics memory)
chs.ShortcutComment=面向合成与后期处理的图像编辑器
en.ShortcutComment=An image editor for compositing and finishing

[Types]
Name: "full"; Description: "{cm:TypeFull}"
Name: "compact"; Description: "{cm:TypeCompact}"
Name: "custom"; Description: "{cm:TypeCustom}"; Flags: iscustom

[Components]
Name: "main"; Description: "{cm:ComponentMain}"; Types: full compact custom; Flags: fixed
Name: "generate"; Description: "{cm:ComponentGenerate}"; Types: full

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#Dist}\{#AppExe}"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#Dist}\Models\*"; Excludes: "sd-inpainting"; DestDir: "{app}\Models"; Components: main; Flags: ignoreversion recursesubdirs
Source: "{#Dist}\Models\sd-inpainting\*"; DestDir: "{app}\Models\sd-inpainting"; Components: generate; Flags: ignoreversion recursesubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Components: main; Flags: ignoreversion

[InstallDelete]
; Upgrading an installation from before the rename (same AppId, same folder): the old program and its shortcuts go.
Type: files; Name: "{app}\Compositor.exe"
Type: files; Name: "{autoprograms}\Compositor.lnk"
Type: files; Name: "{autodesktop}\Compositor.lnk"
; Choosing the compact install over a full one takes the generation model away again.
Type: filesandordirs; Name: "{app}\Models\sd-inpainting"; Components: not generate

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:ShortcutComment}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// The first time Compositor is installed for this person, it starts in the language Setup ran in. Existing settings
// (an earlier installation, a language already chosen in Preferences) are left alone.
procedure CurStepChanged(CurStep: TSetupStep);
var
  SettingsFile, Language: string;
begin
  if CurStep = ssPostInstall then
  begin
    SettingsFile := ExpandConstant('{userappdata}\Compositor\settings.json');
    if not FileExists(SettingsFile) then
    begin
      if ActiveLanguage = 'en' then Language := 'en' else Language := 'zh-CN';
      ForceDirectories(ExtractFileDir(SettingsFile));
      SaveStringToFile(SettingsFile, '{ "Language": "' + Language + '" }', False);
    end;
  end;
end;
