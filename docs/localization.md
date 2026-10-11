# 界面多语言（简体中文 / English）约定

所有子代理和以后的改动都按这份约定写界面文字。

## 写法

- 每一处**用户能看到的**中文文字都改成并排的两种语言：
  ```csharp
  using Compositor.Core.Localization;   // 需要时加上
  Title = L.T("内容识别填充", "Content-Aware Fill");
  ```
- 插值字符串两边都写成插值：`L.T($"要存储对“{name}”所做的更改吗？", $"Save changes to “{name}”?")`
- 拼接时每一段各自翻译：`L.T("撤销 ", "Undo ") + name`。如果两种语言的语序不同，就整句放进 `L.T`，不要拼。
- `L.T` 按当前语言返回一个字符串，**每次调用都会重新取值**。语言只在启动时决定，切换后重启才生效，所以界面构建时取一次就可以，不需要做实时刷新。
- `static readonly` 字段、属性初始化器里也可以用 `L.T`：启动时会先设好语言，再碰任何界面类。
- 不要用 `using static` 只写 `T(...)`：泛型方法里的类型参数 `T` 会和它冲突。

## 哪些不翻译

- **会存到文件里的标识**保持原样，不随语言改变：工程文件（.comp）里的名称（本来就按 Mac 版存英文）、设置文件 `settings.json` 里的键和值、快捷键的 id（`menu:文件 › 新建…` 这种用中文菜单路径做 id 的，切换语言后必须保持不变，否则用户自定义的快捷键会丢）。
- 代码注释（本来就是英文）、日志、只给开发者看的调试文字。
- 用户自己起的名字、文件名。

## 用词

1. **先查原版**：Mac 原版是英文的，源码在
   [robbietilton/Compositor](https://github.com/robbietilton/Compositor)（234 个 .swift 文件；clone 到本地后用 Grep 搜）。Windows 版的中文大多是从那里翻译过来的，能找到对应原文就**直接用原版英文**。
2. 原版没有的（内容识别填充工作区、抹除工具、AI 生成、深色消息框、关于、快捷键速查、首选项等 Windows 版新增功能），用 **Photoshop 英文版的官方用语**。
3. 英文按 Photoshop 习惯：菜单、按钮、面板标题用 Title Case（`Save As…`、`Content-Aware Fill`）；说明、提示用正常句子大小写，句末加句号；省略号用 `…`（单个字符），和中文保持一致；菜单路径用 ` ▸ `（例如 `Edit ▸ Preferences…`）。
4. 下面的词汇表必须统一；表里没有的词，同一个意思全项目只用一种译法。新增的常用词可以在汇报里告诉主管，由主管补进本表。

| 中文 | English |
|---|---|
| 文件 / 编辑 / 图像 / 图层 / 选择 / 滤镜 / 视图 / 帮助 | File / Edit / Image / Layer / Select / Filter / View / Help |
| 新建… / 打开… / 存储 / 存储为… / 导出为… / 关闭 | New… / Open… / Save / Save As… / Export As… / Close |
| 撤销 / 重做 / 剪切 / 拷贝 / 合并拷贝 / 粘贴 / 清除 | Undo / Redo / Cut / Copy / Copy Merged / Paste / Clear |
| 首选项… | Preferences… |
| 确定 / 取消 / 关闭 / 应用 / 重置 / 完成 | OK / Cancel / Close / Apply / Reset / Done |
| 存储 / 不存储（关闭时的保存提示） | Save / Don’t Save |
| 移动工具 / 选框工具 / 套索工具 / 魔棒工具 / 裁剪工具 | Move Tool / Marquee Tool / Lasso Tool / Magic Wand Tool / Crop Tool |
| 画笔工具 / 橡皮擦 / 污点修复画笔 / 抹除工具 / 仿制图章 | Brush Tool / Eraser Tool / Spot Healing Brush Tool / Remove Tool / Clone Stamp Tool |
| 模糊 / 涂抹 / 液化 | Blur / Smudge / Liquify |
| 渐变工具 / 形状工具 / 文字工具 / 吸管工具 / 抓手工具 / 缩放工具 | Gradient Tool / Shape Tool / Type Tool / Eyedropper Tool / Hand Tool / Zoom Tool |
| 前景色 / 背景色 | Foreground Color / Background Color |
| 内容识别填充 | Content-Aware Fill |
| 创造式生成 | Generative Fill |
| 重建方式：自动重建 / 纹理合成 / 平滑延展 / AI 生成 | Fill Method: Auto / Texture Synthesis / Smooth Fill / AI Generation |
| 神经重建（AI 神经重建） | Neural Reconstruction |
| 取样区域 / 取样画笔 / 填充画笔 / AI 选择对象 | Sampling Area / Sampling Brush / Fill Brush / Select Object (AI) |
| 换一个结果 / 生成 / 输出到新图层 | Try Another / Generate / Output to New Layer |
| AI 识别对象（抹除工具选项） | Detect Objects (AI) |
| 选择 › 主体 / 对象选择 / 移除背景 / 色彩范围 | Select ▸ Subject / Object Selection / Remove Background / Color Range |
| 全部 / 取消选择 / 反选 / 羽化 / 扩展 / 收缩 | All / Deselect / Inverse / Feather / Expand / Contract |
| 色阶 / 曲线 / 色相/饱和度 / 色彩平衡 / 黑白 / 反相 | Levels / Curves / Hue/Saturation / Color Balance / Black & White / Invert |
| 画布大小 / 图像大小 / 自由变换 / 扭曲 / 裁切 | Canvas Size / Image Size / Free Transform / Distort / Trim |
| 调整图层 / 图层蒙版 / 剪贴蒙版 / 图层编组 / 图层效果 / 图层样式 | Adjustment Layer / Layer Mask / Clipping Mask / Group Layers / Layer Effects / Layer Style |
| 投影 / 内阴影 / 外发光 / 内发光 / 颜色叠加 / 描边 / 斜面和浮雕 | Drop Shadow / Inner Shadow / Outer Glow / Inner Glow / Color Overlay / Stroke / Bevel & Emboss |
| 不透明度 / 填充（图层） / 混合模式 | Opacity / Fill / Blend Mode |
| 正常 / 溶解 / 变暗 / 正片叠底 / 颜色加深 / 线性加深 / 深色 | Normal / Dissolve / Darken / Multiply / Color Burn / Linear Burn / Darker Color |
| 变亮 / 滤色 / 颜色减淡 / 线性减淡（添加） / 浅色 | Lighten / Screen / Color Dodge / Linear Dodge (Add) / Lighter Color |
| 叠加 / 柔光 / 强光 / 亮光 / 线性光 / 点光 / 实色混合 | Overlay / Soft Light / Hard Light / Vivid Light / Linear Light / Pin Light / Hard Mix |
| 差值 / 排除 / 减去 / 划分 / 色相 / 饱和度 / 颜色 / 明度 | Difference / Exclusion / Subtract / Divide / Hue / Saturation / Color / Luminosity |
| Camera Raw 滤镜 / 基本 / 曝光 / 对比度 / 高光 / 阴影 / 白色 / 黑色 | Camera Raw Filter / Basic / Exposure / Contrast / Highlights / Shadows / Whites / Blacks |
| 色温 / 色调 / 自然饱和度 / 纹理 / 清晰度 / 去除薄雾 | Temperature / Tint / Vibrance / Texture / Clarity / Dehaze |
| 锐化 / 降噪 / 颗粒 / 晕影 | Sharpening / Noise Reduction / Grain / Vignette |
| 网格 / 参考线 / 标尺 / 对齐 / 导航器 | Grid / Guides / Rulers / Snap / Navigator |
| 键盘快捷键 / 搜索命令 / 关于 WinCompositor | Keyboard Shortcuts / Search Commands / About WinCompositor |
| 界面语言 / 跟随系统 / 重启后生效 | Language / Use Windows Language / Takes effect after restarting WinCompositor |
| 产品名（界面里提到本程序时） | WinCompositor（不翻译；提到 Mac 原版或 .comp 格式时仍写 Compositor） |
| 常规 / 稍后 / 立即重启 / 恢复默认 | General / Later / Restart Now / Restore Defaults |
| 无法…（错误标题的固定句式） | Couldn’t … |
| 涂抹工具（含液化 / 模糊 / 涂抹三种模式） | Smear Tool (Liquify / Blur / Smudge) |
| 无工具 / 修改快捷键… / 空格 / 无（空快捷键） | No Tool / Edit Shortcuts… / Space / None |
| 新选区 / 添加 / 减去 / 消除锯齿 | New / Add / Subtract / Anti-alias |
| 对所有图层取样 / 连续 / 容差 / 颜色容差（色彩范围） | Sample All Layers / Contiguous / Tolerance / Fuzziness |
| 自动选择 / 显示变换控件 / 穿透 / 移出组 | Auto-Select / Show Transform Controls / Pass Through / Move Out of Group |
| 添加到取样 / 从取样中减去 / 适合窗口 | Add to Sample / Subtract from Sample / Fit on Screen |
| 污点修复：内容识别 / 创建纹理 / 近似匹配 | Content-Aware / Create Texture / Proximity Match |
| 光泽 / 渐变叠加 / 图案叠加 | Satin / Gradient Overlay / Pattern Overlay |
| 默认名：背景 / 图层 n / 组 n / 拷贝 / 画布扩展 | Background / Layer n / Group n / copy / Canvas Extension |
| 预览 / 数量 / 半径 / 中间调 / 灰度系数校正 / 全图 | Preview / Amount / Radius / Midtones / Gamma Correction / Master |
| 明度（色相/饱和度滑块；混合模式里的明度是 Luminosity） | Lightness |
| 偏好（Camera Raw） / 去边 / 点颜色 / 镜头晕影 / 裁剪后晕影 | Presence / Defringe / Point Color / Lens Vignetting / Post Crop Vignetting |
| 拾色器 / 修补模型 / 抠图模型 | Color Picker / inpainting model / segmentation model |
| 像素（单位） | px |

## 排版

- 英文通常比中文长 1.5–2.5 倍。**写死宽度的标签、列、按钮（例如 `Width = 72`、`MinWidth`、`Caption("…", 72)`）要检查英文放不放得下**：放不下就改成按语言给宽度（`L.IsEnglish ? 120 : 72`）或改成自适应宽度，不要让英文被截断或挤到下一行。
- 按钮文字太长时宁可换一个更短的标准说法（`Try Another` 而不是 `Get a Different Result`）。
