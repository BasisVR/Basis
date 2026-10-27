# Basis Avatar Recorder

Records the local humanoid avatar's pose to disk every frame and converts recordings
into Unity humanoid `AnimationClip`s. Clips carry the avatar's root motion (joystick
locomotion and turning) as `RootT`/`RootQ`, and non-humanoid bones (jiggle bones, hair,
tails, accessories) as transform curves. Both are on by default in the recorder settings. Split out of the Basis framework Developer tab
into its own package.

This package was extracted from the Basis monorepo so it can be versioned and installed
on its own. It is an **optional developer tool** — install it only in projects that use it.

## Requirements

Requires a Basis project. Depends on:

- `com.basis.framework`
- `com.basis.sdk`
- `com.basis.settings`
- `com.basis.common`

## Installation

### Basis Package Manager (recommended)

Search for **Basis Avatar Recorder** in the
[Basis Package Manager](https://github.com/BasisVR/BasisPackageManager) and install.

### Unity Package Manager (git URL)

In Unity: **Window → Package Manager → + → Add package from git URL…**, then paste:

```
https://github.com/BasisVR/BasisAvatarRecorder.git
```

### Manual (`Packages/manifest.json`)

```json
"com.basis.developer.recorder": "https://github.com/BasisVR/BasisAvatarRecorder.git"
```

## Localization

Ships UI strings in 18 languages under `Localization/Languages/`. Basis auto-registers a
package's language files when it is installed, so no extra setup is needed.

## License

[MIT](LICENSE) © BasisVR
