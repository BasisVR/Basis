# Upgrading Basis to a new Unity version

Basis tracks the newest Unity 6.7 beta, and every bump touches four things: the editor version, the package manifest, the embedded URP fork, and whatever Basis code leans on APIs that moved. Most of the work is in the URP fork. The rest is bookkeeping, but the bookkeeping is where upgrades usually break.

The steps below are the ones used for every 6000.7.0 beta from b3 to b9. Paths are relative to the repository root unless they start with `<editor>`, which means an installed editor folder such as `C:/Program Files/Unity/Hub/Editor/6000.7.0b9/Editor`.

## Before you start

Keep the editor you are upgrading from installed. Its copy of URP is the merge base for the fork, and you can't do a clean three-way merge without it. If it has already been removed, the Linux editor tarball from `download.unity3d.com` carries the same `BuiltInPackages` folder; stream it through `tar -xJf - --wildcards '*BuiltInPackages/com.unity.render-pipelines.universal/*'` to avoid unpacking 4 GB.

Commit or branch any work in progress first. An upgrade rewrites a few thousand files under `Basis/Packages/com.unity.render-pipelines.universal`, and untangling that from unrelated edits afterwards is painful.

Install the new editor with the modules the builds need: Windows (Mono and IL2CPP), Linux IL2CPP for the server, Android, and WebGL for the web client. Hub doesn't always offer the same modules for every beta, so check WebGL is really there.

## 1. See what Unity actually changed

Beta releases rarely change version numbers. URP, Core and Shader Graph have reported `17.7.0` since b2, while their contents changed in nearly every beta. Compare the checksums instead:

```sh
OLD="<old editor>/Data/Resources/PackageManager/BuiltInPackages"
NEW="<new editor>/Data/Resources/PackageManager/BuiltInPackages"
for f in "$NEW"/*.sha1; do
  n=$(basename "$f")
  [ "$(cat "$f")" != "$(cat "$OLD/$n" 2>/dev/null)" ] && echo "changed ${n%.sha1}"
done
```

`diff -rq` on any package listed there shows which files moved. Pay attention to `com.unity.render-pipelines.core`, since the fork compiles against it and upstream sometimes moves things from URP into Core (see the traps section).

## 2. Packages

### Editor minimum versions

Opening a project in a newer editor silently raises every package below the editor's `minimumVersion`. Those minimums live in `<editor>/Data/Resources/PackageManager/Editor/manifest.json`. Diff the old and new copies and apply any bump that affects a package in `Basis/Packages/manifest.json` or in its dependency tree (`packages-lock.json`). Doing it by hand keeps the lock readable and avoids a surprise diff on first open.

### Registry and git packages

Check every registry package in the lock against the registry:

```sh
cd Basis
python - <<'EOF'
import json, urllib.request
lock = json.load(open('Packages/packages-lock.json'))['dependencies']
for name, entry in sorted(lock.items()):
    if entry.get('source') != 'registry':
        continue
    info = json.load(urllib.request.urlopen('https://packages.unity.com/' + name))
    latest = info['dist-tags'].get('latest')
    if latest != entry['version']:
        print(name, entry['version'], '->', latest, info['time'].get(latest))
EOF
```

When you bump a package, edit both `manifest.json` and the matching `packages-lock.json` entry (`version`, plus `dependencies` if the new release changed them; the registry JSON has the new list). Unity accepts a hand-edited lock as long as it is consistent.

Some things to watch for:

- `latest` is not always stable. glTFast tagged `7.0.0-exp.1` as latest for a while; Basis stays on the 6.x line until 7 leaves experimental.
- Packages that only appear deeper in the lock (for example `com.unity.services.core` under Cloud Diagnostics) follow whatever their parent asks for. Bump them by bumping the parent, not by adding them to the manifest.
- Git packages are pinned to a commit hash. Compare the pin against the remote with `git ls-remote <url> HEAD`, read the upstream changelog, then update the hash in both the manifest URL and the lock (`version` and `hash`).
- Embedded packages (anything that is a folder in `Basis/Packages`) record `file:<folder>` as their lock version. Don't copy that into another package's dependency list.
- The `com.basis.*` packages declare Unity dependencies in their own `package.json`. Keep them in line with what the lock resolves so the packages still install outside this project.

## 3. Rebase the URP fork

`Basis/Packages/com.unity.render-pipelines.universal` is stock URP with Basis changes on top. It is tracked directly by this repository, so there is no submodule to update. From Unity 6.5 onwards the editor ships a pristine copy of URP in `BuiltInPackages`, so you have all three sides of a merge on disk:

| Side | Source |
|---|---|
| base | `<old editor>/Data/Resources/PackageManager/BuiltInPackages/com.unity.render-pipelines.universal` |
| ours | `Basis/Packages/com.unity.render-pipelines.universal` |
| theirs | `<new editor>/Data/Resources/PackageManager/BuiltInPackages/com.unity.render-pipelines.universal` |

Each file falls into one of four cases. If ours already equals theirs, nothing to do. If ours still equals base, Basis never touched it, so take theirs (this also covers files upstream added or deleted). If theirs equals base, upstream didn't touch it, so keep ours. Anything else gets a real three-way merge with `git merge-file`.

Line endings matter. The editor copies are LF and the working tree is mostly CRLF (`core.autocrlf=true`), with some LF files mixed in. Compare with endings normalised, and write each file back in the ending it already had. New upstream files can stay LF.

Save this as `urp_rebase.py` somewhere outside the repository and run it without `--apply` first to see the plan:

```python
import os, sys, subprocess, tempfile, shutil

base, ours, theirs = sys.argv[1:4]
apply = '--apply' in sys.argv

def files(root):
    found = set()
    for path, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d != '.git']
        for name in names:
            found.add(os.path.relpath(os.path.join(path, name), root).replace(os.sep, '/'))
    return found

def read(root, rel):
    full = os.path.join(root, rel)
    return open(full, 'rb').read() if os.path.exists(full) else None

def lf(data):
    return None if data is None else data.replace(b'\r\n', b'\n')

counts, conflicts = {}, []
for rel in sorted(files(base) | files(ours) | files(theirs)):
    b, o, t = read(base, rel), read(ours, rel), read(theirs, rel)
    nb, no, nt = lf(b), lf(o), lf(t)
    if no == nt: action = 'same'
    elif no == nb: action = 'take'
    elif nt == nb: action = 'keep'
    else: action = 'merge'
    counts[action] = counts.get(action, 0) + 1
    if action in ('take', 'merge'):
        print(action, rel, 'ADDED' if b is None and t is not None else 'DELETED' if t is None else '')
    if not apply or action in ('same', 'keep'):
        continue
    target = os.path.join(ours, rel)
    crlf = o is not None and b'\r\n' in o
    if action == 'take':
        if t is None:
            os.remove(target)
            continue
        os.makedirs(os.path.dirname(target), exist_ok=True)
        open(target, 'wb').write(nt.replace(b'\n', b'\r\n') if crlf else t)
        continue
    if None in (nb, no, nt):
        conflicts.append(rel + ' (added or deleted on one side)')
        continue
    scratch = tempfile.mkdtemp()
    paths = []
    for name, content in (('ours', no), ('base', nb), ('theirs', nt)):
        path = os.path.join(scratch, name)
        open(path, 'wb').write(content)
        paths.append(path)
    result = subprocess.run(['git', 'merge-file', '-p', *paths], capture_output=True)
    open(target, 'wb').write(result.stdout.replace(b'\n', b'\r\n') if crlf else result.stdout)
    if result.returncode != 0:
        conflicts.append(rel)
    shutil.rmtree(scratch)

print(counts)
print('conflicts:', conflicts)
```

```sh
python urp_rebase.py "$BASE" "Basis/Packages/com.unity.render-pipelines.universal" "$THEIRS"
python urp_rebase.py "$BASE" "Basis/Packages/com.unity.render-pipelines.universal" "$THEIRS" --apply
```

Conflicts are left with standard `<<<<<<<` markers. Resolve them by keeping the Basis intent on top of the new upstream code. Upstream deletions can leave empty folders behind; remove those along with their `.meta`.

### Prove nothing was lost

After applying, the Basis delta should be the same set of changed lines as before, just sitting on newer code. For each file Basis changes (the list in "Where Basis differs" below), compare the changed lines of `diff(new pristine, fork now)` with those of `diff(old pristine, fork at HEAD)`. They should match exactly. A file that differs either lost something or had a conflict you resolved. Read those again either way.

Then regenerate the delta itself against the new pristine copy and check it against the list below. Anything new in it is either drift (see the traps) or Basis work that this guide should mention.

### GUID collisions

Check every `.meta` the merge added against the rest of the project and the new editor's builtin packages:

```sh
grep -h '^guid:' $(git ls-files --others --exclude-standard -- '*.meta') | awk '{print $2}' |
while read g; do grep -rl "guid: $g" --include=*.meta Basis/Assets Basis/Packages "$NEW" |
  grep -v com.unity.render-pipelines.universal; done
```

Any hit means two assets share a GUID, and Unity will pick one at random.

## 4. Editor version

Set both lines of `Basis/ProjectSettings/ProjectVersion.txt`. The changeset is in the editor's file version, which you can read from PowerShell:

```powershell
(Get-Item '<new editor>\Unity.exe').VersionInfo.ProductVersion   # 6000.7.0b9_2e2e237e2443
```

```
m_EditorVersion: 6000.7.0b9
m_EditorVersionWithRevision: 6000.7.0b9 (2e2e237e2443)
```

CI reads the version and changeset from this file (`.github/actions/unity-editor-image`) and builds its editor image from them, so nothing else in the repository needs the version.

## 5. First open

Open the project once in batch mode to compile and import:

```sh
"<new editor>/Unity.exe" -batchmode -quit -projectPath Basis -logFile b9_open.log
grep -E "error CS|Scripts have compiler errors" b9_open.log
```

Batch runs can damage settings assets. On this project they have emptied `m_RuntimeSettings.m_List` in `Assets/Basis/Settings/Unity Rendering Defaults/UniversalRenderPipelineGlobalSettings.asset`, nulled `customRenderPipeline` in `GraphicsSettings.asset` and `QualitySettings.asset`, and reset anti-aliasing. Run `git status` afterwards and `git checkout --` any of those that changed. Other diffs from the first open, such as new OpenXR feature entries or Addressables schema fields, are usually Unity migrating data for a new package version and should be kept. Check that enabled flags didn't change.

Open the editor normally afterwards. Some migrations, like OpenXR settings for newly added features, only happen in an interactive session.

## 6. Verify

At the very least:

1. Zero compile errors in the batch log.
2. EditMode tests (`-runTests -testPlatform EditMode`). Restore the settings assets afterwards, as above.
3. Play mode on desktop and in VR, looking at mirrors, GI, upscaling and the performance bar, which are the features that depend on the URP fork.
4. Player builds for Windows, Android and the Linux server.

## Where Basis differs from stock URP

These are the Basis changes to the fork as of 6000.7.0b9. Everything else in the folder should be byte-identical to the editor's copy, line endings aside.

### Variable rate shading

`Runtime/UniversalShadingRateData.cs` (new) is a `ContextItem` that carries a shading rate image from `BasisVariableRateShadingPass` (`com.basis.framework/Rendering/VariableRateShading`) into the forward passes. `DepthOnlyPass`, `DepthNormalOnlyPass`, `DrawObjectsPass` (opaque and rendering-layers paths) and `DrawSkyboxPass` each bind it with `SetShadingRateImageAttachment`. Every block is wrapped in `#if !UNITY_ANDROID` and skipped when XR hardware foveation is active, so native foveated rendering is never overridden.

### Mirrors

`isMirrorReflectionCamera` on `UniversalAdditionalCameraData` (public, not serialised) is copied into `UniversalCameraData` in `UniversalRenderPipeline.InitializeStackedCameraData`. `DrawSkyboxPass` uses it to draw the skybox with the camera's `nonJitteredProjectionMatrix`, because the mirror's oblique near plane must not clip an infinitely distant sky. Set by `BasisMirrorReflectionCamera` and `BasisSDKMirror`, and read by the GI feature.

`Runtime/UniversalRenderPipeline.StereoCameraRequest.cs` (new) adds `StereoCameraRequest`, which renders one camera into a two-slice texture array with single-pass instanced stereo using two caller-supplied views. VR mirrors use it to draw both reflected eyes in one pass. It reaches the internal `XRView` and `XRPass.AddView` in Core through reflection and checks the struct layout first; if Core changed shape, `SupportsStereoCameraRequest` goes false and mirrors fall back to two cameras. After any upgrade where Core's XR code changed, confirm this still reports true in VR. Supporting edits are in `UniversalRenderPipeline.cs` (request routing, `cullFromCamera` culling), `XRPassUniversal.cs` (`cullFromCamera`, skybox projections), `DrawSkyboxPass` (per-eye skybox projections), and `UniversalRenderer.cs` (forces an intermediate target when the request renders with MSAA into a single-sampled array). `Shaders/Utils/Universal2D.hlsl` gained the stereo macros so the `BasisSurface` pass below works in single-pass instanced.

### Global illumination surface output

`ShaderLibrary/BasisSurfaceOutput.hlsl` (new) declares the `_BASIS_SURFACE_MRT` keyword and writes diffuse albedo into an extra render target during the opaque pass, which the GI package uses for bounce colour. It is wired into the forward passes of `Lit`, `ComplexLit`, `SimpleLit`, `BakedLit` and the Shader Graph Lit target (`PBRForwardPass.hlsl`, `UniversalLitSubTarget.cs`). Those shaders also gain a `BasisSurface` pass (LightMode `BasisSurface`) for objects drawn outside the main opaque pass. `UniversalResourceData` holds `basisSurfaceTexture` and `basisSurfaceWritten`, `DrawObjectsPass.BindBasisSurface` attaches the target when its format matches the colour target, and `ShaderScriptableStripper` strips the keyword on Android, iOS, WebGL and visionOS. Consumer: `com.basis.globalillumination` (`BasisGlobalIlluminationPass`, `BasisGlobalIlluminationSurfaceTags`).

### Reflections and specular occlusion

`ShaderLibrary/GlobalIllumination.hlsl` includes the new `BasisWorldReflection.hlsl`, which samples the GI world cascades for reflections, and adds `BasisSampleTracedReflection` for ray traced reflections published by `com.basis.globalillumination`. Both are driven by uniforms rather than keywords, so a scene with GI off pays for one untaken branch and gets no new variants. `GlobalIllumination` also applies occlusion with Lagarde's specular occlusion approximation (`GetSpecularOcclusion`) instead of multiplying the final colour by AO. Diffuse and specular are occluded separately, which is what `com.basis.rtao` expects.

### Upscaling

- `UniversalRenderPipeline.skipUpscaler` hides matching registry entries while Core's `Upscaling` constructor runs (`CreateUpscaling`). Basis uses it to drop FSR3/FSR4 on hardware that can't run them, which silences Core's "not available" warnings. Set in `SMModuleAntialiasingURP`.
- `GetUpscaler` and `GetUpscalerOptions` expose per-upscaler options for the settings menu.
- `GetCameraUpscaler(camera)` replaces the global `upscaling.activeUpscaler` at every call site, so cameras that render to a texture or have post-processing off never get an upscaler.
- `ScriptableRenderer` sets `_GlobalMipBias` to the plain resolution ratio. The upscaler's own `CalculateMipBias` adds another -1, which was measured to make DLSS and FSR2 shimmer.
- `UpscalerPostProcessPass.RecordPerView` runs temporal upscalers (other than STP) once per eye on single-slice copies, because the upscalers in use don't accept texture arrays.
- `Shaders/PostProcessing/ScalingSetup.shader` clamps negative colour before `FastTonemap` under HDR output, which fixes black NaN spots with FSR1.

The `ENABLE_UPSCALER_FRAMEWORK` scripting define is still set for Standalone in Player Settings. Upstream stopped guarding on it in b6. Basis code still checks it, so other platforms take the legacy branch in `SMModuleAntialiasingURP`.

### Render scale

`UniversalRenderPipeline.XRRenderScaleExternallyOwned` stops URP calling `XRSystem.SetRenderScale` every frame. `SMModuleRenderResolutionURP` sets it and drives XR render scale itself. Without it, URP resets the scale and the eye texture flips between sizes, which on the path tracer caused GPU faults.

### Profiling

`URPProfilingSamplers` is public (it is `internal` upstream). URP 17.6 made the old `URPProfileId` enum obsolete, so `BasisPerformanceBarData` reads these samplers directly. `MainLightShadowCasterPass` and `AdditionalLightsShadowCasterPass` keep their sampler in a static field and expose `GpuMs` and `SetProfilingEnabled` for the same bar. `ScriptableRenderPass.profilingSampler` returns the real sampler in any build with `ENABLE_PROFILER`, not only instrumented ones.

### Smaller changes

- `Shaders/DepthNormalsPass.hlsl` includes `Shaders/Utils/NormalMap.hlsl`. Upstream calls `UseNormalMap()` there without including it, which breaks any custom shader using the classic `SurfaceInput.hlsl` + `DepthNormalsPass.hlsl` pattern (the pool table shaders, for one). Drop the line if upstream ever adds the include.
- `DrawObjectsPass` always uses the default depth state (LEqual with ZWrite) instead of switching to Equal with ZWrite off under depth priming.
- `UniversalRendererRenderGraph` has one comment explaining why depth priming needs single-sample targets. No behaviour change.
- `Runtime/Materials/Lit.mat` defaults to white with zero smoothness and has motion vector flags set.
- `.gitattributes` is Basis's own. `Editor/SceneTemplates/Indoors.scenetemplate` and `Textures/2D/Sparkle.png.meta` differ only by importer churn. Taking upstream for them is harmless.

## Other places Basis differs from stock packages

- `com.basis.textmeshpro` holds TextMeshPro's essential resources (shaders, fonts, settings) as a normal package, separate from `com.unity.ugui`. When ugui's TMP shaders change upstream, compare them by hand.
- `com.basis.openxr/RenderModelFeature` is a Basis copy of Valve's old render model sample, feature id `com.valve.openxr.feature.render_models`. Valve OpenXR Utils 1.1.0 ships its own `ValveOpenXRRenderModelFeature` with a different id (`com.valvesoftware.openxr.utils.render_models`), so both can exist. Only enable one; `BasisOpenXRRenderModel` uses the Basis copy.
- Several third-party packages are embedded and edited (AudioLink, Steam Audio, SteamVR, OpenVR, JigglePhysics, volumetric fog, Cilbox, MediaPipe). Updating them is a separate job from a Unity upgrade.

## Traps from earlier upgrades

- b3 moved `UpscalerReactiveMask.shader` from URP to Core, keeping its GUID. Keeping the fork's copy gives a GUID collision. Follow the deletion instead. The GUID check above catches this.
- Earlier partial resyncs left old upstream code mixed in, which looked like Basis changes. `git log -- <file>` tells you which is which: a file whose only commits are version bumps is drift and should take upstream.
- b6 removed every `#if ENABLE_UPSCALER_FRAMEWORK` in Core and URP. The conflicts were Basis lines inside those guards, resolved by keeping the Basis code and dropping the guards.
- Since b7, `XRLayout.AddCamera` rewrites `camera.stereoTargetEye` from the XR toggle every frame, so a Basis camera can't hold a mask that disagrees with `allowXRRendering`.
- Valve OpenXR Utils 1.1.0 moved `ValveOpenXRRefreshRateFeature` into `Valve.OpenXR.Utils`, so `BasisOpenXRRefreshRate.cs` needed a `using`. Read changelogs for git packages before bumping the hash.
- Addressables 4.1.2 gates code on `Unity >= 6000.7.0b8`. An offline compile that treats the editor as plain `6000.7.0` reports false errors.
