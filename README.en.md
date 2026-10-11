<div align="center">

[简体中文](README.md) | **English**

<img src="docs/images/icon.png" width="112" alt="WinCompositor icon">

# WinCompositor

**Compositor, for Windows — a Photoshop-style image editor for compositing and finishing**

[![Release](https://img.shields.io/github/v/release/biubiu223/WinCompositor?include_prereleases&label=release)](https://github.com/biubiu223/WinCompositor/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![Platform: Windows 10 | 11](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)](#system-requirements)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/8.0)

<a href="https://github.com/biubiu223/WinCompositor/releases"><img src="https://img.shields.io/badge/Download-Windows%20installer-2ea44f?style=for-the-badge" alt="Download the Windows installer"></a>

Current version [**0.1.0-beta.1**](https://github.com/biubiu223/WinCompositor/releases/tag/v0.1.0-beta.1) (beta) · English / Simplified Chinese · Free and open source

[Highlights](#highlights) · [Screenshots](#screenshots) · [Download and install](#download-and-install) · [Features](#features) · [Building from source](#building-from-source) · [License and credits](#license-and-credits)

</div>

<br>

![WinCompositor with the English interface](docs/images/english-ui.png)

**WinCompositor** is **Compositor** for **Win**dows. [Compositor](https://github.com/robbietilton/Compositor) is Robbie Tilton's Photoshop-style image editor for macOS, made for compositing and finishing; WinCompositor rebuilds it for Windows, feature by feature, from the original source. Layers, masks, adjustment layers, Camera Raw, Content-Aware Fill — they work the way they do in Photoshop. The AI features (Select Subject, Remove Background, Neural Reconstruction, AI Generation) run on open models, locally and offline.

> [!NOTE]
> This is a beta (pre-release). If you run into a problem, please [open an issue](https://github.com/biubiu223/WinCompositor/issues).

## Highlights

<table>
  <tr>
    <td width="50%" valign="top">
      🎨 <b>Photoshop-style layers</b><br>
      Layers and groups, layer masks, clipping masks, 24 blend modes, adjustment layers and layer styles — with Photoshop's menus, terms and shortcuts
    </td>
    <td width="50%" valign="top">
      🎞️ <b>Camera Raw Filter</b><br>
      Basic, Curve, Detail, Color Mixer, Color Grading, Optics, Geometry, Effects and Calibration; the light and color sliders use color tables measured from Photoshop's Camera Raw
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      🪄 <b>Content-Aware Fill &amp; Generative Fill</b><br>
      A dedicated workspace like Photoshop's: PatchMatch texture synthesis, LaMa neural reconstruction, and AI generation with Stable Diffusion 1.5 Inpainting — no prompt needed
    </td>
    <td width="50%" valign="top">
      🧽 <b>Remove Tool</b><br>
      Paint over or circle what you don't want and let go. AI finds the whole object's outline, so you needn't paint carefully
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      ✂️ <b>Select Subject &amp; Remove Background</b><br>
      Select the subject in one click, or click any object to select it; Remove Background hides the background with a layer mask instead of deleting pixels, and its Advanced quality recovers hair
    </td>
    <td width="50%" valign="top">
      📂 <b>Opens PSD / PSB, works with the Mac version</b><br>
      Brings in layers, masks, editable type and layer styles from PSD files; <code>.comp</code> projects open in both WinCompositor and Compositor for Mac
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      🌐 <b>English and Simplified Chinese</b><br>
      Terms match the English and Chinese editions of Photoshop; switch languages in Preferences (takes effect after a restart)
    </td>
    <td width="50%" valign="top">
      📦 <b>Ready to run</b><br>
      The .NET runtime is built in, so there's nothing else to install; the 78 MB online installer downloads only the AI models you pick, and they run offline on your PC
    </td>
  </tr>
</table>

## Screenshots

<table>
  <tr>
    <td width="50%" valign="top" align="center">
      <img src="docs/images/content-aware-fill.png" width="380" alt="The Content-Aware Fill workspace"><br>
      <b>Content-Aware Fill</b><br>
      <sub>The original on the left, with the sampling area in green; a live fill preview on the right</sub>
    </td>
    <td width="50%" valign="top" align="center">
      <img src="docs/images/remove-tool.png" width="380" alt="The Remove Tool, before and after"><br>
      <b>Remove Tool</b><br>
      <sub>Left: before. Right: after letting go — the buoy and its reflection are gone</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top" align="center">
      <img src="docs/images/camera-raw.png" width="380" alt="The Camera Raw Filter panel"><br>
      <b>Camera Raw Filter</b><br>
      <sub>Filter ▸ Camera Raw Filter… (Ctrl+Shift+A)</sub>
    </td>
    <td width="50%" valign="top" align="center">
      <img src="docs/images/hero.png" width="380" alt="The Simplified Chinese interface"><br>
      <b>Simplified Chinese</b><br>
      <sub>The Chinese interface; switch in Edit ▸ Preferences…</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top" align="center">
      <img src="docs/images/shortcuts.png" width="380" alt="The Keyboard Shortcuts reference window"><br>
      <b>Keyboard Shortcuts</b><br>
      <sub>Help ▸ Keyboard Shortcuts: the current shortcuts for tools, the canvas and menu commands</sub>
    </td>
    <td width="50%" valign="top" align="center">
      <img src="docs/images/installer.png" width="380" alt="The Select Components page of Setup"><br>
      <b>Setup</b><br>
      <sub>Choose which AI models to download while installing</sub>
    </td>
  </tr>
</table>

## Download and install

### System requirements

- 64-bit Windows 10 version 1809 or later, or Windows 11
- **Nothing else to install**: the .NET 8 runtime (with WPF) is bundled into `WinCompositor.exe` (about 83 MB), and no Visual C++ runtime is needed either
- Disk space: about 83 MB for the program, plus about 395 MB (Recommended) or about 2.2 GB (Full) for the AI models
- Select Subject, Remove Background and the other AI selection features use the graphics card when there is one; AI Generation (Stable Diffusion) runs on the graphics card and uses a lot of graphics memory

### Installing

1. Download `WinCompositor-Setup-<version>.exe` (about 78 MB) from [Releases](https://github.com/biubiu223/WinCompositor/releases).
2. Run it. Setup picks English or Simplified Chinese from your Windows display language. You can install just for yourself (no administrator rights needed) or for all users.
3. Choose an installation type. The installer holds only the program; the AI models are downloaded during setup from where each one is published (GitHub, Hugging Face), and every file is checked against its SHA-256 hash:

| Installation type | Downloads | AI features available |
|---|---|---|
| **Recommended** (default) | The core AI models, about 395 MB | Select Subject, Object Selection, Remove Background, Neural Reconstruction, Remove Tool |
| **Full** | The AI generation model as well (about 1.8 GB), about 2.2 GB in all | All of the above, plus AI Generation (Stable Diffusion 1.5 Inpainting) |
| **Program only** | No models | None; run Setup again later and select the models to add them |

A Custom installation lets you pick the components yourself. Also:

- Models from an earlier installation aren't downloaded again. If a download fails, choose **Retry**, or **Ignore** to install everything else — running Setup again later fetches whatever is missing.
- Setup adds a Start menu shortcut (a desktop shortcut is optional) and an uninstaller; uninstalling removes the downloaded models too.
- On a first installation, WinCompositor starts in the language Setup ran in; change it later in Edit ▸ Preferences… (Ctrl+K).
- To upgrade, run the new version's installer; it updates the existing installation in place.
- The first launch takes a few seconds while some native components are unpacked to a temporary folder; after that, WinCompositor starts in about a second.

> [!IMPORTANT]
> **Downloading the AI models needs access to GitHub and Hugging Face.** The Select Subject model comes from GitHub and the others from Hugging Face (huggingface.co). If a download can't get through, choose Ignore to finish installing the program and whatever did download, then run Setup again once the connection works.

> [!WARNING]
> **SmartScreen warning**: the installer isn't code-signed, so Windows may show "Windows protected your PC". Click **More info**, then **Run anyway**. Your browser may also warn that the file isn't commonly downloaded; choose Keep. The release notes list the installer's SHA-256, which you can check with PowerShell's `Get-FileHash`.

## Features

Expand a group to see everything in it. The Mac version's ⌘ is **Ctrl** on Windows, and ⌥ is **Alt**.

<details>
<summary><b>Canvas and view</b></summary>

- Several projects open as tabs; the welcome page creates a canvas (presets, size, resolution, white / black / transparent background) and opens recent projects
- Drag images or `.comp` files into the window to import or open them; paths can also be passed on the command line
- Zoom (Ctrl+scroll wheel, Ctrl+= / Ctrl+-, Zoom Tool, Fit on Screen, 100%) and pan (Space-drag, middle button, scroll wheel, Hand Tool)
- Hard-edged pixels above 200%, and a pixel grid above 800%
- Navigator: at 300% and above, a thumbnail in the canvas's top-right corner; click or drag it to move the view, or turn it off in the View menu
- Rulers, guides dragged out of the rulers, a grid, and Snap (to the canvas, layers, guides and the grid)
- Full Screen (Canvas Only) (F)

</details>

<details>
<summary><b>Files: .comp, PSD / PSB, SVG, camera raw and export</b></summary>

- Reads and writes `.comp` projects (format version 11, fully validated, atomic saves, with a QuickLook preview) — the same format as the Mac version
- A project changed by another program (a sync folder, an AI agent, git…) reloads automatically; if you have unsaved changes, you're asked first
- Opens and imports PNG, JPEG, WebP, GIF and BMP; TIFF, HEIC and others go through the codecs built into Windows; EXIF orientation is applied
- **Opens PSD / PSB**: layers, groups, layer masks, blend modes, opacity and fill, clipping masks, Levels / Curves / Hue/Saturation adjustment layers, rectangle and ellipse shape layers; anything that has to be converted is listed before the file opens
  - Type layers stay editable type (font, size, color, tracking, leading, alignment, paragraph text boxes, rotation); PostScript font names such as ArialMT and MicrosoftYaHei are matched to installed fonts
  - Layer styles come in as layer effects: Drop Shadow, Inner Shadow, Outer Glow, Inner Glow, Color Overlay and Stroke (with the global light angle, scale and hidden state); those without a counterpart, such as Bevel & Emboss and Gradient / Pattern Overlay, are listed in the import report
- Imports SVG (gradients, CSS styles, `use`, clip paths, Chinese text and more), rasterized at the canvas size
- Camera raw files (DNG and others, through the raw decoder built into Windows; other makes need the Raw Image Extension from the Microsoft Store) open in a develop panel first (Exposure, Temperature, Tint, Boost) and are developed at 16-bit precision
- Export PNG (transparency kept) and JPEG (adjustable quality), both carrying the document's resolution
- Export As… (Ctrl+Alt+Shift+W): PNG / JPEG / single-page PDF, with scaling and a background color, and a preview with the file size before exporting

</details>

<details>
<summary><b>Layers, masks and layer styles</b></summary>

- Layers and groups (group / ungroup / drag in and out / nest); groups are Pass Through, and their opacity and mask apply to every layer inside
- Opacity and 24 Photoshop blend modes (the 8 that Skia lacks, and Soft Light, are written in SkSL from Photoshop's formulas)
- Layer masks: add (Reveal All / Hide All / Reveal Selection / Hide Selection), paint on the mask, disable, apply, delete, link / unlink, Alt-click to view the mask on its own
- Clipping masks (Ctrl+Alt+G, or Alt-click the layer)
- New, duplicate, delete, rename (double-click), show / hide (drag across several eyes to toggle them at once), drag to reorder, Bring Forward / Send Backward, Merge Down / Merge Layers / Merge Group, and a context menu
- Layer styles: Stroke, Drop Shadow, Color Overlay, Inner Shadow, Outer Glow, Inner Glow; each has its own fx row in the Layers panel to turn it on or off; Copy / Paste / Clear Layer Style

</details>

<details>
<summary><b>Move, transform and select</b></summary>

- Move Tool: drag; scale with the handles (constrain proportions / Shift toggles it / Alt scales from the center); rotate (Shift snaps to 15°); flip; transform several layers or a whole group at once
- Ctrl+T Free Transform (Enter applies, Esc cancels), with exact X, Y, width, height and angle in the options bar
- Distort: hold Ctrl and drag a handle while transforming, or choose Edit ▸ Distort; works on selected pixels, several layers, or a mask on its own
- With a selection, dragging moves the selected pixels (Alt duplicates them); Auto-Select (or Ctrl-click); nudge with the arrow keys
- Rectangular / Elliptical Marquee, Lasso / Polygonal Lasso, Magic Wand (Tolerance, sample size, Contiguous, Sample All Layers)
- New / Add (Shift) / Subtract (Alt); drag inside a selection to move it; marching ants
- All, Deselect, Inverse, Expand, Contract, Feather, Layer's Pixels, Mask's Black Areas
- Color Range (Select ▸ Color Range…): sample / add / subtract with the eyedropper, Fuzziness, Invert, live preview

</details>

<details>
<summary><b>Painting, retouching and type</b></summary>

- Brush / Eraser: size, hardness, opacity, smoothing; [ ] for size, Shift+[ ] for hardness, number keys for opacity, Shift-click for straight lines, Alt to pick a color, Alt+right-drag to resize the tip
- Painting stays inside the selection, and the layer grows when you paint past its edges
- Fill with Foreground / Background Color (Alt / Ctrl+Backspace), Clear, Invert
- Eyedropper and Color Picker (HSB field, hue slider, RGB / HSB / hex)
- Gradient Tool (linear / radial / angle / reflected / diamond; adjust the handles, then apply) and Shape Tool (rectangle / ellipse / line, kept editable)
- Type Tool: point and paragraph type, Chinese input methods, font / size / color / alignment / leading / tracking, with a color for each character if you like
- Spot Healing Brush (Content-Aware / Create Texture), Clone Stamp (Alt-click to sample, Aligned), Blur / Smudge / Liquify
- Copy, Cut, Copy Merged, Paste (shared with the Windows clipboard as PNG with transparency), Layer via Copy / Layer via Cut (Ctrl+J / Ctrl+Shift+J)
- Canvas Size (anchor, relative, extension color), Image Size (resampling), Crop (aspect ratio, Alt for symmetry), rotate / flip the canvas
- Image ▸ Trim: trims the edges by transparent pixels or by the top-left / bottom-right pixel color

</details>

<details>
<summary><b>Adjustments and filters</b></summary>

- Image ▸ Adjustments: Levels (histogram, Auto, eyedroppers), Curves, Exposure, Hue/Saturation (per-color ranges, add / subtract eyedroppers), Color Balance, Black & White, Invert, Gradient Map, Grain
- Layer ▸ New Adjustment Layer: all of the above, plus Gaussian Blur, Motion Blur and Add Noise; double-click an adjustment layer to edit it again, with live preview, masks, opacity and clipping
- Filter: Last Filter (Ctrl+Alt+F), Gaussian Blur, Motion Blur, Add Noise
- Vignette (which can also be generated on an empty layer), Bloom / Glow, Tonal Contrast, Lens Correction, Dither (Atkinson, Floyd–Steinberg, Bayer, halftone dots / lines / diamonds, Mac patterns, ASCII), Scanlines

</details>

<details>
<summary><b>Camera Raw Filter</b></summary>

Filter ▸ Camera Raw Filter… (Ctrl+Shift+A). The light and color sliders use the color tables that come with the original, measured from Photoshop's Camera Raw.

- **Basic**: White Balance (with an eyedropper and Auto), Exposure, Contrast, Highlights, Shadows, Whites, Blacks, Texture, Clarity, Dehaze, Vibrance, Saturation
- **Curve** (parametric and point curves), **Detail** (Sharpening, Noise Reduction), **Color Mixer**, **Color Grading**
- **Optics** (chromatic aberration, Defringe, Lens Vignetting, Distortion), **Geometry**, **Effects** (Grain, Post Crop Vignetting, Glow), **Calibration**
- A histogram / vectorscope at the top (right-click to switch); the triangles at its top left and top right toggle the shadow / highlight clipping warnings (or press U / O); the RGB values under the pointer are shown below it
- Alt-drag the Exposure, Highlights, Shadows, Whites and Blacks sliders to see what clips; Alt-drag the sharpening Masking slider to see the mask
- The eye beside each section's title hides that section's adjustments for a moment
- Targeted Adjustment: in Curve (the parametric regions) and Color Mixer (hue / saturation / luminance), drag up or down on the image to adjust it directly
- Point Color: pick up to 8 colors and shift each one's hue / saturation / luminance and range, with Visualize Range
- Geometry ▸ Guided Upright: draw up to 4 guides on the image (vertical ones correct the perspective, the others level the picture)

</details>

<details>
<summary><b>AI selection and masking</b></summary>

The original uses macOS's Vision framework; WinCompositor uses the open IS-Net model instead, run by Windows ML (built into Windows) — on the graphics card when there is one, and fully offline.

- Select ▸ Subject (Ctrl+Alt+A): finds the main subject and selects it
- Object Selection: switch the Magic Wand's options bar to Object, then click an object to select it (Sample All Layers, edge offset)
- Filter ▸ Remove Background…: hides the background with a layer mask (no pixels are deleted, so you can always bring it back); the Advanced quality adds Refine Edge (a guided filter that recovers hair), Contrast and Shift Edge
- When there's no model beside the program (say, built from source without the models), the first use offers to download it from the [rembg](https://github.com/danielgatis/rembg) releases page into `%LOCALAPPDATA%\Compositor\Models`, or to use an `isnet-general-use.onnx`, `u2net.onnx`, `silueta.onnx` or `u2netp.onnx` you already have

</details>

<details>
<summary><b>Content-Aware Fill and Generative Fill</b></summary>

Edit ▸ Content-Aware Fill… (Shift+Backspace) opens a workspace much like Photoshop's: the original on the left, with the sampling area in green, and a live fill preview on the right; both sides zoom and pan together.

- **Select Object (AI)**: open the workspace without a selection and click an object on the left; AI (IS-Net) finds the whole object and grows it by a few pixels to make the fill area (Shift adds, Alt subtracts). With a selection, the selection is the fill area
- **Sampling Brush / Fill Brush**: add to or take away from the sampling area and the fill area (hold Alt to switch for a moment, [ ] for size), with undo and redo
- **Sampling Area**: Auto (a band around the fill area, in proportion to its size) / All / Custom; the green overlay can be shown or hidden and its opacity changed
- **Fill Method**:
  - **Texture Synthesis**: multi-scale PatchMatch patch synthesis (the same family of algorithm as Photoshop's Content-Aware Fill) builds the fill coarse to fine from the most similar patches in the sampling area. It starts at the edges and works inward ring by ring, and patches near the edge count for more in the vote, so boundaries like grass meeting water or a ridgeline carry on into the fill
  - **Smooth Fill**: extends the edge colors smoothly inward and adds grain to match the surroundings — good for skies, gradients and plain backgrounds
  - **Auto**: Smooth Fill when the edges are almost all smooth, Texture Synthesis otherwise
  - **AI Generation**: Stable Diffusion 1.5 Inpainting paints new content into the fill area from what surrounds it (no prompt needed), with its colors matched to the original along the edges. It generates once when you choose it; after you change the fill area, click Generate to update. Each run takes about 10 seconds, and Try Another gives you more to choose from
- **Neural Reconstruction**: the LaMa inpainting model rebuilds the structure (joining up hidden roads, curbs, lawns and ridgelines), then Texture Synthesis brings back the image's own grain. The structure is the AI's; Texture Synthesis only adds detail and never replaces it. Falls back to MI-GAN when LaMa isn't there
- Edit ▸ Generative Fill… opens the same workspace, starting with Texture Synthesis (+ Neural Reconstruction). Stable Diffusion is loaded only when you choose AI Generation (it uses a lot of graphics memory) — the workspace never opens in AI Generation
- **Try Another**, a preview toggle, and **Output to New Layer** (the default; only the filled pixels) or changing the current layer directly
- The fill keeps the image's own grain: the last passes use only the best-matching patches (rather than averaging dozens into a blur), and the structure the AI fills in is covered again with real texture from around it
- The models (all in the `Models\` folder beside the program, running offline, with their license files):
  - LaMa (`lama_fp32.onnx`, about 198 MB, Apache-2.0): the default for Neural Reconstruction; runs on the CPU (DirectML on the graphics card doesn't support its Fourier convolutions) in about 1–2 seconds
  - MI-GAN (`migan_pipeline_v2.onnx`, about 27 MB, MIT): the fallback when LaMa isn't there
  - Stable Diffusion 1.5 Inpainting (`sd-inpainting\`, the fp16 UNet and VAE, about 1.8 GB, CreativeML Open RAIL-M): AI Generation, on the graphics card. As there's no prompt, the 235 MB text encoder is left out; the empty prompt's encoding is precomputed (`empty_prompt.bin`, 0.2 MB) and gives the same results as the text encoder would
- Memory: AI models are loaded only when needed and released after 1.5–2 minutes idle (or when the fill workspace closes); nothing is left running after WinCompositor quits. Generation slows down when graphics memory is tight (with ComfyUI open at the same time, for example)

</details>

<details>
<summary><b>Remove Tool</b></summary>

The Remove Tool (Shift+J) sits under the Spot Healing Brush in the toolbar. Paint over what you want gone — the stroke shows in pink — and let go: it's removed in a single undoable step. It fills the same way as Texture Synthesis in Content-Aware Fill and Generative Fill: the LaMa inpainting model first rebuilds the structure (joining up curbs, lawns and planting strips), then Texture Synthesis covers it with the most similar patches from around it, bringing back the image's own grain.

- Circle an object (draw a closed loop) to remove everything inside it
- **Detect Objects (AI)**: when a stroke covers most of an object, AI (IS-Net) finds the whole object's outline and removes all of it, so you needn't paint carefully
- **Neural Reconstruction** (on by default): turn it off to use Texture Synthesis alone, without an AI model
- With a selection, it removes only inside the selection; it works on the current layer's pixels

</details>

<details>
<summary><b>Language, shortcuts and undo</b></summary>

- **English and Simplified Chinese**: terms follow the English and Chinese editions of Photoshop (in English, the Mac version's own wording comes first). Switch in Edit ▸ Preferences… (Ctrl+K); it takes effect after a restart, and by default follows the Windows display language. Names stored in project files (blend modes, undo steps and so on) stay in English as in the original, so projects open in either language and on the Mac. Translation conventions and the glossary are in [docs/localization.md](docs/localization.md) (in Chinese)
- **Search Commands** (Ctrl+F): type a name to run any menu command or switch tools
- **Edit ▸ Keyboard Shortcuts…**: customize every menu command and single-key canvas shortcut, with conflict detection; changes apply as soon as you save
- **Help ▸ Keyboard Shortcuts**: a quick reference of all the current shortcuts
- So that single-key tool shortcuts (V, M, B…) work straight away, the Chinese input method is turned off on the canvas; it works as usual in text fields
- **Undo**: one step per complete operation, up to 100 steps, with the operation's name in the menu

Default tool shortcuts:

| Tool | Shortcut | Tool | Shortcut |
|---|---|---|---|
| Move Tool | <kbd>V</kbd> | Spot Healing Brush Tool | <kbd>J</kbd> |
| Marquee Tool | <kbd>M</kbd> | Remove Tool | <kbd>Shift</kbd>+<kbd>J</kbd> |
| Lasso Tool | <kbd>L</kbd> | Clone Stamp Tool | <kbd>S</kbd> |
| Magic Wand Tool | <kbd>W</kbd> | Blur / Smudge / Liquify | <kbd>R</kbd> |
| Crop Tool | <kbd>C</kbd> | Gradient Tool | <kbd>G</kbd> |
| Brush Tool | <kbd>B</kbd> | Shape Tool | <kbd>U</kbd> |
| Eraser Tool | <kbd>E</kbd> | Type Tool | <kbd>T</kbd> |
| Eyedropper Tool | <kbd>I</kbd> | Hand Tool | <kbd>H</kbd> |
| Zoom Tool | <kbd>Z</kbd> | Full Screen (Canvas Only) | <kbd>F</kbd> |

</details>

## About the port

The SwiftUI, AppKit, Metal, Core Graphics and Core Image that the original relies on don't exist on Windows, so this port was **rewritten feature by feature from the original source** rather than recompiled:

| | Mac original | Windows version |
|---|---|---|
| Language / UI | Swift, SwiftUI + AppKit | C# (.NET 8), WPF |
| Drawing | Core Graphics, Core Image, Metal | SkiaSharp (Skia) |
| Pixel algorithms | C | C# (ported line by line from the original C) |
| Project files | `.comp` (manifest.json + PNG layers) | **The same format — each opens the other's projects** |
| Select Subject | macOS Vision framework | The open IS-Net model + Windows ML |

The Content-Aware Fill workspace, the Remove Tool, AI Generation, the keyboard shortcut reference and Preferences are new in the Windows version.

## Building from source

You'll need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0); you can also open `Compositor.Windows.sln` in Visual Studio 2022.

```powershell
# Build
dotnet build Compositor.Windows.sln

# Test
dotnet test tests/Compositor.Core.Tests

# Publish
dotnet publish src/Compositor.App -p:PublishProfile=win-x64
```

The publish settings are in `src/Compositor.App/Properties/PublishProfiles/win-x64.pubxml`: self-contained (the .NET runtime included), single file, compressed, no debug symbols, output to `dist\WinCompositor\`. The result runs without installing: the `Models\` folder beside `WinCompositor.exe` holds the AI models, so copy it along with the program.

### AI models (not in the repository)

The model files, about 2.2 GB in all, aren't in the repository. When building from source, download them into the project's `models\` folder; building and publishing copy them to `Models\` beside the program. A missing model only makes its feature unavailable — the program still builds and runs. The license files, configuration files and the precomputed `sd-inpainting\empty_prompt.bin` are in the repository.

| Path under `models\` | Source | Size | License | Used for |
|---|---|---|---|---|
| `isnet-general-use.onnx` | [rembg releases](https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx) | 170 MB | Apache-2.0 | Select Subject, Object Selection, Remove Background |
| `lama_fp32.onnx` | [Carve/LaMa-ONNX](https://huggingface.co/Carve/LaMa-ONNX/resolve/main/lama_fp32.onnx) | 198 MB | Apache-2.0 | Neural Reconstruction, Remove Tool |
| `migan_pipeline_v2.onnx` | [andraniksargsyan/migan](https://huggingface.co/andraniksargsyan/migan/resolve/main/migan_pipeline_v2.onnx) | 27 MB | MIT | Fallback inpainting model |
| `sd-inpainting\unet\model.onnx`<br>`sd-inpainting\vae_encoder\model.onnx`<br>`sd-inpainting\vae_decoder\model.onnx` | [RanaLLC/stable-diffusion-v1-5-inpainting-onnx-fp16](https://huggingface.co/RanaLLC/stable-diffusion-v1-5-inpainting-onnx-fp16/tree/main) (the folders of the same names) | 1.8 GB | CreativeML Open RAIL-M | AI Generation (optional) |

### Building the installer

You'll need [Inno Setup 6.7+](https://jrsoftware.org/isdl.php); then run:

```powershell
powershell -ExecutionPolicy Bypass -File installer/build-installer.ps1
```

It publishes to `dist\WinCompositor\` first, then compiles `installer\WinCompositor.iss` into `dist\installer\WinCompositor-Setup-<version>.exe` (the version comes from `Directory.Build.props`). The model files don't need to be on your machine to build the installer: their download URLs, sizes and SHA-256 hashes are in `WinCompositor.iss`, and must be updated there whenever a model changes. The wizard images are made from the program icon by `installer\make-wizard-images.ps1`.

## Project layout

```
src/Compositor.Core/      The core, independent of the UI (tested on its own)
  Model/                  Layers, documents, transforms, masks, selections, blend modes
  Rendering/              Layer compositing (DocumentRenderer), SkSL blenders, viewport
  Painting/               Brush strokes and pixel editing (RasterEdit / BrushStroke)
  Editing/                EditorSession: every editing operation and undo
  Adjustments/            Pixel math for the adjustments (Levels, Curves, Hue/Saturation…)
  Filters/                The Filter menu (Vignette, Dither, Scanlines, Content-Aware Fill…)
  CameraRaw/              Camera Raw Filter (measured color tables, each panel's math, histogram, preview)
  Segmentation/           Before and after the subject model (model input, guided filter, object regions)
  Inpainting/             Content-Aware Fill (automatic sampling area, PatchMatch synthesis, Smooth Fill, neural inpainting)
  Text/                   Type layout
  IO/                     .comp projects, image import and export, SVG rendering
  IO/Psd/                 PSD / PSB reading (type layers and layer styles included)
  Pixels/                 Magic Wand, Spot Healing (ports of WandPixels.c / HealPixels.c)
  Localization/           Switching the interface between Chinese and English (L.T)
  Resources/              Camera Raw color tables
src/Compositor.App/       The WPF interface
  Drawing/                Canvas control, rulers
  Panels/                 Layers panel, options bar, adjustment / filter / layer style / Color Range panels, command palette
  Dialogs/                New, Canvas Size, Image Size, Color Picker, Keyboard Shortcuts, raw develop, AI model download…
  Services/               Settings, shortcuts, running models with Windows ML
  Themes/Dark.xaml        The dark theme
models/                   The AI models' licenses and configuration (the models themselves: see above; copied to Models\ when building)
installer/                The installer script (Inno Setup), wizard images and the Chinese language file
tests/Compositor.Core.Tests/  132 tests (rendering, blend modes, brushes, selections, adjustments, styles, tools, filters,
                              distort, PSD, SVG, Camera Raw, subject detection, Content-Aware Fill, raw develop, trim,
                              project round trips, performance)
```

## License and credits

WinCompositor is released under the [MIT License](LICENSE). It is ported from the source of Robbie Tilton's [Compositor](https://github.com/robbietilton/Compositor) (Copyright (c) 2026 Wonder Assembly LLC); see [LICENSE](LICENSE) for the original copyright notice.

Each AI model has its own license; the license files sit with the models in the `Models\` folder beside the program:

| Model | Used for | License |
|---|---|---|
| IS-Net (DIS, `isnet-general-use`, as published by [rembg](https://github.com/danielgatis/rembg)) | Select Subject, Object Selection, Remove Background, Detect Objects (AI) | Apache-2.0 |
| LaMa ([Carve/LaMa-ONNX](https://huggingface.co/Carve/LaMa-ONNX)) | Neural Reconstruction, Remove Tool | Apache-2.0 |
| MI-GAN ([andraniksargsyan/migan](https://huggingface.co/andraniksargsyan/migan)) | Fallback inpainting model | MIT |
| Stable Diffusion 1.5 Inpainting ([fp16 ONNX conversion](https://huggingface.co/RanaLLC/stable-diffusion-v1-5-inpainting-onnx-fp16)) | AI Generation | CreativeML Open RAIL-M |

Thanks also to [SkiaSharp](https://github.com/mono/SkiaSharp) (MIT, built on Google's Skia), which does all the drawing; Windows ML, part of Windows, which runs the AI models; [Inno Setup](https://jrsoftware.org/isinfo.php), which builds the installer; and [xUnit](https://xunit.net/) for the tests.

<sub>Photoshop and Camera Raw are trademarks of Adobe. This project is not affiliated with Adobe.</sub>
