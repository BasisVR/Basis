# Basis changes to URP

This package is a **fork of Unity's Universal Render Pipeline**, not the stock package. It is its own
git repo (`https://github.com/BasisVR/BasisURP.git`) embedded in the Basis repo at
`Basis/Packages/com.unity.render-pipelines.universal`. Both repos see the same working tree, so an edit
here shows up as uncommitted in both.

Current base: **URP 17.6.0** (Unity 6000.6.0f1).

This file is the record of what Basis changed and why. Keep it current when you touch the fork, because
it is the checklist you re-apply against every time URP is re-based.

## Delta at a glance

16 modified files, 1 new source file, 0 upstream deletions.

| Area | Files |
|---|---|
| Variable rate shading injection | `Runtime/UniversalShadingRateData.cs` (new), `DepthOnlyPass`, `DepthNormalOnlyPass`, `DrawObjectsPass` (x2), `DrawSkyboxPass` |
| Depth priming under MSAA | `UniversalRendererRenderGraph`, `DrawObjectsPass` |
| Mirror reflection cameras | `UniversalCameraData`, `UniversalAdditionalCameraData`, `UniversalRenderPipeline`, `DrawSkyboxPass` |
| Traced reflections and specular occlusion | `ShaderLibrary/GlobalIllumination.hlsl` |
| Profiling sampler access | `URPProfilingSamplers`, `MainLightShadowCasterPass`, `AdditionalLightsShadowCasterPass` |
| URP detection for build processing | `Editor/BuildProcessors/URPProcessScene.cs` |
| XR orthographic warning removed | `URPProcessScene`, `UniversalRenderPipelineCameraUI.Drawers` |
| Asset tweaks | `Runtime/Materials/Lit.mat`, `Editor/SceneTemplates/Indoors.scenetemplate` |
| Repo hygiene | `.gitattributes`, this file (plus the `.meta` Unity generates for it) |

## 1. Variable rate shading injection

`Runtime/UniversalShadingRateData.cs` is a new `ContextItem` carrying a per-frame VRS image produced by
`BasisVariableRateShadingFeature`. Five passes read it and, when it is valid, attach it with
`ShadingRateCombiner.Override`:

- `DepthOnlyPass`, `DepthNormalOnlyPass` (so depth priming stays consistent for cutout and
  per-fragment-depth shaders)
- `DrawObjectsPass`, in both the plain and rendering-layers paths
- `DrawSkyboxPass` (the peripheral skybox shades at the coarse rate too)

Every injection site is wrapped in `#if !UNITY_ANDROID` and additionally skipped when
`cameraData.xr.enabled && cameraData.xr.supportsFoveatedRendering`, so native XR hardware foveation is
never overridden.

## 2. Depth priming under MSAA

Stock URP refuses to prime depth when MSAA is on. `UniversalRendererRenderGraph` comments out that gate
and forces `bool isNotMSAA = true`. The upstream reasoning was that Unity did not want to deal with
MSAA buffer artifacts, not that it cannot work
([forum thread](https://discussions.unity.com/t/depth-priming-msaa-and-shader-artifacts/1560911/2)).

`DrawObjectsPass` then has to split the two cases, via `MustKeepDepthWritable(cameraData)`
(`msaaSamples > 1`):

- **MSAA on:** keep a plain LEqual + ZWrite pass and take only the early-Z benefit of an already
  populated depth buffer. The prepass and the opaque pass do not agree per sample, so the primed depth
  cannot be re-tested with `CompareFunction.Equal`. That disagreement is the artifact Unity cited.
- **MSAA off:** restore the ZWrite-off + `CompareFunction.Equal` path and let depth be
  `AccessFlags.Read`. Opaque overdraw stops being shaded and render graph can treat depth as read-only.

This matters most on render targets that clamp to a single sample regardless of the quality setting,
where the scene is drawn a second time: mirrors, the handheld camera, headless.

## 3. Mirror reflection cameras

`isMirrorReflectionCamera` is added to `UniversalCameraData` (internal) and
`UniversalAdditionalCameraData` (`[NonSerialized] public`), and propagated in
`UniversalRenderPipeline.InitializeStackedCameraData`.

`DrawSkyboxPass` consumes it. Mirrors render with a reflected view and an **oblique** projection for
scene geometry, but an infinitely distant skybox must not receive the oblique clip, and the native
camera-only skybox path does not preserve the setup. So for mirror cameras the skybox renderer list is
built from the clean pre-oblique projection, which Basis mirrors stash in
`camera.nonJitteredProjectionMatrix`, plus `cameraData.GetViewMatrix(0)`.

## 4. Traced reflections and specular occlusion

All in `ShaderLibrary/GlobalIllumination.hlsl`, affecting both `GlobalIllumination` overloads.

**Traced reflections.** `_BasisGISpecularTexture` and `_BasisGISpecularParams` are declared
unconditionally, as a uniform branch rather than a keyword, so a scene running neither feature pays one
dead branch and gains no shader variant. `BasisSampleTracedReflection` blends the trace over whatever
irradiance the shader already resolved, weighted by roughness (`params.y` is the reciprocal of
`specularMaxRoughness`, making the blend a multiply) and by `traced.a`, the trace's own confidence.
Transparent surfaces bail out, because the buffer was built from opaque depth. Published by
`com.basis.globalillumination` and `com.basis.rtao`.

**Specular occlusion.** Stock URP multiplies the whole result by `occlusion` at the end.
The fork instead applies `occlusion` to `indirectDiffuse` directly and feeds a proper
`GetSpecularOcclusion(NoV, ao, roughness)` term into `GlossyEnvironmentReflection`. That is Lagarde's
Frostbite approximation, the same one HDRP uses. A flat `reflection * ao` multiply can never produce
the below-ao values a mirror lobe reads at grazing angles. `_AmbientOcclusionParam.y` lerps between the
old behaviour and the new one (`BasisRTAOSettings.specularOcclusionRelief`) and defaults to 1, which is
a no-op on unoccluded surfaces whether or not RTAO runs at all.

The AO debug view now returns `half3(1,1,1) * occlusion` early, before the clear-coat blend, so a coat
reflection cannot pollute it.

## 5. Profiling sampler access

URP 17.6 cut the `URPProfileId` enum to three members and marked it `[Obsolete]`. Passes now scope on
fields of `internal static class URPProfilingSamplers`, so `ProfilingSampler.Get(URPProfileId.X)`
returns an instance nothing records into. The fork makes `URPProfilingSamplers` **public** so
`com.basis.framework`'s performance bar can read the samplers the passes actually use.

`MainLightShadowCasterPass` and `AdditionalLightsShadowCasterPass` additionally hold their sampler in a
`static readonly` field and expose `GpuMs` and `SetProfilingEnabled`, since the shadow passes are
constructed per renderer and the bar needs one stable handle.

## 6. URP detection for build processing

`URPProcessScene` replaces `URPBuildData.instance.buildingPlayerForUniversalRenderPipeline` with an
explicit check: URP is considered in use if `GraphicsSettings.defaultRenderPipeline` is a
`UniversalRenderPipelineAsset` **or** any quality level's pipeline asset is one. Basis assigns URP
assets per quality level rather than only globally, and the stock check misses that.

## 7. XR orthographic warning removed

The "Orthographic projection is not supported in XR" warning is stripped from both `URPProcessScene`
(build-time log) and `UniversalRenderPipelineCameraUI.Drawers` (inspector help box). Basis uses
orthographic XR-enabled cameras deliberately.

## 8. Asset tweaks

- `Runtime/Materials/Lit.mat`: base colour white instead of grey, smoothness 0 instead of 0.5, plus
  `_AddPrecomputedVelocity` and `_XRMotionVectorsPass` entries.
- `Editor/SceneTemplates/Indoors.scenetemplate`: swapped one dependency reference.

## Re-basing onto a new URP version

Pristine URP ships **inside the editor** from Unity 6.5 onward, not in the UPM cache:

```
<editor>/Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.render-pipelines.universal
```

Keep the old editor installed and you get a free three-way merge base:

- **BASE** = pristine URP from the **old** editor
- **OURS** = this working tree (exclude `.git`)
- **THEIRS** = pristine URP from the **new** editor

Editor copies are **LF**; this working tree is **CRLF** (`* text=auto`, `core.autocrlf=true`).
Normalize everything to LF for the merge, then write CRLF back. Empty directories left by upstream
deletions have to be removed by hand.

**The fork drifts silently.** Before the 17.5.0 to 17.6.0 update it differed from stock in 78 files, but
only about 20 were real Basis work. The rest was 17.4-era code that earlier partial resyncs never
carried forward. Tell drift from intent with `git log -- <file>` in the parent repo: a file whose only
commits are bulk import or resync hashes is drift and should take upstream; a file with a targeted
commit is Basis work.

### Verifying the delta

Compare the fork against the pristine copy with line endings normalized. Anything that turns up outside
the table above is either drift or a change nobody documented, and both are worth chasing:

Save as `urpdiff.py` and run `python urpdiff.py <pristine> <fork>`:

```python
import os, sys, hashlib
P, F = sys.argv[1], sys.argv[2]
SKIP = {'.git', 'Documentation~', 'node_modules'}
CR, LF = chr(13).encode(), chr(10).encode()

def walk(root):
    out = {}
    for dp, dn, fn in os.walk(root):
        dn[:] = [d for d in dn if d not in SKIP]
        for f in fn:
            full = os.path.join(dp, f)
            rel = os.path.relpath(full, root).replace(os.sep, '/')
            try:
                d = open(full, 'rb').read().replace(CR + LF, LF)
            except Exception:
                continue
            out[rel] = hashlib.md5(d).hexdigest()
    return out

a, b = walk(P), walk(F)
for label, keys in (('MODIFIED', sorted(k for k in set(a) & set(b) if a[k] != b[k])),
                    ('ADDED BY FORK', sorted(set(b) - set(a))),
                    ('DELETED FROM UPSTREAM', sorted(set(a) - set(b)))):
    print('=== %s (%d) ===' % (label, len(keys)))
    for k in keys:
        print('  ' + k)
```

Then read any single file's change with line endings normalized:

```bash
diff <(tr -d '\r' < "$P/Runtime/Passes/DrawObjectsPass.cs") \
     <(tr -d '\r' < "$F/Runtime/Passes/DrawObjectsPass.cs")
```
