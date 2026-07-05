# Basis Package Extraction — Working Tracker

Working doc for the `working-on-packing` branch. Goal: lift **non-core** packages out of
the Basis monorepo into their own repos on the BasisVR GitHub, list them on
`basisvr.org/packages/`, and make them installable via the Basis Package Manager.
Remove this file before merging to main.

## Conventions (locked)

- **Repo naming:** friendly PascalCase (e.g. `BasisSnapControls`, `BasisAvatarRecorder`),
  matching existing `BasisExamples` / `BasisGizmos` / `BasisVisualTrackers`.
- **History:** preserved per package via **git-filter-repo** on a throwaway hardlinked
  `--no-checkout` clone: `python git-filter-repo --force --subdirectory-filter Basis/Packages/<id>`.
  (Built-in `git subtree split` is far too slow on the ~5700-commit history.) filter-repo isn't
  installed system-wide — the single script sits in the session scratchpad.
- **Disposition (case-by-case):** `opt-in` = removed from the app; users add it via the Package
  Manager. `keep` = still shipped; the monorepo references the new repo as a UPM git dependency.
- **Publish-first, batch removals:** *publishing* a package (repo → seed → generator → website)
  needs no Unity and its localization travels with it, so publish freely. The monorepo *removal*
  of a **localization-bearing** package needs a Unity pass to regenerate
  `Assets/AddressableAssetsData/AssetGroups/Basis Localization.asset`, so collect those removals
  into one Unity session. Packages with no `Localization/Languages/` (e.g. snapcontrols) can be
  removed immediately without Unity.
- **Publishing flow:** edit `src/BasisPM.Server/seed/packages.json` in the Package Manager repo
  (source of truth) → `dotnet run --project src/BasisPM.Server -- generate <out>` (needs .NET 9;
  `GITHUB_TOKEN` for stats) → copy `packages.json`/`catalog.json`/`index.html` into the website's
  `packages/`. Repo root is `Github/Basis`; packages live at `Basis/Packages/<id>`.

## Pipeline

```
new repo (BasisVR/BasisX)  ──►  seed/packages.json  ──(generator)──►  basisvr.org/packages/  ──►  BasisPM install
   package.json at root         (source of truth)     catalog.json      cards + catalog          writes UPM git URL
```

## Removal surface (when a package leaves the monorepo)

Delete the package folder, then trim its entries from:
- `Basis/Packages/packages-lock.json` — the embedded lock block
- `Basis/Assets/Basis/link.xml` — its `<assembly .../>` preserve line (a `keep` package stays here)
- `Basis/Packages/com.basis.framework.editor/Editor/Documentation Engine/BasisDocGenerator.cs` — its `PackageIdsToScan[]` entry
- **If it has `Localization/Languages/`:** `Basis/Assets/AddressableAssetsData/AssetGroups/Basis Localization.asset`
  holds auto-generated `Languages/<pkgid>/<lang>` entries. Unity's Addressables postprocessor prunes
  them on next open — **do a Unity pass; don't hand-edit the group asset.**

## Status

Legend: ⬜ pending · 🔄 in progress · ✅ done · ➖ later

| Package id | Repo | Disposition | Status | Notes |
|---|---|---|---|---|
| com.basis.addon.snapcontrols | BasisSnapControls | opt-in | ✅ | Live + listed + removed from monorepo (no localization). Fully done + pushed. |
| com.basis.developer.recorder | BasisAvatarRecorder | opt-in | 🔄 | Live + listed. **Removal deferred** (18-lang localization → Unity batch). |
| com.basis.developer.exceptions | BasisExceptionReporting | opt-in | 🔄 | Live + listed. **Removal deferred** (localization → Unity batch). |
| com.basis.provider.servers | BasisServersProvider | opt-in | 🔄 | Live + listed. **Removal deferred** (localization `menu.servers.*` co-owned w/ framework → Unity batch). |
| com.basis.vehicles | BasisVehicles | keep | 🔄 | Live + listed. **keep-swap deferred** to Unity (add git dep + remove embedded folder; verify resolve+build). No localization. |
| com.basis.imagepickup | BasisImagePickup | opt-in | ✅ | Live + listed + **removed from monorepo** (no localization). Fully done. |
| com.basis.pooltable | (reconcile) | ⬜ tbd | ⬜ | already listed → dooly123/MS-BASISSA-Billiards (community); embedded copy still present |
| com.basis.examples | BasisExamples *(exists)* | keep | ⬜ | update existing repo |
| com.basis.visualtrackers | BasisVisualTrackers *(exists)* | ⬜ tbd | ⬜ | update existing repo |
| com.basis.mediaplayer (+ integration.ytdlp, integration.audiolink) | BasisMediaPlayer (family) | ⬜ tbd | ⬜ | grouping TBD (own repo vs ?path=) |
| com.basis.mediapipe | BasisMediaPipe | opt-in | ⛔ | **Blocked:** vpmDeps on `com.github.homuler.mediapipe` + `dev.hai-vr.basis.comms` must be made installable first. Has localization. |
| com.basis.openvr | BasisOpenVR | ⬜ tbd | ➖ | platform XR (versionDefines) |
| com.basis.openxr | BasisOpenXR | ⬜ tbd | ➖ | platform XR (versionDefines) |
| com.basis.shim | BasisShims | ⬜ tbd | ➖ | needs eventdriver→hai-vr.comms sever first |
| dev.hai-vr.basis.ndmf | (hai-vr upstream?) | ⬜ tbd | ➖ | Haï~ owned |
| dev.hai-vr.hvr.license-review | (hai-vr upstream?) | ⬜ tbd | ➖ | Haï~ owned; has localization |

**Framework-coupled / third-party (do not extract without refactoring):** sdk, common, server(+nested),
eventdriver, gizmos, bundlemanagement, settings, profilerintergration, openlipsync, textmeshpro, and the
vendored libs hard-referenced by `Basis Framework.asmdef` (jigglephysics, opussharp, urpvolumetricfog,
rnnoise, steamaudio, embedded URP). Several vendored libs already have BasisVR repos (UnityJigglePhysics,
OpusSharp, RNNoise.Net, steam-audio, audiolink) — those are "update existing" jobs, later.

**Cleanup freebies:** empty `Packages/org.basisvr.zstdsharp/`, empty `Packages/com.basis.profilerintegration/`
(misspelled dup of the active `...intergration`), the `Packages/Basis Server Export/` build artifact, and 6
loose `.tgz` tarballs in `Packages/`.

## Deferred monorepo changes (batch into one Unity pass)

Removals (regenerate `Basis Localization.asset` — localized):
- com.basis.developer.recorder (BasisAvatarRecorder) — published 2026-07-05
- com.basis.developer.exceptions (BasisExceptionReporting) — published 2026-07-05
- com.basis.provider.servers (BasisServersProvider) — published 2026-07-05

Keep-swaps (add git dep to `Packages/manifest.json` + remove embedded folder; Unity-verify resolve+build):
- com.basis.vehicles (BasisVehicles) — published 2026-07-05 · no localization

## Per-package checklist (repeatable)

1. Hardlinked clone: `git clone --no-checkout <monorepo> <scratch>/<Repo>-extract`
2. `python <scratch>/git-filter-repo --force --subdirectory-filter Basis/Packages/<id>` ; `git branch -M main`
3. Add `README.md` (+`.meta`), `LICENSE` (+`.meta`, if missing), `.gitignore`; commit (unsigned)
4. `gh repo create BasisVR/<Repo> --public --description "..." --source <clone> --remote origin --push`
5. Seed entry → run generator → copy JSON (+`index.html` if changed) into website `packages/`; commit + push website + PM
6. **Verify:** fresh clone of the git URL resolves the package (`package.json` at root)
7. **Remove from monorepo** — folder + `packages-lock.json` + `link.xml` + `BasisDocGenerator.cs`; if localized, defer to the Unity batch. Commit + push.

## Progress log

- **BasisSnapControls** (com.basis.addon.snapcontrols) — ✅ DONE: repo live, listed, removed from monorepo, all pushed. No localization.
- **BasisAvatarRecorder** (com.basis.developer.recorder) — 🔄 published: repo live, listed, seed + website pushed. Removal deferred to the Unity batch (18-language localization).
- **BasisExceptionReporting** (com.basis.developer.exceptions) — 🔄 published: repo live, listed, seed + website pushed. Removal deferred to the Unity batch (localization).
- **BasisServersProvider** (com.basis.provider.servers) — 🔄 published: repo live, listed, pushed. Removal deferred to the Unity batch (localization).
- **BasisImagePickup** (com.basis.imagepickup) — ✅ DONE: repo live, listed, removed from monorepo. No localization.
- **BasisVehicles** (com.basis.vehicles) — 🔄 published: repo live, listed, pushed. keep-swap deferred to Unity (git dep + remove embedded; no localization).
