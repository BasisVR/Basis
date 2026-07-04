# Basis Package Extraction — Working Tracker

Working doc for the `working-on-packing` branch. Goal: lift **non-core** packages out of
the Basis monorepo into their own repos on the BasisVR GitHub, list them on
`basisvr.org/packages/`, and make them installable via the Basis Package Manager.
Remove this file before merging to main.

## Conventions (locked)

- **Repo naming:** friendly PascalCase (e.g. `BasisSnapControls`), matching existing
  `BasisExamples` / `BasisGizmos` / `BasisVisualTrackers`.
- **History:** preserved per package via `git subtree split --prefix=<pkg>` (git-filter-repo
  is not installed; subtree split is built-in and correct for a single folder).
- **Disposition:** decided case-by-case per package —
  - `opt-in` = removed from the app entirely; users add it via the Package Manager.
  - `keep` = still shipped by default; the monorepo references the new repo as a UPM git dependency.
- **Publishing flow:** edit `src/BasisPM.Server/seed/packages.json` in the Package Manager repo →
  run the .NET generator → copy `catalog.json`/`packages.json`/`bundles.json` into the
  website's `packages/` folder. The seed is the source of truth; the website JSON is generated.
- **Order per package:** publish + verify the standalone package *first*, then remove from the monorepo.

## Pipeline

```
new repo (BasisVR/BasisX)  ──►  seed/packages.json  ──(generator)──►  basisvr.org/packages/  ──►  BasisPM install
   package.json at root         (source of truth)     catalog.json      cards + catalog          writes UPM git URL
```

## Central registries to trim on full removal

When a package is `opt-in` (fully removed), delete its entry from **both**:
- `Packages/com.basis.framework.editor/Editor/Documentation Engine/BasisDocGenerator.cs` → `PackageIdsToScan[]`
- `Assets/Basis/link.xml` → its `<assembly .../>` line
- …plus `Packages/packages-lock.json` (the embedded lock block) and the package folder itself.

A `keep` package stays in `link.xml` (its assembly still ships) but is sourced via git URL.

## Status

Legend: ⬜ pending · 🔄 in progress · ✅ done · ➖ later

| Package id | Proposed repo | Disposition | Status | Notes |
|---|---|---|---|---|
| com.basis.addon.snapcontrols | BasisSnapControls | opt-in | ✅ | **Pilot done** (local): live repo, listed, removed from monorepo. Pending pushes. |
| com.basis.developer.recorder | BasisRecorder | ⬜ tbd | ⬜ | "split out of framework" |
| com.basis.developer.exceptions | BasisExceptions | ⬜ tbd | ⬜ | crash/exception reporting |
| com.basis.provider.servers | BasisServersProvider | ⬜ tbd | ⬜ | Servers menu panel |
| com.basis.vehicles | BasisVehicles | keep | ⬜ | user example: keep in app |
| com.basis.imagepickup | BasisImagePickup | ⬜ tbd | ⬜ | networked image pickup |
| com.basis.pooltable | (reconcile) | ⬜ tbd | ⬜ | already listed → dooly123/MS-BASISSA-Billiards (community); embedded copy still present |
| com.basis.examples | BasisExamples *(exists)* | keep | ⬜ | update existing repo |
| com.basis.visualtrackers | BasisVisualTrackers *(exists)* | ⬜ tbd | ⬜ | update existing repo |
| com.basis.mediaplayer (+ integration.ytdlp, integration.audiolink) | BasisMediaPlayer (family) | ⬜ tbd | ⬜ | grouping TBD (own repo vs ?path=) |
| com.basis.mediapipe | BasisMediaPipe | opt-in | ⬜ | user example: opt-in |
| com.basis.openvr | BasisOpenVR | ⬜ tbd | ➖ | platform XR (versionDefines) |
| com.basis.openxr | BasisOpenXR | ⬜ tbd | ➖ | platform XR (versionDefines) |
| com.basis.shim | BasisShims | ⬜ tbd | ➖ | needs eventdriver→hai-vr.comms sever first |
| dev.hai-vr.basis.ndmf | (hai-vr upstream?) | ⬜ tbd | ➖ | Haï~ owned |
| dev.hai-vr.hvr.license-review | (hai-vr upstream?) | ⬜ tbd | ➖ | Haï~ owned |

**Framework-coupled / third-party (do not extract without refactoring):** sdk, common, server(+nested),
eventdriver, gizmos, bundlemanagement, settings, profilerintergration, openlipsync, textmeshpro, and the
vendored libs hard-referenced by `Basis Framework.asmdef` (jigglephysics, opussharp, urpvolumetricfog,
rnnoise, steamaudio, embedded URP). Several vendored libs already have BasisVR repos (UnityJigglePhysics,
OpusSharp, RNNoise.Net, steam-audio, audiolink) — those are "update existing" jobs, later.

**Cleanup freebies:** empty `Packages/org.basisvr.zstdsharp/`, empty `Packages/com.basis.profilerintegration/`
(misspelled dup of the active `...intergration`), the `Packages/Basis Server Export/` build artifact, and 6
loose `.tgz` tarballs in `Packages/`.

## Per-package checklist (repeatable)

1. `git subtree split --prefix=Packages/<id> -b extract/<name>`
2. `gh repo create BasisVR/<Repo> --public --description "..."`
3. `git push <repo-url> extract/<name>:main`
4. Add `README.md` + `.gitignore` to the new repo
5. Add entry to `src/BasisPM.Server/seed/packages.json`; run generator; copy JSON into website `packages/`
6. **Verify** standalone (package.json at root, gitUrl clones, website card renders)
7. Remove from monorepo: delete folder + trim `packages-lock.json`, `link.xml`, `BasisDocGenerator.cs`
8. Commit; review; push when approved

## Pilot log — BasisSnapControls

- [x] history extracted (git-filter-repo, 3 commits preserved, re-rooted)
- [x] README + .gitignore added (+ README.md.meta)
- [x] repo created + pushed → https://github.com/BasisVR/BasisSnapControls (public, main)
- [x] seed + generator + website (packages.json, catalog.json, index.html deployed)
- [x] verified standalone (fresh clone resolves com.basis.addon.snapcontrols v0.0.1)
- [x] removed from monorepo (folder deleted; trimmed packages-lock.json, link.xml, BasisDocGenerator.cs)

**Pilot complete (local).** Pending pushes: BasisPM seed · website (basisvr.org) · monorepo branch.
