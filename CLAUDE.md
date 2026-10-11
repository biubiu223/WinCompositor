# WinCompositor（Compositor 的 Windows 版）— 协作约定

WinCompositor：robbietilton/Compositor 的 Windows 移植：C# / .NET 8 / WPF / SkiaSharp。功能说明和目录结构见 [README.md](README.md)。
**始终用中文回复用户。**

## 工作方式：主管 + 子代理

用户提需求，主对话担任项目主管，不亲自埋头写大段代码：

1. **拆解**：把需求拆成边界清楚的任务（按模块 / 文件划分，避免两个子代理改同一个文件）。需求有歧义时先问用户一个关键问题再动手。
2. **分派**：用 Agent 工具派子代理（一般用 general-purpose）。子代理不带任何上下文，任务书要写全：
   - 目标和验收标准（要达到什么效果、怎么算完成）
   - 相关文件路径、现有做法、要沿用的写法
   - 本文件里的约束（构建命令、不下载、不删文件、不动用户进程）
   - 要它最后汇报：改了哪些文件、为什么、构建 / 测试结果、没解决的问题
   - 互不依赖的任务并行派出；有依赖的按顺序派
3. **把关**：子代理完成后，主管自己审查改动、统一构建、跑测试、在真实程序里验证效果（截图对比），不合格就打回或补派任务。
4. **汇报**：用中文告诉用户做了什么、实测效果、已知局限，以及需要用户决定的事。

一两行就能改完的小改动（改文案、改个名字）主管可以直接做，省掉派任务的开销；用户要求全部派发时照办。

## 构建与测试

PowerShell 前缀（SDK 装在用户目录）：

```powershell
$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"; $env:DOTNET_CLI_TELEMETRY_OPTOUT="1"; $env:DOTNET_NOLOGO="1"
```

- 构建：`dotnet build src/Compositor.App -c Debug --no-restore -p:UseSharedCompilation=false`
- 测试：`dotnet test tests/Compositor.Core.Tests --no-restore -p:UseSharedCompilation=false`（目前 132 个，应全部通过）
- 发布：`dotnet publish src/Compositor.App -p:PublishProfile=win-x64 -p:UseSharedCompilation=false --source "$env:USERPROFILE\.nuget\packages"`（设置在 `src/Compositor.App/Properties/PublishProfiles/win-x64.pubxml`：自包含、单文件、压缩、无 .pdb，输出到 `dist\Compositor\`；用户电脑不需要装 .NET）
- 安装程序：`powershell -ExecutionPolicy Bypass -File installer/build-installer.ps1 [-SkipPublish]`（Inno Setup 6.7，已装在 `%LOCALAPPDATA%\Programs\Inno Setup 6`），输出 `dist\installer\`
- 结束时：`dotnet build-server shutdown`（不留后台编译进程）
- **不要强制结束用户自己开着的 WinCompositor**（里面可能有未存储的工作）：只结束自己启动的测试实例（记下它的 PID）。Debug 构建不受 dist 里运行的程序影响；发布到 `dist\` 前如果发现有不是自己启动的 WinCompositor 在运行，先问用户
- 同一时间只让一个代理构建，避免输出目录互相占用

## 约束

- **下载任何文件或新增 NuGet 包前，先问用户**（说明文件名、来源、大小）。子代理一律不下载、不装包，需要时回报主管。
- **不永久删除文件**：要删的移到回收站（`Microsoft.VisualBasic.FileIO.FileSystem` 的 `SendToRecycleBin`），并告诉用户可以清空。
- 不结束、不修改用户自己的进程，只动自己启动的 WinCompositor 测试实例。
- 名称：程序、窗口、安装包对外叫 **WinCompositor**；代码里的项目和命名空间仍叫 `Compositor.*`，设置文件夹仍是 `%APPDATA%\Compositor`（升级不丢设置），`.comp` 项目格式仍叫“Compositor 项目”（与 Mac 版通用），关于窗口保留对原版的署名。
- 不把用户邮箱发往任何地方。
- 界面文字中英双语：一律写成 `L.T("中文", "English")`，规则与词汇表见 [docs/localization.md](docs/localization.md)（新加的界面文字也必须这样写）；代码注释用英文，沿用现有风格（说明“为什么”）。

## 关键位置

- 内容识别填充 / 创造式生成：`src/Compositor.Core/Inpainting/`（ContentFill、PatchSynthesis、SmoothFill、FillMasks）、`src/Compositor.App/Dialogs/ContentFillWindow.cs`
- 抹除工具：`src/Compositor.Core/Editing/EditorSession.Remove.cs`、`src/Compositor.App/Panels/OptionsBar.cs`
- AI 模型：`models/`（构建时复制到程序旁 `Models\`）；加载与闲置释放：`src/Compositor.App/Services/`（InpaintModels、StableDiffusion、SubjectModels、ModelIdle）
- 菜单：`src/Compositor.App/MainWindow.Commands.cs`
