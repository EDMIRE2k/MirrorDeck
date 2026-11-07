# MirrorDeck
A tool that is designed to take media capture devices, such as capture cards and allow near seamless mirroring with as minimal input delay as possible, on top of supporting native DLSS upscaling for lower resolution capture cards, as well as experimental support for native DLSS 5 injection into the live video feed.

## Download

Use the Windows x64 portable ZIP under [Releases](https://github.com/EDMIRE2k/MirrorDeck/releases), not GitHub's source-code ZIP. Extract it and run `MirrorDeck.App.exe`. ReShade is not installed or required.

## Native Processing

Spatial scaling works on supported DirectX 12 GPUs. DLSS 4.5 and experimental neural rendering are offered only on RTX adapters. Capture frames do not contain engine depth or motion vectors; MirrorDeck estimates motion and cannot reproduce an engine-integrated DLSS pipeline.

Repeated neural rendering (2x, 3x, 4x) is optional and off by default. Extra passes cost GPU time and can exaggerate artifacts. The 4x test exceeded the 60 FPS budget on an RTX 5070 Ti. Other RTX generations are not hardware-verified.
