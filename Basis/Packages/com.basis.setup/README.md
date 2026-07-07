# Basis Setup

Some project configuration cannot live in a package — Unity resolves it only from
fixed `Assets/` locations (Addressables data, XR loaders, URP render pipeline defaults,
quality settings, the Input Actions asset, `link.xml`, Android gradle templates, and the
SteamVR binding files under `StreamingAssets/`). As Basis moves its sources into packages,
those files still have to be generated into the consuming project.

This package builds them from code and keeps them current.

- **Creator** — generates each file into its required `Assets/` path when it is missing.
- **Merger** — brings an existing (older) copy up to the latest shipped defaults, backing
  up the previous version to `BasisSetupBackups/` first. Additive modules (`link.xml`,
  Addressables groups) only add what is missing and never remove your own entries.

## Usage

`Basis ▸ Project Setup ▸ Configuration` opens the window listing every module with its
status (missing / out of date / up to date / not applicable). Buttons:

- **Create Missing Assets** — safe; only writes files that do not exist yet.
- **Update All To Latest** — regenerates managed files to current defaults (backs up first).

Per-module Create / Update / Locate buttons are in each row. The same actions are on the
`Basis ▸ Project Setup` menu.

Applied versions are tracked in `ProjectSettings/BasisSetup.json` so the merger knows which
files are out of date.

## Extending

Implement `IBasisSetupModule` (or derive `BasisSetupModuleBase`) with a parameterless
constructor in any editor assembly; it is discovered by reflection and appears in the window.
Vendor integrations live in their own define-gated assemblies so the package still compiles
when the vendor package is absent.

## Modules

| Category | Generates |
|---|---|
| Addressables | `AddressableAssetSettings` + profiles, data builders, Basis groups |
| XR | `XRGeneralSettingsPerBuildTarget`, loaders, OpenXR/OpenVR settings |
| Rendering | URP renderers, volume profiles, global settings |
| Quality | Per-platform quality profiles |
| Input | `InputAction.inputactions`, input settings |
| Highlight | Desktop / Android highlight settings |
| Steam Audio | `SteamAudioSettings` + material presets |
| Linker | `Assets/Basis/link.xml` |
| Android | `AndroidManifest.xml` + gradle templates |
| SteamVR | `StreamingAssets/SteamVR` action + binding files |
