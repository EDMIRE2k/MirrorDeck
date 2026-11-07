# MirrorDeck

MirrorDeck is a Windows capture-and-display application for viewing capture cards, cameras, desktops, and individual windows in a separate, low-latency preview. It combines GPU scaling, image cleanup, audio monitoring, and experimental native DLSS processing in one settings interface.

Use it to view a console through a capture card, enlarge lower-resolution footage, or mirror a desktop or game window with image processing applied. MirrorDeck is a local preview tool, not a replacement for OBS recording and broadcasting.

[Download releases](https://github.com/EDMIRE2k/MirrorDeck/releases) · [Report an issue](https://github.com/EDMIRE2k/MirrorDeck/issues)

> Native DLSS features are experimental. MirrorDeck processes an already-rendered video feed, not the game's internal rendering data. Results and performance vary with the source, GPU, driver, and selected settings. ReShade is not required.

## Requirements

- Windows 10/11, 64-bit. Desktop and window capture require Windows Graphics Capture support.
- A compatible graphics adapter and driver. DirectX 12 is the primary preview renderer; native DLSS requires the DirectX 12 path and a supported NVIDIA RTX adapter.
- A capture card or camera for video-device input, or a desktop/window available for capture.
- Enough GPU performance for the selected input resolution, output resolution, and effects.

Spatial filters are available without an RTX GPU. DLSS 4.5 and DLSS 5 are hidden when MirrorDeck's rendering adapter is not identified as RTX. Appearing in the list does not guarantee that a particular runtime/driver combination will initialize successfully.

## Quick Start

1. Download `MirrorDeck-<version>-win-x64.zip` from **Releases**. The automatically generated **Source code** ZIP is not the runnable application.
2. Extract the entire ZIP into a folder. Keep `PreviewHost`, `MirrorDeck.portable`, and the other supplied files beside the application.
3. Run `MirrorDeck.App.exe`. Windows may request administrator approval for this build.
4. Open **Capture + Audio**, select a **Source type**, then select your **Source** and **Input format**.
5. Select the appropriate audio **Input** and **Output**. Adjust volume or mute as needed.
6. Use **Image** for scaling and cleanup, and **Display** for fullscreen-related layout, preview preferences, and frame pacing.

With fresh settings, MirrorDeck opens the first available video device automatically. Later launches restore the saved device when automatic startup is enabled. A missing remembered device does not silently switch to another device. Change this behavior under **Advanced → Open last device automatically**.

Closing the preview keeps it closed until you explicitly open or restart it. Closing the main settings window also closes its preview.

## Choosing a Source

| Source type | Captures | Notes |
| --- | --- | --- |
| **Video Devices** | Capture cards and cameras | Select a resolution, frame rate, and pixel format advertised by the device's driver. |
| **Desktop** | A complete display | Mirrors the selected display. Avoid capturing a display containing the preview if you do not want a repeated mirror effect. |
| **Window** | An individual window, including capturable game windows | Uses Windows capture rather than an OBS-style game-injection hook. Some protected content or applications may not be capturable. |

**Capture mouse pointer** controls cursor inclusion for desktop/window sources and is enabled by default. Select audio separately: choosing a window is not an automatic selection of that application's audio stream.

The rescan button in **Capture + Audio** refreshes connected devices and available input formats. The restart button in the top toolbar restarts the preview using the current selection and settings.

## Settings Overview

| Tab | Controls |
| --- | --- |
| **Capture + Audio** | Source type, source, input format, cursor capture, audio devices, volume, mute, delay offset, and channel mode. |
| **Image** | Image enhancements, scaling filters, edge cleanup, and filter-specific spatial or neural controls. |
| **Display** | Preview sizing, aspect ratio, fill behavior, processing resolution, always-on-top, borderless mode, opacity, frame pacing, and the FPS overlay. |
| **Profiles** | Built-in profiles and named configurations you can save, load, or delete. |
| **Advanced** | Startup behavior, maintenance, diagnostics, settings/log locations, and editable hotkeys. |
| **Updates** | Release checks, downloads, verified installation, release notes, and visible connection sources. |

Most image adjustments update the running preview live and settings save automatically. Text fields generally apply when you leave the field. Changing a source or input format restarts an already-open preview automatically; changes to neural models, working resolution, or pass count may briefly rebuild processing resources.

### Toolbar and Hotkeys

The top toolbar provides **Open/Stop preview**, **Restart preview**, **Mute**, **Fullscreen**, and **Screenshot**. Screenshots are saved under **Pictures/MirrorDeck**.

| Action | Default shortcut |
| --- | --- |
| Toggle fullscreen | `F11` |
| Toggle mute | `Ctrl+M` |
| Toggle stats overlay | `Ctrl+T` |
| Toggle always-on-top | `Ctrl+P` |
| Save a screenshot | `Ctrl+S` |
| Relaunch preview | `Ctrl+R` |

Edit shortcuts under **Advanced → Hotkeys**. Use them with a MirrorDeck window focused; they are not system-wide global shortcuts.

## Scaling and Image Cleanup

Turn on **Image → Enable image enhancements**, then choose a **Scaling filter**. Available controls change with the selected filter.

| Filter | Purpose |
| --- | --- |
| Nearest neighbor | Hard-edged pixel enlargement without smoothing. |
| Bilinear | Lightweight, smooth scaling. |
| Bicubic / Lanczos | Alternative reconstruction filters with different sharpness and ringing characteristics. |
| Sharp bilinear | Pixel-oriented scaling with a sharper appearance. |
| FSR EASU | Edge-adaptive spatial upscaling for general footage. This is not temporal FSR or frame generation. |
| Anime line art | Anime4K-inspired line cleanup for stylized, lower-resolution material. It is not a full Anime4K implementation. |
| DLSS 4.5 | Experimental native RTX super resolution using estimated motion. |
| DLSS 5 | Experimental native neural image enhancement with live effect controls. |

Upscaling cannot recover every detail absent from the captured image. Compression, source antialiasing, capture-card processing, and noisy input can all limit the result. More sharpening or more neural passes is not necessarily better.

### DLSS 4.5: Upscaling to a Target Resolution

1. Select the actual capture resolution under **Capture + Audio → Input format**, for example 1920 × 1080.
2. Under **Image**, enable enhancements and select **DLSS 4.5**.
3. Choose **DLSS output resolution**, such as 1440p, 4K, preview-window size, or the current monitor size.
4. Adjust **DLSS sharpness**, **Motion estimation**, and **Text / detail protection** while watching the preview.

The capture format and upscale target are separate. Choosing a 4K target does not make the capture card send 4K frames. Fixed targets are processed at the selected resolution even in a smaller preview; window/monitor targets follow the corresponding display dimensions. The source aspect ratio is preserved.

The model selector includes **Auto**, **K**, **L**, and **M**. K uses the older DLSS 4 preset; L and M use DLSS 4.5 presets. Auto selects according to the scale ratio. These are runtime presets, not an arbitrary model-file loader, and a different preset is not automatically a quality improvement.

Higher motion-estimation quality costs GPU time. More text/detail protection helps preserve source UI and fine features but can reduce how much those regions are reconstructed. The sharpness slider applies MirrorDeck's GPU post-sharpening rather than NVIDIA's deprecated DLSS sharpness parameter.

### DLSS 5: Native Neural Rendering

Select **DLSS 5** in the scaling filter list. No ReShade installation or overlay is needed. Its controls include:

- Working resolution, style, and model preset hint.
- NR intensity, local tone strength, local structure strength, and skin structure strength.
- Automatic mask, UI correction, color strength, and effect blend.
- Optional repeated neural rendering: **Off**, **2x**, **3x**, or **4x**.

**NR intensity = 0** bypasses neural evaluation while keeping its resources ready. Use it for a quick comparison with the source without repeatedly releasing and rebuilding the neural feature.

**Repeat neural rendering: Off means one normal pass**, not that DLSS 5 is disabled. The 2x, 3x, and 4x options reapply the effect using separate temporal histories. They are off by default and can increase artifacts, GPU time, and VRAM usage. They do not generate extra frames.

Neural rendering uses its own **working resolution**. MirrorDeck processes that image, blends source detail back at capture resolution, then uses spatial scaling for presentation. This is different from DLSS 4.5's super-resolution target. Lower working resolutions reduce processing cost. Experimental preset hints may be ignored by the runtime.

> In a local RTX 5070 Ti test with 1080p input, 720p neural working resolution, and a 4K output surface, four passes took about **17.85 ms median / 20.42 ms p95** including GPU completion. That exceeds the **16.7 ms** budget for 60 FPS. Additional passes cannot be promised to maintain 60 FPS.

The included native NR compatibility runtime is community-modified, not an NVIDIA-signed universal-RTX release. It is intended to support multiple RTX generations, but only the available RTX 5070 Ti has been hardware-tested for this build. See [runtime details and limitations](NATIVE-DLSS.txt).

## Frame Pacing and Latency

Fresh settings use **Low-latency mode on**, **Frame buffering 2**, **V-sync on**, **Drop late frames off**, and **FPS cap 0**. Zero means no additional application FPS cap; it does not create frames beyond the source or remove display/processing limits.

Low-latency mode prefers the newest available capture frame. Turning it off allows timestamped buffering to prioritize steadier delivery at the cost of added latency. V-sync coordinates presentation with the display to reduce tearing. The frame-buffer setting is not a promise of exactly two frames of end-to-end delay.

Use **Display → Capture Overlay** to enable the FPS display and choose its corner and size. An FPS counter alone cannot establish perfect frame pacing or measure controller-to-display latency.

If a neural filter cannot keep up, reduce its working/output resolution, reduce repeated passes, or use a spatial filter. A 30 FPS source does not become a true 60 FPS source simply by selecting a 60 Hz display. MirrorDeck does not currently provide frame generation.

## How MirrorDeck Works

```text
Capture card / camera       Desktop / window
        |                         |
   DirectShow             Windows Graphics Capture
        +------------+------------+
                     |
       Timestamped, bounded frame queue
                     |
       Dedicated preview rendering worker
                     |
      Spatial scaling or native RTX processing
                     |
         DXGI presentation / preview window

Audio input -> WASAPI monitoring -> selected audio output
```

The main application owns device selection, settings, profiles, and audio monitoring. A separate **MirrorDeck.PreviewHost** process owns the preview and GPU rendering. Live render settings are exchanged through an atomically updated settings file.

The capture queue bounds outstanding frames. Source timestamps, high-resolution deadlines, and DXGI frame-latency waits help schedule presentation without relying on a Windows Forms repaint timer. Desktop/window capture uses asynchronous GPU readback and pooled CPU buffers; this is **not a fully zero-copy GPU pipeline**.

Native RTX processing estimates movement using NVIDIA Optical Flow and uses a flat depth guide. A captured video feed does not supply engine motion vectors, actual scene depth, geometry, or original subpixel samples. Consequently, this integration cannot offer the same guarantees as DLSS built directly into a game. Menus, transitions, occlusion, and fine moving details remain challenging cases.

The preview initially fits within the monitor's work area, capped at 1280 × 720. This controls the launch window, not the capture resolution or a fixed neural output target. Fullscreen uses the display area independently.

## Profiles and Saved Settings

Under **Profiles**, enter a name and choose **Save Current** to save the current configuration. Use **Load** to restore it or **Delete** to remove that saved profile. Built-in profiles provide starting configurations; review their device and performance settings before use.

In portable mode, `MirrorDeck.portable` keeps settings and profiles with the application. Without portable mode, settings are stored under `%LOCALAPPDATA%\MirrorDeck`. The exact settings, profile, and log paths are shown in the app.

Keep a backup of `MirrorDeck.settings.json` and your `configs` folder before manually replacing an installation. Extracting a fresh release directly over an old folder can overwrite files; the built-in updater explicitly preserves existing settings and profiles.

## Updates and Network Connections

Use **Updates → Check for updates**, then **Download update** and **Install and restart**. Installation asks for confirmation, closes the preview, keeps existing settings and profiles, retains a backup of the previous application, and restarts MirrorDeck.

Startup update checks can be disabled in the Updates tab. A check does not automatically download or install a release.

| Connection | Purpose |
| --- | --- |
| `api.github.com` | Read published stable-release information for `EDMIRE2k/MirrorDeck`. |
| `github.com/EDMIRE2k/MirrorDeck` | Download the selected release asset. |
| `release-assets.githubusercontent.com` | Receive the file through GitHub's asset-delivery service. |

The screen shows the download URL, last connection, release notes, and GitHub-provided SHA-256 digest. Download size and digest are checked before installation; the installer re-verifies the archive and validates its paths and manifest. Missing or mismatched digests block installation. A checksum verifies integrity, not independent publisher identity: trust still depends on the repository and its release assets.

MirrorDeck does not upload the captured feed for these effects. Processing happens locally. The updater sends ordinary GitHub requests, including the request IP address and application user agent; it does not require a GitHub account or collect application telemetry.

## ReShade Compatibility

ReShade is not installed by default, is not needed for native processing, and no longer has install/uninstall controls in MirrorDeck. Legacy add-ons, presets, and settings are retained for existing setups.

Native DLSS processing is bypassed while an external ReShade proxy is loaded, to avoid competing NGX ownership. Do not expect native DLSS and ReShade's neural add-ons to run together. For an existing ReShade-enabled installation, use a separate clean portable extraction to test the native path without deleting your old files.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| No preview | Check Source type and Source, then choose Open preview. If the remembered device is disconnected, select an available device. |
| Device or desired format missing | Rescan devices, check the card's driver and USB connection, and close other applications using it. Available modes come from the driver; an advertised HDMI input mode is not necessarily a USB capture output mode. |
| No audio | Select the correct capture audio input and playback output; check volume and mute. Video and audio devices are configured separately. |
| DLSS options absent | Check the GPU shown in the main window. DLSS options are intentionally hidden for non-RTX rendering adapters. |
| DLSS selected but not taking effect | Confirm image enhancements are enabled, check the target/working resolution and diagnostics, and test without an external ReShade proxy. |
| Unstable text or excessive neural changes | Increase text/detail protection for DLSS 4.5, or reduce NR intensity/blend/repeated passes for DLSS 5. Compare against the source or a spatial filter. |
| FPS falls with enhancements | Reduce the output or neural working resolution and pass count. Check GPU load and the actual source frame rate. |
| Window capture is blank | Check whether the application is minimized, protected, or incompatible with Windows capture. Try desktop capture where appropriate. |
| Update unavailable | Only published stable releases with the expected portable asset and digest are eligible. A draft, prerelease, GitHub rate limit, or missing release asset can prevent an update offer. |

When [reporting a bug](https://github.com/EDMIRE2k/MirrorDeck/issues), include the app version, Windows version, GPU/driver, source device or application, input format, filter settings, output resolution, and steps to reproduce. Include relevant **Advanced → Diagnostics** output, but remove personal paths or device identifiers you do not want to share publicly.

## Building and Contributing

See [GITHUB-SETUP.txt](GITHUB-SETUP.txt) for build prerequisites, packaging, release naming, updater behavior, and rollback instructions. The recovered project currently requires its local dependency inputs and a self-contained portable template; cloning the source alone is not sufficient for a complete release build.

The repository includes settings/UI regression tests, capture pacing and native GPU tests, and updater validation tests. GPU results on one machine do not establish performance or compatibility across every RTX generation or capture device.

## License and Third-Party Components

MirrorDeck's own code is covered by the repository [LICENSE](LICENSE). Third-party libraries, SDKs, shaders, models, modified runtimes, and add-ons retain their respective terms; the repository license does not relicense them.

Read [THIRD-PARTY-UPSCALERS.txt](THIRD-PARTY-UPSCALERS.txt) and [NATIVE-DLSS.txt](NATIVE-DLSS.txt), and review the bundled notices before redistributing a binary package. MirrorDeck is an independent project, not an official NVIDIA product.
