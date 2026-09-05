# Israeli Author Studio for macOS

Two self-contained app packages are produced:

- `IsraeliAuthorStudio-macos-arm64.zip` for Apple Silicon Macs (M1 or newer).
- `IsraeliAuthorStudio-macos-x64.zip` for Intel Macs.

## Install

1. Extract the appropriate ZIP file.
2. Drag `Israeli Author Studio.app` into the Applications folder.
3. Open it from Applications. The app starts locally and opens the project screen in the default browser.

The app stores its settings under `~/Library/Application Support/IsraeliAuthorStudio`. Story projects remain in the folders selected by the user.

## Gatekeeper

The generated package has an ad-hoc signature so it can run on Apple Silicon, but it is not Developer ID signed or notarized. On first launch, macOS may require opening System Settings > Privacy & Security and choosing **Open Anyway** after the first failed launch. Normal double-click launching works afterward.

For warning-free distribution to other people, sign the `.app` with an Apple Developer ID certificate and submit it to Apple's notarization service before distribution.
