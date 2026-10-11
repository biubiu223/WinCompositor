; WinCompositor (Compositor for Windows) — the installer (Inno Setup 6.7+).
; Build it with installer\build-installer.ps1, which publishes the self-contained program to dist\WinCompositor first and
; passes the version from Directory.Build.props. Output: dist\installer\WinCompositor-Setup-<version>.exe
;
; What it installs: WinCompositor.exe (the .NET 8 runtime inside, nothing else needed on the machine) and the Models
; folder beside it. The Stable Diffusion model for AI generation is an optional component (about 1.8 GB).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
; The numbers alone (0.1.0 for 0.1.0-beta.1): Windows' file version can't carry a pre-release label.
#ifndef AppVersionNumber
  #define AppVersionNumber AppVersion
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
VersionInfoVersion={#AppVersionNumber}
VersionInfoProductTextVersion={#AppVersion}
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

; Only the program and some small files are inside now: the best compression costs little time.
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\dist\installer
OutputBaseFilename=WinCompositor-Setup-{#AppVersion}

[Languages]
Name: "chs"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chs.TypeRecommended=推荐安装（下载基础 AI 模型，约 395 MB）
en.TypeRecommended=Recommended installation (downloads the core AI models, about 395 MB)
chs.TypeFull=完整安装（另下载 AI 生成模型，共约 2.2 GB）
en.TypeFull=Full installation (also downloads the AI generation model, about 2.2 GB in all)
chs.TypeCompact=仅程序（不下载模型，AI 功能不可用）
en.TypeCompact=Program only (no models; the AI features stay unavailable)
chs.TypeCustom=自定义安装
en.TypeCustom=Custom installation
chs.ComponentMain=WinCompositor 程序
en.ComponentMain=The WinCompositor program
chs.ComponentModels=基础 AI 模型：选择主体、神经重建、抹除工具（安装时下载）
en.ComponentModels=Core AI models: Select Subject, Neural Reconstruction, Remove Tool (downloaded while installing)
chs.ComponentGenerate=AI 生成模型 Stable Diffusion 1.5 Inpainting（可选，安装时下载，占用显存较大）
en.ComponentGenerate=AI generation model: Stable Diffusion 1.5 Inpainting (optional, downloaded while installing; uses a lot of graphics memory)
chs.ShortcutComment=面向合成与后期处理的图像编辑器
en.ShortcutComment=An image editor for compositing and finishing

[Types]
; The first is the default: the program and the core models, not the 1.8 GB generation model.
Name: "recommended"; Description: "{cm:TypeRecommended}"
Name: "full"; Description: "{cm:TypeFull}"
Name: "compact"; Description: "{cm:TypeCompact}"
Name: "custom"; Description: "{cm:TypeCustom}"; Flags: iscustom

[Components]
Name: "main"; Description: "{cm:ComponentMain}"; Types: recommended full compact custom; Flags: fixed
Name: "models"; Description: "{cm:ComponentModels}"; Types: recommended full custom
Name: "generate"; Description: "{cm:ComponentGenerate}"; Types: full

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#Dist}\{#AppExe}"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Components: main; Flags: ignoreversion

; What comes with the models but isn't downloaded: their licenses, the generation model's settings, and the text
; encoder's precomputed answer for the empty prompt (empty_prompt.bin), which is published nowhere else.
Source: "..\models\isnet-general-use.LICENSE.txt"; DestDir: "{app}\Models"; Components: models; Flags: ignoreversion
Source: "..\models\lama_fp32.LICENSE.txt"; DestDir: "{app}\Models"; Components: models; Flags: ignoreversion
Source: "..\models\migan_pipeline_v2.LICENSE.txt"; DestDir: "{app}\Models"; Components: models; Flags: ignoreversion
Source: "..\models\sd-inpainting\*"; Excludes: "*.onnx,*.onnx_data"; DestDir: "{app}\Models\sd-inpainting"; Components: generate; Flags: ignoreversion recursesubdirs

; The models, downloaded while installing from where each is published and checked against its SHA-256 (sizes and
; hashes are those of the files the program was built and tested with). One an earlier installation already has isn't
; fetched again (onlyifdoesntexist; a Check function instead would hide the download sizes from the Components page).
; A failed download shows Setup's usual Retry / Ignore choice; Ignore installs everything else, and running Setup
; again later fetches what's missing.
Source: "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx"; \
  DestName: "isnet-general-use.onnx"; DestDir: "{app}\Models"; Components: models; \
  Hash: "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a"; ExternalSize: 178648008; \
  Flags: external download ignoreversion onlyifdoesntexist
Source: "https://huggingface.co/Carve/LaMa-ONNX/resolve/main/lama_fp32.onnx"; \
  DestName: "lama_fp32.onnx"; DestDir: "{app}\Models"; Components: models; \
  Hash: "1faef5301d78db7dda502fe59966957ec4b79dd64e16f03ed96913c7a4eb68d6"; ExternalSize: 208044816; \
  Flags: external download ignoreversion onlyifdoesntexist
Source: "https://huggingface.co/andraniksargsyan/migan/resolve/main/migan_pipeline_v2.onnx"; \
  DestName: "migan_pipeline_v2.onnx"; DestDir: "{app}\Models"; Components: models; \
  Hash: "6f1f3530a1a2324b19752018ce756088b07973cda8d7d890034ace5c8a48c40b"; ExternalSize: 28079181; \
  Flags: external download ignoreversion onlyifdoesntexist
Source: "https://huggingface.co/RanaLLC/stable-diffusion-v1-5-inpainting-onnx-fp16/resolve/main/unet/model.onnx"; \
  DestName: "model.onnx"; DestDir: "{app}\Models\sd-inpainting\unet"; Components: generate; \
  Hash: "a3726e1944ec2fbc9596096cd001520aea789367f329360ee34eb520c751de16"; ExternalSize: 1720173976; \
  Flags: external download ignoreversion onlyifdoesntexist
Source: "https://huggingface.co/RanaLLC/stable-diffusion-v1-5-inpainting-onnx-fp16/resolve/main/vae_encoder/model.onnx"; \
  DestName: "model.onnx"; DestDir: "{app}\Models\sd-inpainting\vae_encoder"; Components: generate; \
  Hash: "f0da9070d007def0d6a4e7c10a21462bb6172e460ef2587c3fe91191397b4cea"; ExternalSize: 68430178; \
  Flags: external download ignoreversion onlyifdoesntexist
Source: "https://huggingface.co/RanaLLC/stable-diffusion-v1-5-inpainting-onnx-fp16/resolve/main/vae_decoder/model.onnx"; \
  DestName: "model.onnx"; DestDir: "{app}\Models\sd-inpainting\vae_decoder"; Components: generate; \
  Hash: "ce0ffca3fcfd0a2729a88d8d90dabf35c66d237404997e0461d52e46ac7a91bc"; ExternalSize: 99093889; \
  Flags: external download ignoreversion onlyifdoesntexist

[InstallDelete]
; Upgrading an installation from before the rename (same AppId, same folder): the old program and its shortcuts go.
Type: files; Name: "{app}\Compositor.exe"
Type: files; Name: "{autoprograms}\Compositor.lnk"
Type: files; Name: "{autodesktop}\Compositor.lnk"
; Models left out on the Components page go, when an earlier installation had them.
Type: files; Name: "{app}\Models\isnet-general-use.onnx"; Components: not models
Type: files; Name: "{app}\Models\lama_fp32.onnx"; Components: not models
Type: files; Name: "{app}\Models\migan_pipeline_v2.onnx"; Components: not models
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
