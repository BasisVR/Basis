# Basis Exception Reporting

Captures exceptions and errors, stores crash reports across sessions and replays them on
reconnect, and uploads them to the server. Split out of the Basis framework into its own
package.

This package was extracted from the Basis monorepo so it can be versioned and installed on
its own. It is an **optional developer tool** — install it only in projects that use it.

## Requirements

Requires a Basis project. Depends on:

- `com.basis.framework`
- `com.basis.server`
- `com.basis.sdk`

## Installation

### Basis Package Manager (recommended)

Search for **Basis Exception Reporting** in the
[Basis Package Manager](https://github.com/BasisVR/BasisPackageManager) and install.

### Unity Package Manager (git URL)

In Unity: **Window → Package Manager → + → Add package from git URL…**, then paste:

```
https://github.com/BasisVR/BasisExceptionReporting.git
```

### Manual (`Packages/manifest.json`)

```json
"com.basis.developer.exceptions": "https://github.com/BasisVR/BasisExceptionReporting.git"
```

## Localization

Ships UI strings in 18 languages under `Localization/Languages/`. Basis auto-registers a
package's language files when it is installed, so no extra setup is needed.

## License

[MIT](LICENSE) © BasisVR
