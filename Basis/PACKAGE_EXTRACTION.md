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
| com.basis.pooltable | BasisPoolTable | opt-in | ✅ | Re-homed to BasisVR (was dooly123 community listing); listed curated; **removed from monorepo**. No localization. |
| com.basis.examples | BasisExamples | keep | 🔄 | Live + listed; stale 2025 repo **overwritten** (Avatars/Shaders discarded). keep-swap deferred to Unity. No localization. |
| com.basis.visualtrackers | BasisVisualTrackers | keep | 🔄 | Live + listed; existing repo **overwritten** with the monorepo copy (fallback-only; old HTC/Index/Tundra/Meta models discarded per user). keep-swap deferred to Unity. |
| com.basis.mediaplayer | BasisMediaPlayer | opt-in | 🔄 | Live + listed (native codec libs; ships own README/LICENSE/THIRD_PARTY_NOTICES). **Removal blocked by shim** (shim vpm-deps mediaplayer). |
| com.basis.integration.ytdlp | BasisYtDlpIntegration | opt-in | ✅ | Live + listed + **removed from monorepo** (leaf, no localization). |
| com.basis.integration.audiolink | BasisAudioLinkIntegration | opt-in | ✅ | Live + listed + **removed from monorepo** (leaf, no localization). |
| com.basis.mediapipe | BasisMediaPipe | opt-in | ⛔ | **Blocked:** vpmDeps on `com.github.homuler.mediapipe` + `dev.hai-vr.basis.comms` must be made installable first. Has localization. |
| com.basis.openvr | BasisOpenVR | keep | 🔄 | Live + listed. Platform VR (keep). Deps now published: [BasisSteamVR] + [BasisOpenVRPlugin]. keep-swap deferred to Unity. |
| com.steam.steamvr | BasisSteamVR | keep | 🔄 | Live + listed. Valve SteamVR (BSD-3), trimmed for Basis. Dep of openvr; keep-swap deferred. |
| com.valvesoftware.unity.openvr | BasisOpenVRPlugin | keep | 🔄 | Live + listed. Valve OpenVR XR plugin (BSD-3, 20 MB w/ native libs). Dep of openvr; keep-swap deferred. |
| com.basis.openxr | BasisOpenXR | keep | 🔄 | Live + listed (platform VR; app needs it). keep-swap deferred to Unity. No localization. |
| com.basis.shim | BasisShims | ⬜ tbd | ➖ | needs eventdriver→hai-vr.comms sever first |
| dev.hai-vr.basis.ndmf | (hai-vr upstream?) | ⬜ tbd | ➖ | Haï~ owned |
| dev.hai-vr.hvr.license-review | (hai-vr upstream?) | ⬜ tbd | ➖ | Haï~ owned; has localization |

**Framework-coupled / third-party (do not extract without refactoring):** sdk, common, server(+nested),
eventdriver, gizmos, bundlemanagement, settings, profilerintergration, openlipsync, textmeshpro, and the
vendored libs hard-referenced by `Basis Framework.asmdef` (jigglephysics, opussharp, urpvolumetricfog,
rnnoise, steamaudio, embedded URP). Several vendored libs already have BasisVR repos (UnityJigglePhysics,
OpusSharp, RNNoise.Net, steam-audio, audiolink) — those are "update existing" jobs, later.

**Cleanup freebies:** ✅ done 2026-07-07 — removed empty `Packages/org.basisvr.zstdsharp/`, empty
`Packages/com.basis.profilerintegration/` (misspelled dup; kept the active `...intergration`), the
`Packages/Basis Server Export/` build artifact, the 19 MB dead `com.valvesoftware.unity.openvr-1.2.1.tgz`,
and the 5 `org.basisvr.*` `.tgz` tarballs (see the 2026-07-07 session below).

## Deferred monorepo changes (batch into one Unity pass)

Removals (regenerate `Basis Localization.asset` — localized):
- com.basis.developer.recorder (BasisAvatarRecorder) — published 2026-07-05
- com.basis.developer.exceptions (BasisExceptionReporting) — published 2026-07-05
- com.basis.provider.servers (BasisServersProvider) — published 2026-07-05

Keep-swaps (add git dep to `Packages/manifest.json` + remove embedded folder; Unity-verify resolve+build):
- com.basis.vehicles (BasisVehicles) — published 2026-07-05 · no localization
- com.basis.examples (BasisExamples) — published 2026-07-05 · no localization (stale repo overwritten)

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
- **BasisExamples** (com.basis.examples) — 🔄 published: current content force-pushed over the stale 2025 repo; listed. keep-swap deferred to Unity (no localization).
- **BasisMediaPlayer** (com.basis.mediaplayer) — 🔄 published (own README/LICENSE + native codec libs); listed. Grouping decided: 3 separate repos. Removal blocked by shim (depends on mediaplayer).
- **BasisYtDlpIntegration** (com.basis.integration.ytdlp) — 🔄 published, listed. Removal TBD (leaf, no localization).
- **BasisAudioLinkIntegration** (com.basis.integration.audiolink) — 🔄 published, listed. Removal TBD (leaf, no localization).
- **BasisPoolTable** (com.basis.pooltable) — ✅ DONE: re-homed from dooly123/MS-BASISSA-Billiards to BasisVR/BasisPoolTable (curated); listing repointed; removed from monorepo. No localization.
- **BasisOpenXR** (com.basis.openxr) — 🔄 published (own LICENSE + THIRD_PARTY_NOTICES), listed. Platform VR → keep; keep-swap deferred to Unity. No localization.
- **ytdlp + audiolink removals** — ✅ DONE: both removed from monorepo (clean leaves, no localization; verified the ContentPolice `AudioLink.*` refs are the vendored llealloo package, not these).
- **BasisVisualTrackers** (com.basis.visualtrackers) — 🔄 published: existing repo force-overwritten with the monorepo fallback-only copy (per user; 61 MB of old tracker models discarded). keep-swap deferred.
- **BasisOpenVR + BasisSteamVR + BasisOpenVRPlugin** (com.basis.openvr, com.steam.steamvr, com.valvesoftware.unity.openvr) — 🔄 published as 3 repos: the openvr integration + the two Valve/BSD-3 vendored deps it needs. All keep (platform VR); keep-swaps deferred to Unity.

## Session 2026-07-07 — vendored NuGet deps → git, cleanup, setup package

**org.basisvr.* NuGet-wrapper libs → individual BasisVR git repos (disposition: keep).** These were the
last `file:` local mounts — repackaged NuGet DLLs. Now resolved from git instead of vendored tarballs.
Built GUID-preserving from the embedded folders (DLL bytes + `.meta` GUIDs byte-identical to what Unity
resolved, so framework references are unchanged), each with README + `.gitattributes` (`*.dll binary`).
They are `keep` framework infrastructure, **not** user-installable features → **intentionally NOT listed**
on basisvr.org/packages or in the BasisPM catalog.

| pkg id | repo | ver |
|---|---|---|
| org.basisvr.base128 | [BasisBase128](https://github.com/BasisVR/BasisBase128) | 1.2.2 |
| org.basisvr.bouncycastle | [BasisBouncyCastle](https://github.com/BasisVR/BasisBouncyCastle) | 2.5.0 |
| org.basisvr.generator.equals | [BasisGeneratorEquals](https://github.com/BasisVR/BasisGeneratorEquals) | 3.2.0 |
| org.basisvr.k4os.compression.lz4 | [BasisK4osCompressionLZ4](https://github.com/BasisVR/BasisK4osCompressionLZ4) | 1.3.8 |
| org.basisvr.newtonsoft.json | [BasisNewtonsoftJson](https://github.com/BasisVR/BasisNewtonsoftJson) | 13.0.3 |
| org.basisvr.simplebase | [BasisSimpleBase](https://github.com/BasisVR/BasisSimpleBase) | 4.0.2 |

Manifest now points each at `https://github.com/BasisVR/<Repo>.git`; the 5 loose `.tgz` + 6 embedded
folders removed; `packages-lock.json` file:/embedded entries dropped (Unity regenerates git entries on
open). `k4os.compression.lz4` was an *implicit embedded* package (not previously in the manifest) — now an
explicit git dep. Commit: `Swap org.basisvr deps for BasisVR git dependencies`.

**com.basis.setup** committed (`Add com.basis.setup package`) — Assets-config generators; static-verified,
**not yet Unity-compiled**.

**Only remaining `file:` mount:** `com.github.homuler.mediapipe`. Its manifest entry
(`file:com.github.homuler.mediapipe-0.16.3.tgz`) is **stale** — no such tarball exists; Unity resolves the
**embedded 389 MB folder** (968 files, native libs up to 47 MB). Safe cleanup available: drop the dead
manifest line (embedded folder auto-resolves). Converting the 389 MB native plugin to a git dep is
technically possible (all files < GitHub's 100 MB limit) but heavy and of dubious benefit vs staying
embedded like the other vendored native libs.

### mediapipe / shim / comms — the remaining knot (blocked on decisions, not code)

- **mediaplayer opt-in is blocked by shim.** `BasisShims.asmdef` hard-references `BasisMediaPlayer`
  (`VideoPlayerShim.cs` + `CilboxSceneBasis.cs` expose video playback to CILBOX sandbox worlds).
  mediaplayer is presently a `keep` git dep (shipped). To make it truly opt-in, gate the VideoPlayer shim
  behind a `versionDefine` (`#if BASIS_HAS_MEDIAPLAYER`) and drop the hard asmdef ref. **Decision:** keep
  mediaplayer shipped, or do the versionDefine refactor.
- **mediapipe extraction is blocked by two deps:** `com.github.homuler.mediapipe` (389 MB embedded, no git
  home) and `dev.hai-vr.basis.comms` (**Haï~-owned**; **circular** with shim — comms vpmDeps shim, shim
  vpmDeps comms). Policy: don't fork Haï~'s packages. **Decision:** coordinate with Haï~ to publish comms
  standalone (breaks the circular knot), or leave mediapipe embedded.
- **shim** stays embedded — it's the CILBOX sandbox bridge, hard-wired to framework + comms + cilbox +
  mediaplayer; not a clean leaf.

## Session 2026-07-08 — full modularization: all remaining embedded packages → git

Per user direction ("move all of these to repos, create and then link" + "setup dependencies"), moved the
remaining **24 embedded packages** to BasisVR git deps (excluding the 3 Haï~-owned packages — kept embedded
per the don't-fork rule; shim/mediapipe still resolve against them). The manifest now has **49 git deps and
0 `file:` local mounts**. `Packages/` is a thin shell — only `com.unity.xr.openxr` + the 3 `dev.hai-vr.*`
folders remain embedded.

Built GUID- and binary-preserving from each embedded folder's exact content (Unity `.gitattributes`; native
libs + DLLs md5-verified byte-identical committed vs source):
- **Basis core (14):** BasisFramework, BasisFrameworkEditor, BasisSDK, BasisCommon, BasisServer,
  BasisEventDriver, BasisGizmos\*, BasisBundleManagement, BasisSettings, BasisOpenLipSync,
  BasisProfilerIntegration, BasisMediaPipe, BasisShims, BasisSetup.
- **Vendored (7):** OpusSharp\*, UnityJigglePhysics\*, steam-audio\*, RNNoise.Net\*, BasisAudioLink,
  BasisMeaModDns, BasisTextMeshPro. (\* = overwrote an existing repo per overwrite-on-divergence.)
- **Giant (3):** BasisMediaPipePlugin (homuler, 389 MB), BasisURP (modified URP), BasisThirdPersonDemo.

Native UPM ignores `vpmDependencies` and has no transitive git deps → every package is listed explicitly in
the manifest. Dependency-closure check: all 29 distinct vpmDeps satisfiable (manifest / embedded / registry),
no orphans. Overwritten repos had default branch set to `main` + stale branches pruned. Commit:
`Move remaining embedded packages to BasisVR git dependencies` (Packages/ only).

**NOT build-verified** — needs a Unity open to resolve the git deps (big-bang; user accepted). A large
uncommitted `Basis/Assets/` churn (~170 M / 21 D / 12 new: Addressables groups, URP/quality/XR settings) from
the Unity + com.basis.setup session is left for the user — not part of this package move.

**Haï~ packages + final cleanup (2026-07-08):** the 3 `dev.hai-vr.*` packages live in the `hai-vr/Basis`
monorepo (not standalone repos); Basis's `comms` is heavily modified (93/218 files differ from upstream),
`ndmf` ~upstream, `license-review` has no upstream. Per user ("fork the originals"), **forked `hai-vr/Basis`
→ `BasisVR/hai-vr-Basis`** (real GitHub fork, lineage preserved) and pushed Basis's modified content to a
`basis` branch (partial+sparse clone since the fork is 1.3 GB; PNG icons md5-verified). Manifest references
all 3 via `?path=Basis/Packages/dev.hai-vr.basis.X#basis`. `com.unity.xr.openxr` was cruft (no package.json)
→ removed (Unity uses the registry version). **`Packages/` is now empty — 52 git deps, 0 `file:` mounts, 0
embedded folders.** Closure re-checked: all 29 vpmDeps resolve from the manifest. Commit:
`Move HVR packages to BasisVR hai-vr/Basis fork`.
