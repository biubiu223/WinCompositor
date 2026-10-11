# WinCompositor

**WinCompositor** = Compositor for Windows：名字前面的 Win 表示它是 Windows 版。

[Compositor](https://github.com/robbietilton/Compositor) 是 Robbie Tilton 为 macOS 写的一款面向合成与后期处理的类 Photoshop 图像编辑器（Swift / SwiftUI / Metal，MIT 许可证）。本项目把它移植到 Windows。

原版依赖的 SwiftUI、AppKit、Metal、Core Graphics、Core Image 在 Windows 上都不存在，所以这是一次**按原版源码逐个功能重写**的移植，而不是重新编译：

| | Mac 原版 | Windows 版 |
|---|---|---|
| 语言 / 界面 | Swift、SwiftUI + AppKit | C#（.NET 8）、WPF |
| 绘图 | Core Graphics、Core Image、Metal | SkiaSharp（Skia） |
| 像素算法 | C | C#（按原 C 代码逐行移植） |
| 工程文件 | `.comp`（manifest.json + PNG 图层） | **同一格式，两边可以互相打开** |

界面有**简体中文**和 **English** 两种语言，术语分别与 Photoshop 中文版、英文版一致（英文措辞优先沿用 Mac 原版）；在「编辑 › 首选项…」（Ctrl+K）里切换，重启后生效。第一次使用时跟随 Windows 的显示语言；从旧版本升级的用户保持原来的中文。工程文件里的名称（混合模式、撤销步骤等）仍按原版用英文存储，两种界面语言、Mac 版之间都能互相打开。翻译约定与词汇表见 [docs/localization.md](docs/localization.md)。

## 运行

**安装程序**：`dist\installer\WinCompositor-Setup-0.1.0.exe`（约 2.1 GB，单个文件）。中文或英文安装向导（按 Windows 显示语言自动选择），可以只为当前用户安装（不需要管理员权限）或为所有用户安装；可选是否安装 AI 生成模型（约 1.8 GB）；创建开始菜单 / 桌面快捷方式，带卸载程序。第一次安装时程序界面语言与安装向导一致。

**免安装**：编译好的程序在 `dist\WinCompositor\WinCompositor.exe`，AI 模型在旁边的 `Models\` 文件夹里（复制程序时要连同这个文件夹一起复制）。

- **不需要安装任何运行环境**：.NET 8 运行时（含 WPF）已打包进 `WinCompositor.exe`（约 83 MB），也不需要 VC++ 运行库；只要 Windows 10 1809 或更高版本、Windows 11（64 位）
- 第一次启动会把几个原生组件解包到 `%TEMP%\.net\WinCompositor\`，要几秒钟；之后启动约 1 秒

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
dotnet build Compositor.Windows.sln
```

```bash
dotnet test tests/Compositor.Core.Tests
```

```bash
dotnet publish src/Compositor.App -p:PublishProfile=win-x64
```

发布设置在 `src/Compositor.App/Properties/PublishProfiles/win-x64.pubxml`：自包含（内置 .NET 运行时）、单文件、压缩、不带调试符号，输出到 `dist\WinCompositor\`。

也可以直接用 Visual Studio 2022 打开 `Compositor.Windows.sln`。

### AI 模型（不在 Git 仓库里）

模型文件共约 2.2 GB，没有放进仓库。从源码构建时，把它们下载到项目的 `models\` 文件夹，构建和发布时会自动复制到程序旁的 `Models\`。缺哪个模型，对应功能就不可用，程序本身照常构建、运行。许可证文件、配置文件和预先算好的 `sd-inpainting\empty_prompt.bin` 都在仓库里。

| 放到 `models\` 下的路径 | 来源 | 大小 | 许可证 | 用于 |
|---|---|---|---|---|
| `isnet-general-use.onnx` | [rembg 发布页](https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx) | 170 MB | Apache-2.0 | 选择主体、对象选择、移除背景 |
| `lama_fp32.onnx` | [Carve/LaMa-ONNX](https://huggingface.co/Carve/LaMa-ONNX/resolve/main/lama_fp32.onnx) | 198 MB | Apache-2.0 | 神经重建、抹除工具 |
| `migan_pipeline_v2.onnx` | [andraniksargsyan/migan](https://huggingface.co/andraniksargsyan/migan/resolve/main/migan_pipeline_v2.onnx) | 27 MB | MIT | 修补模型后备 |
| `sd-inpainting\unet\model.onnx`、`vae_encoder\model.onnx`、`vae_decoder\model.onnx` | [RanaLLC/stable-diffusion-v1-5-inpainting-onnx-fp16](https://huggingface.co/RanaLLC/stable-diffusion-v1-5-inpainting-onnx-fp16/tree/main)（同名子文件夹） | 1.8 GB | CreativeML Open RAIL-M | AI 生成（可选） |

制作安装程序需要 [Inno Setup 6.7+](https://jrsoftware.org/isdl.php)，然后：

```bash
powershell -ExecutionPolicy Bypass -File installer/build-installer.ps1
```

它先发布到 `dist\WinCompositor\`，再把 `installer\WinCompositor.iss` 编译成 `dist\installer\WinCompositor-Setup-<版本>.exe`（版本号取自 `Directory.Build.props`）。安装向导的图片由 `installer\make-wizard-images.ps1` 从程序图标生成。

## 第一阶段完成的功能

**画布与文件**
- 多个项目以标签页打开；欢迎页可新建画布（预设、尺寸、分辨率、白/黑/透明背景）和打开最近的项目
- 读写 `.comp` 工程（格式版本 11，完整校验，原子保存，附带 QuickLook 预览），与 Mac 版互通
- 打开 / 导入 PNG、JPEG、WebP、GIF、BMP；TIFF、HEIC 等交给 Windows 自带解码器；按 EXIF 方向自动摆正
- 导出 PNG（保留透明）和 JPEG（可调品质），都写入文档分辨率
- 把图像或 `.comp` 拖进窗口即可导入 / 打开；也可从命令行传入路径
- 缩放（Ctrl+滚轮、Ctrl+=/-、缩放工具、适合屏幕、100%）、平移（空格拖动、中键、滚轮、抓手工具）
- 200% 以上像素硬边显示，800% 以上显示像素网格
- 标尺、从标尺拖出参考线、网格、对齐（画布、图层、参考线、网格）
- 全屏只显示画布（F）

**图层**
- 图层与文件夹（编组 / 取消编组 / 拖入拖出 / 嵌套），文件夹为“穿透”，其不透明度和蒙版作用于内部所有图层
- 不透明度、Photoshop 全部 24 种混合模式（其中 Skia 没有的 8 种和柔光用 SkSL 按 Photoshop 公式实现）
- 图层蒙版：添加（显示全部 / 隐藏全部 / 显示选区 / 隐藏选区）、在蒙版上绘画、停用、应用、删除、链接 / 取消链接、Alt 单击单独查看蒙版
- 剪贴蒙版（Ctrl+Alt+G 或 Alt 单击图层）
- 新建、复制、删除、重命名（双击）、显示 / 隐藏（可拖过多个眼睛一次切换）、拖动排序、前移 / 后移、向下合并 / 合并图层 / 合并组、右键菜单

**移动与变换**
- 移动工具：拖动、手柄缩放（锁定比例 / Shift 切换 / Alt 从中心）、旋转（Shift 15° 步进）、翻转、多图层或整个文件夹一起变换
- Ctrl+T 自由变换（回车应用、Esc 取消），选项栏可精确输入 X、Y、宽、高、角度
- 有选区时拖动 = 移动选区内的像素（Alt 复制）
- 自动选择图层（或按住 Ctrl 单击）、方向键微移

**选区**
- 矩形 / 椭圆选框、自由 / 多边形套索、魔棒（容差、取样大小、连续、对所有图层取样）
- 新选区 / 添加（Shift）/ 减去（Alt）；拖动选区内部移动选区；蚂蚁线
- 全选、取消选择、反选、扩展、收缩、羽化、载入图层像素、蒙版的黑色区域

**绘画与编辑**
- 画笔 / 橡皮擦：大小、硬度、不透明度、平滑；[ ] 调大小、Shift+[ ] 调硬度、数字键调不透明度、Shift 单击画直线、Alt 吸色、Alt+右键拖动调整笔尖
- 绘画受选区约束，超出图层边界时图层自动扩展
- 填充前景色 / 背景色（Alt / Ctrl+Backspace）、清除、反相
- 吸管、拾色器（HSB 方块 + 色相条 + RGB / HSB / 十六进制）
- 拷贝、剪切、合并拷贝、粘贴（与 Windows 剪贴板互通，带透明度的 PNG）、通过拷贝 / 剪切的图层（Ctrl+J / Ctrl+Shift+J）
- 画布大小（锚点、相对、扩展颜色）、图像大小（重新采样）、裁剪（比例、Alt 对称）、旋转 / 翻转画布

**撤销**
- 每个完整操作一步撤销，最多 100 步；菜单里显示具体操作名称

## 第二阶段新增的功能

**调整与调整图层**
- 图像 › 调整：色阶（直方图、自动、吸管）、曲线、曝光度、色相/饱和度（分色范围、吸管加减）、色彩平衡、黑白、反相、渐变映射、颗粒
- 图层 › 新建调整图层：上述全部 + 高斯模糊、动感模糊、添加杂色；双击调整图层重新编辑，实时预览，支持蒙版、不透明度、剪贴

**图层样式**
- 描边、投影、颜色叠加、内阴影、外发光、内发光；图层面板 fx 子行可单独开关；拷贝 / 粘贴 / 清除图层样式

**工具**
- 渐变工具（线性 / 径向 / 角度 / 对称 / 菱形，拖动手柄调整后应用）、形状工具（矩形 / 椭圆 / 直线，保持可编辑）
- 文字工具：点文字与段落文字、中文输入法、字体 / 字号 / 颜色 / 对齐 / 行距 / 字距，每个字符可单独设颜色
- 污点修复画笔（内容识别 / 创建纹理）、仿制图章（Alt 单击取样，对齐）、模糊 / 涂抹 / 液化

**滤镜菜单**
- 上次滤镜（Ctrl+Alt+F）、高斯模糊、动感模糊、添加杂色
- 暗角（空图层上可直接生成暗角）、泛光、色调对比、镜头校正、仿色（Atkinson、Floyd–Steinberg、Bayer、半调网点 / 线条 / 菱形、Mac 图案、ASCII 字符）、扫描线
- **Camera Raw 滤镜**（Ctrl+Shift+A）：基本（白平衡含吸管和自动、曝光、对比度、高光、阴影、白色、黑色、纹理、清晰度、去除薄雾、自然饱和度、饱和度）、曲线（参数曲线 + 点曲线）、细节（锐化、减少杂色）、混色器、颜色分级、光学（色差、去边、镜头晕影、扭曲）、几何、效果（颗粒、裁剪后晕影、光晕）、校准。光线和颜色滑块使用原版附带的、从 Photoshop Camera Raw 实测得到的颜色表

**编辑与选区**
- 自由扭曲：变换时按住 Ctrl 拖动手柄，或 编辑 › 扭曲；支持选区内像素、多图层、单独蒙版
- 内容识别填充（编辑 › 内容识别填充，现已改为独立工作区，见下文
- 色彩范围（选择 › 色彩范围）：吸管取样 / 添加 / 减去、颜色容差、反相，实时预览

**文件**
- 打开 PSD / PSB：图层、文件夹、图层蒙版、混合模式、不透明度与填充、剪贴蒙版、色阶 / 曲线 / 色相饱和度调整图层、矩形和椭圆形状图层；打开前列出需要转换的内容
- 导入 SVG（渐变、CSS 样式、use、裁剪路径、中文文字等），按画布大小栅格化
- 相机 RAW（DNG 等，借助 Windows 自带的 RAW 解码器；其他品牌需安装 Microsoft Store 的“Raw 图像扩展”）
- 导出为（Ctrl+Alt+Shift+W）：PNG / JPEG / 单页 PDF，可缩放、设背景色，导出前预览并显示文件大小
- 工程文件被其他程序（同步盘、AI 代理、git 等）修改后自动重新载入；有未存储的更改时先询问

**界面**
- 搜索命令（Ctrl+F）：输入名称即可执行任何菜单命令或切换工具
- 导航器：放大到 300% 以上时画布右上角显示缩略图，可点击 / 拖动移动视图

## 第三阶段新增的功能

**AI 选择与抠图**（原版用 macOS 的 Vision 框架；Windows 版改用开源模型 + Windows 自带的 WinML 运行，有显卡时用显卡）
- 选择 › 主体（Ctrl+Alt+A）：识别画面主体并生成选区
- 对象选择：魔棒工具选项栏切换到“对象选择”，单击哪个对象就选中哪个（可对所有图层取样、调整边缘）
- 滤镜 › 移除背景：用图层蒙版隐藏背景（像素不删除，可随时找回）；“高级”品质可优化边缘（导向滤波，找回发丝）、对比度、移动边缘
- 模型 `isnet-general-use.onnx`（IS-Net / DIS，约 170 MB，Apache-2.0，许可证见 `models\isnet-general-use.LICENSE.txt`）已内置：放在项目的 `models\` 文件夹，构建和发布时复制到程序旁的 `Models\` 文件夹，完全离线可用
- 如果程序旁没有模型（例如从源码构建时没有 `models\` 文件夹），第一次使用时会询问是否从 [rembg](https://github.com/danielgatis/rembg) 的发布页下载，存放在 `%LOCALAPPDATA%\Compositor\Models`；也可以选择本地已有的 `isnet-general-use.onnx`、`u2net.onnx`、`silueta.onnx` 或 `u2netp.onnx`

**Camera Raw 滤镜补全**
- 顶部直方图 / 矢量示波器（右键切换），左上 / 右上三角形开关阴影 / 高光修剪警告（也可按 U / O），下方显示指针处的 RGB 值
- 按住 Alt 拖动曝光、高光、阴影、白色、黑色滑块显示修剪视图；按住 Alt 拖动锐化“蒙版”显示锐化蒙版
- 每个分区标题旁的眼睛可临时隐藏该分区的调整
- 目标调整工具：在曲线（参数曲线区域）和混色器（色相 / 饱和度 / 明亮度）中，按住图像上下拖动直接调整
- 点颜色：吸取最多 8 种颜色，分别调整色相 / 饱和度 / 明亮度偏移和范围，可“显示范围”
- 几何 › 引导式校正：在图像上画最多 4 条参考线（竖线校正透视，其余拉直画面）

**文件**
- PSD 文字图层导入后仍是可编辑文字（字体、字号、颜色、字距、行距、对齐、段落文本框、旋转）；PostScript 字体名（如 ArialMT、MicrosoftYaHei）会对应到已安装的字体
- PSD 图层样式导入为图层效果：投影、内阴影、外发光、内发光、颜色叠加、描边（含全局光角度、缩放、隐藏状态）；斜面和浮雕、渐变 / 图案叠加等无法对应的会列在导入报告里
- 打开相机 RAW 时先显示冲印面板（曝光、色温、色调、增强），以 16 位精度冲印后导入

**编辑与界面**
- 编辑 › 键盘快捷键：可自定义所有菜单命令和画布单键快捷键，检测冲突，存储后立即生效
- 图像 › 裁切：按透明像素或左上角 / 右下角像素颜色裁掉边缘
- 视图 › 导航器：可关闭 300% 以上时显示的导航器

## 内容识别填充工作区

编辑 › 内容识别填充…（Shift+Backspace）打开类似 Photoshop 的独立工作区：左边是原图，绿色表示取样区域；右边是实时填充预览，两边缩放、平移同步。
- **AI 识别并抹除对象**：没有选区时直接打开，单击左图中的对象，AI（IS-Net）识别出整个对象并向外扩几个像素作为填充区域（Shift 添加、Alt 减去）；有选区时以选区为填充区域
- **取样画笔 / 填充画笔**：增加或减去取样区域、填充区域（按住 Alt 临时切换，[ ] 调大小），可还原 / 重做
- **取样区域**：自动（填充区域周围、与它大小相称的一圈）/ 全部 / 自定义，绿色覆盖可开关、调透明度
- **重建方式**：自动重建 / 纹理合成 / 平滑延展
  - 纹理合成：多尺度 PatchMatch 图块合成（与 Photoshop 内容识别填充同类的算法），用取样区域里最相似的图块由粗到细拼出填充内容；从边缘向内一圈圈起步，投票时靠近边缘的图块权重更高，草地与水岸、山脊线这类边界能接进去
  - 平滑延展：把边缘颜色平滑延伸进来并加上与周围一致的颗粒，适合天空、渐变和纯色背景
  - 自动：边缘几乎全是平滑区域时平滑延展，否则纹理合成
- **神经重建**：用 AI 修补模型 LaMa 补全结构（把被挡住的道路、路沿、草坪、山脊线接上），再用纹理合成补回图像自身的颗粒；结构以 AI 的为准，纹理合成只提供细节，不会把 AI 接好的结构换掉。没有 LaMa 时退回 MI-GAN

- **AI 生成**：在重建方式里选“AI 生成”。“编辑 › 创造式生成…”打开的是同一个工作区，默认用纹理合成（+ 神经重建），只有选了“AI 生成”才载入 Stable Diffusion（显存占用较大），关闭工作区或闲置 90 秒即释放；工作区从不默认以 AI 生成打开。用 Stable Diffusion 1.5 Inpainting 按周围内容在填充区域里画出新内容（不需要输入提示词），颜色沿边缘与原图对齐；选中时自动生成一次，之后修改了填充区域就点“生成”更新，每次约 10 秒，“换一个结果”可以多试几次
- **换一个结果**、预览开关、**输出到新图层**（默认，只含填充出的像素）或直接改写当前图层
- 内置模型（都在程序旁的 `Models\` 文件夹，离线运行，许可证文件在同一文件夹）：
  - `lama_fp32.onnx`（LaMa，约 198 MB，Apache-2.0）：神经重建的默认模型，在 CPU 上运行（显卡的 DirectML 不支持它的傅里叶卷积），约 1–2 秒

  - `sd-inpainting\`（Stable Diffusion 1.5 Inpainting fp16 的 UNet 和 VAE，约 1.8 GB，CreativeML Open RAIL-M）：AI 生成，在显卡上运行。因为不用提示词，不带 235 MB 的文本编码器，空提示词的编码已预先算好（`empty_prompt.bin`，0.2 MB），生成结果与带文本编码器时相同
  - `migan_pipeline_v2.onnx`（MI-GAN，约 27 MB，MIT）：没有 LaMa 时的后备
- 内存与显存：AI 模型用到时才载入，闲置 1.5–2 分钟（或关闭填充工作区时）自动释放；程序退出后不残留任何进程。显存紧张时（例如同时开着 ComfyUI）生成会变慢
- 填充结果保留图像自身的颗粒：最后几轮只采用匹配最好的图块（不把几十个图块平均成一片模糊），AI 补出的结构也会再用周围的真实纹理覆盖一遍

## 抹除工具

工具栏“污点修复画笔”下面的新工具（Shift+J）：直接在画布上涂抹要去掉的东西，涂抹处显示粉色，松开鼠标即自动抹除，一步撤销。填充方式与内容识别填充 / 创造式生成里的**纹理合成**相同：先用 AI 修补模型 LaMa 补全结构（把路沿、草坪、绿化带接上），再用纹理合成从周围找最相似的图块覆盖，补回图像自身的颗粒。
- 圈住一个对象（画一个闭合的圈）就会抹除整个圈内
- **AI 识别对象**：涂抹覆盖了对象的大部分时，用 AI（IS-Net）找出整个对象的轮廓一起抹除，不必涂得很仔细
- **AI 神经重建**（默认开）：关掉则只用纹理合成，不用 AI 模型
- 有选区时只在选区内抹除；作用于当前图层的像素

## 快捷键对照

Mac 的 ⌘ 对应 Windows 的 **Ctrl**，⌥ 对应 **Alt**。所有快捷键见“帮助 › 键盘快捷键”。为了让单键工具快捷键（V、M、B……）直接生效，画布上会关闭中文输入法；文本框里输入法正常可用。

## 项目结构

```
src/Compositor.Core/      与界面无关的核心（可单独测试）
  Model/                  图层、文档、变换、蒙版、选区、混合模式
  Rendering/              图层合成（DocumentRenderer）、SkSL 混合器、视口
  Painting/               画笔笔触与像素编辑（RasterEdit / BrushStroke）
  Editing/                EditorSession：所有编辑操作与撤销
  Adjustments/            调整（色阶、曲线、色相/饱和度等）的像素算法
  Filters/                滤镜菜单（暗角、仿色、扫描线、内容识别填充等）
  CameraRaw/              Camera Raw 滤镜（实测颜色表 + 各面板算法、直方图、预览视图）
  Segmentation/           主体识别的前后处理（模型输入、导向滤波、对象区域）
  Inpainting/             内容识别填充（自动取样区域、PatchMatch 纹理合成、平滑延展、神经修补接口）
  Text/                   文字排版
  IO/                     .comp 工程读写、图片导入导出、SVG 渲染
  IO/Psd/                 PSD / PSB 读取（含文字图层与图层样式）
  Pixels/                 魔棒、污点修复（WandPixels.c / HealPixels.c 的移植）
  Resources/              Camera Raw 颜色表
src/Compositor.App/       WPF 界面
  Drawing/                画布控件、标尺
  Panels/                 图层面板、选项栏、调整 / 滤镜 / 图层样式 / 色彩范围面板、命令面板
  Dialogs/                新建、画布大小、图像大小、拾色器、键盘快捷键、RAW 冲印、AI 模型下载等
  Services/               设置、快捷键、WinML 模型运行
  Themes/Dark.xaml        深色主题
models/                   内置的 AI 模型（主体识别 IS-Net、修补 LaMa / MI-GAN、生成 Stable Diffusion 1.5 Inpainting）及其许可证（构建时复制到程序旁的 Models\）
tests/Compositor.Core.Tests/  132 个测试（渲染、混合模式、画笔、选区、调整、样式、工具、滤镜、扭曲、PSD、SVG、Camera Raw、主体识别、内容识别填充、RAW 冲印、裁切、工程往返、性能）
```

## 许可证

MIT。WinCompositor 由 Compositor（Copyright (c) 2026 Wonder Assembly LLC）的源码移植而来，原版权声明见 [LICENSE](LICENSE)。
