MirrorDeck 1.0.36 Portable

Run MirrorDeck.App.exe. No installer is required.
Keep MirrorDeck.portable beside the application to store settings locally.
Keep the PreviewHost folder beside MirrorDeck.App.exe.

Image contains the scaling filters and their live controls. Native DLSS
4.5 and experimental DLSS 5 appear only on an RTX rendering adapter.
Display contains sizing, window preferences, pacing and the FPS overlay.
Open/stop and restart preview are available in the top toolbar.

ReShade installation controls have been removed. ReShade is NOT installed
in this portable. Existing add-ons, presets and settings are preserved.
Native DLSS is bypassed while an externally installed ReShade is loaded to prevent
competing NGX sessions. See NATIVE-DLSS.txt for runtime provenance,
compatibility limitations, performance measurements and release notes.

Fresh settings auto-start the first available video device. Saved device
selections are restored; missing devices never silently select another.
An existing startup opt-out remains respected. Preview launch size fits
the monitor, at most 1280x720, independently of the processing resolution.

DLSS 5 repeated passes: Off (single pass), 2x, 3x, 4x. Off is the default.
Additional passes can exaggerate artifacts and increase GPU cost.
4x exceeded the 60 FPS frame budget on our RTX 5070 Ti test.

Updates checks the official EDMIRE2k/MirrorDeck GitHub stable releases.
The Updates screen lists all connection sources, exact download URL,
release notes and SHA-256. Downloads and installation require your action.
The updater verifies the digest, keeps existing settings/add-ons/presets,
backs up the old application beside its folder and restarts MirrorDeck.
Check at startup can be disabled in Updates. No telemetry is collected.
