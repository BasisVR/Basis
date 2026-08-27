# Basis Ray Traced Ambient Occlusion

Ray traced ambient occlusion for the Universal Render Pipeline, built on Unity's Unified Ray Tracing
backend. It replaces screen space AO with occlusion traced against the real scene geometry, so contact
shadows are correct for surfaces that never appear on screen, and it is stereo aware end to end: a single
dispatch covers both eyes of a single pass instanced VR frame.

The technique follows the structure of [boksajak/RTAO](https://github.com/boksajak/RTAO) — a ray tracing
pass followed by temporal accumulation and spatial filtering — extended for VR, half resolution tracing
and URP's render graph.

## Pipeline

| Pass | Kind | Resolution | Output |
| --- | --- | --- | --- |
| Prepass | Raster, 2 MRT | trace res × views | camera relative world position + octahedral world normal |
| Trace | Ray tracing dispatch | trace res × views | visibility + mean hit distance |
| Temporal | Compute | trace res × views | reprojected and accumulated visibility |
| Denoise (N × H+V) | Compute | trace res × views | à-trous cascade, reach doubling per pass |
| Composite | Raster | full res | `_ScreenSpaceOcclusionTexture` |
| Bind | Raster | — | enables `_SCREEN_SPACE_OCCLUSION`, sets `_AmbientOcclusionParam` |

Because the result is published as `_ScreenSpaceOcclusionTexture` with the standard URP keyword, every URP
lit shader consumes it with no shader change. Nothing needs a GBuffer, so the forward and forward+ paths
both work.

### Depth and normals

The prepass reconstructs world position from `_CameraDepthTexture` and derives the normal from a five tap
depth neighbourhood, picking the less discontinuous neighbour on each axis. It never reads
`_CameraNormalsTexture`, which is what lets it coexist with MSAA depth priming.

At the default divider of 2 the prepass picks the nearest of each 2×2 depth block, so the trace samples sit
on real surfaces rather than on interpolated edges, and the composite bilaterally upsamples by comparing
the full resolution depth against the view depth the trace resolution pixel recorded. Not the distance
between two world positions: a full resolution pixel is laterally offset from its trace resolution parents
by construction, so weighting on the whole offset docks a tap for being where it was always going to be,
and picks the nearest tap rather than the one on the same surface.

The prepass also marks anything past **Fade End** as having no geometry. The composite multiplies that
range by zero, so there is nothing to be gained by tracing it, accumulating it or filtering it — the whole
chain skips those pixels rather than resolving an occlusion that is then thrown away.

## VR

Everything is a `Texture2DArray` sized `volumeDepth = viewCount`, including the monoscopic case, so the
trace and denoise shaders carry one declaration. The ray tracing dispatch uses its `depth` dimension for
the eye index, and the per eye view projection matrices and view planes are uploaded as arrays. History is
per camera and per eye, and it is dropped whenever the view count or resolution changes.

**Stereo coherent noise** is on by default. The random seed is hashed from the world position quantised to
`noiseCellSize` (1 cm by default) rather than from the pixel coordinate, so both eyes reconstructing the
same surface point draw the same ray directions. Independent per eye noise is the thing that reads as
shimmer between the eyes in a headset; this removes it at the source instead of leaning on the denoiser.

The hash sets where in the sample square a pixel starts. **The frame index then walks that start along the
R2 lattice** rather than re-hashing the cell with it. Re-hashing draws an independent offset every frame,
so what the temporal filter averages is a random walk that converges at `1/sqrt(n)`; R2 is low discrepancy
in time, so the same number of accumulated frames covers the square far more evenly and the same ray budget
lands visibly less noise. Both eyes advance by the same amount from the same start, so stereo coherence is
untouched.

## Backends

Hardware ray tracing needs **Direct3D12** on Windows, or Vulkan with the ray tracing extensions. Direct3D11
has no ray tracing path at all, so the package carries a screen space estimator that feeds the same
temporal, spatial and composite chain — one code path for the denoiser, the VR handling and the URP
integration, with only the occlusion estimate changing.

| Occlusion Source | Backend | Sees |
| --- | --- | --- |
| Auto (default) | hardware ray tracing, or screen space where the GPU has none | the real scene, or the depth buffer |
| Ray Traced | hardware ray tracing, otherwise the feature turns itself off | the real scene, including geometry off screen and behind the camera |
| Screen Space | the compute estimator, on any compute capable GPU including Direct3D11 | only what the frame already drew |

The screen space path shares the world hashed seed, so it is stereo coherent for the same reason the traced
path is. ⚠️ It also ignores everything that describes the *structure* rather than the frame: `BasisRTAOExclude`,
the layer mask and the shadow casting filter all select what goes into the acceleration structure, and the
fallback never consults it. Anything still being drawn still occludes there. A fourth mode, `ComputeBvh`, traces the real scene through a software BVH: correct everywhere and
far too slow for a frame budget, so it is an authoring aid rather than a shipping option.

Standalone headsets have no viable path either way, so the integration compiles out on Android.

## Acceleration structure

`BasisRTAOScene` keeps an `IRayTracingAccelStruct` in sync with the scene: it rescans renderers on an
interval (2 s by default, or immediately on `MarkDirty()`), updates transforms for non static renderers, and
only rebuilds when something actually changed. Filtering is layer mask, renderer enabled state, shadow
casting mode, and an explicit `BasisRTAOExclude` component.

⚠️ **An instance can only be removed by handle while the mesh it was registered against is alive**, and the
two backends fail in opposite directions once it is not. The hardware one drops the instance itself the
moment Unity destroys its mesh and hands the handle straight back out, so a late `RemoveInstance` takes
whichever instance inherited it. The compute one copied the geometry into its own BLAS pool, keyed by the
mesh's instance id, and never hears that the mesh died — skip the removal there and the old geometry occludes
for the rest of the session, and the next mesh Unity hands that recycled id to inherits its BLAS. An avatar
bundle unloading out from under a swap is how a registered mesh dies first, so that case escalates to
`ResetStructure()`: clear every instance and re-add the entries that still resolve to geometry.
`StructureResetCount` counts them, and it should stay near zero — a number that climbs per swap means
something is releasing too late.

### Avatars

Avatars are skinned meshes, so they only occlude when skinned mode is on, and they are the reason it now
defaults to Dynamic. Both the local and remote avatars go through the same path:

- Every avatar **install** — local switch, remote switch, fallback, far LOD — calls
  `BasisRTAOFeature.MarkSceneDirty()` from `BasisRTAOIntegration`, off the single funnel all of them go
  through (`BasisAvatarFactory.OnAnyAvatarInstalled`), so the new body is in the structure the same frame
  rather than whenever the rescan interval happens to come round. Unity has already processed the outgoing
  avatar's `Destroy` by the time the pass records, so the forced rescan drops the old one in that same frame.
  ⚠️ **Not off `OnRemotePlayerJoined`/`Left`**, which is what this used to be: those only fire when the
  room's *population* changes, so a remote switching avatars — or dropping to their far LOD on the way out
  of range, which is a whole new mesh — reached neither and kept occluding as the body they had left until
  the rescan came round. `BasisNetworkLifeCycle` also nulls both actions on shutdown, which left this deaf
  to remotes entirely after the first disconnect.
- Every avatar's instance transform follows its own transform every frame, near or far, so a remote never
  occludes from where it used to be standing.
- The **pose** re-bake is what costs, so that is what the occlusion quality actually buys on avatars:

  | Quality | Avatars re-posed per frame | Minimum frames between re-poses |
  | --- | --- | --- |
  | Low | 1 | 8 |
  | Medium | 4 | 4 |
  | High | 16 | 2 |
  | Ultra | 100 | 1 |

  Both halves move together — a budget of 100 buys nothing if every avatar is still rate limited to one
  re-pose every four frames. **Avatars Re-posed Per Frame** on the Developer tab pins the budget against the
  quality level, which is how you measure what a busy instance costs; zero means follow the quality.
- Only avatars within `skinnedMaxDistance` (15 m) spend the budget. Past that an avatar keeps its last pose
  — still occluding, from the right place, just not re-posed. ⚠️ **The first bake is exempt from that gate
  and from the interval.** `AddEntry` snapshots a body the frame it enters the structure, and a freshly
  installed avatar has not been posed yet — it is still standing in the pose its mesh was imported in.
  Apply the gate to that bake and everyone who loads in beyond 15 m occludes as a T-pose for as long as they
  stay out there. `Entry.needsPosedBake` carries the exemption and clears on the first bake that lands.
- ⚠️ **Do not filter avatars on `shadowCastingMode`.** It is an authoring signal on world geometry, but
  `BasisAvatarShadowLOD` writes `ShadowCastingMode.Off` onto every remote renderer past mesh LOD 2 (roughly
  14 m). Treating that as "does not occlude" silently drops most of the room out of the acceleration
  structure. `ShouldInclude` applies the shadow filter to mesh renderers only.

Skinned meshes default to **Dynamic**, because avatars are what people look at and an avatar that casts no
contact shadow reads as floating. Dynamic bakes them on a per frame budget (2 per frame, one every 4 frames
each, within 8 m) and re-adds the instance so the BLAS is rebuilt. That is real CPU and GPU cost per avatar;
`Static` bakes once and `Off` skips them entirely if a world needs the frame time back. Static's "once" is
still the first **posed** bake, not `AddEntry`'s snapshot — `BakeFirstPoses` is the one thing Static does per
frame, and without it every avatar would occlude as its import pose forever.

## Denoising

Two stages, both toggleable through the settings:

1. **Temporal accumulation** — reverse reprojection with depth and normal rejection, blending toward the new
   trace at `1/(frames+1)` down to a floor so lighting changes still land.

   Reprojection walks this frame's world position back through last frame's matrix, so it carries the
   *camera's* motion and nothing else. A floor that stayed still while an avatar walked across it reprojects
   perfectly onto history that still holds the shadow the avatar has since left; depth and normal both
   agree, so neither rejection test sees anything wrong, and the shadow drags for as many frames as the
   accumulation is long. In a room full of moving avatars that is the artifact you actually see.

   **Temporal Variance Gamma** (1.25) bounds the history by what the 3×3 raw neighbourhood is reading now —
   `mean ± gamma·sigma`, with confidence falling by how far the clamp had to move so the blend opens up in
   the same breath. `sigma` never narrows past the spread a visibility estimate genuinely has at this many
   rays, because nine taps of a one ray estimate agree outright often enough to matter and a box measured
   from that alone would have zero width. Zero turns the clamp off.
2. **Spatial à-trous cascade** — `Noise Reduction` picks how many passes run. Each pass is a separable
   edge-aware bilateral whose taps spread twice as far as the last, so the reach doubles per pass at a fixed
   tap count: Off, Standard (1 pass), High (2, the default), Maximum (3). Because the stride widens, the
   depth and normal edge stopping tightens with it, or the later passes would smear occlusion across creases
   the first pass respected.

   Two things bound how wide a given pixel may be filtered and the narrower wins. Accumulated frames say how
   much noise is left to remove. The accumulated **mean hit distance** says how much detail the occlusion
   there has to lose: rays that all struck within a centimetre describe a shadow that varies over a
   centimetre, and filtering it over the reach of one whose rays flew the whole radius is exactly how a
   contact shadow becomes a smudge. The trace has always written that distance; until now nothing read it,
   which is why the sharpest occlusion in the frame was the most heavily filtered part of it.

Fewer rays need more filtering, so the quality presets scale the two together: Low traces 1 ray and denoises
3 passes, Ultra traces 6 and denoises 1.

## Occlusion Applies To

Two ways the resolved occlusion can reach the image, mirroring what Unity's own SSAO offers.

**Lighting** (default) publishes `_ScreenSpaceOcclusionTexture` and lets URP's lighting consume it. Truthful,
and subject to all three gates below.

**Final Image** multiplies the finished opaque frame instead, exactly as URP's SSAO does in its After Opaque
mode — `Blend One SrcAlpha` with the occlusion in alpha, which resolves to `cameraColor *= visibility`. It
lands on **every opaque surface whatever its shader is**, so it reaches Poiyomi avatars and anything else
that never samples the occlusion texture; it ignores material occlusion maps; and it is not subject to the
indirect/direct split. The cost is honesty: it dims direct light and specular that already carry their own
shadowing. Occlusion On Direct Light is hidden in this mode because it no longer means anything.

If the effect is visible on avatars but not on the world, Final Image is almost certainly what you want.

## Why the shaded image shows less than the buffer

This trips people up, so it is worth stating plainly: **the occlusion buffer is not the effect**. URP applies
it in two different strengths, and then clamps one of them against the material.

- `GlobalIllumination(...)` returns `color * occlusion` — ambient, light probes and lightmaps are dimmed by
  the occlusion **in full**.
- `GetMainLight(...)` does `light.color *= lerp(1, occlusion, _AmbientOcclusionParam.w)` — light arriving
  straight from a lamp or the sun is dimmed **only by Occlusion On Direct Light**, 0.25 by default.

So a surface carried by a bright directional light barely moves even where the buffer is nearly black, while
an avatar lit mostly by probes darkens the full amount. That is URP behaving as designed, not the buffer
being wrong. **Occlusion On Direct Light** is the knob that closes the gap; raising it trades physical
correctness for the look the buffer promises.

### And why the sliders can look dead on avatars

`AmbientOcclusion.hlsl:59` then does:

```hlsl
aoFactor.indirectAmbientOcclusion = min(aoFactor.indirectAmbientOcclusion, occlusion);
```

`occlusion` there is the material's own **Occlusion Map**. Avatars almost always ship one; world geometry
usually does not. Wherever the baked map is darker than the traced result, `min` picks the map, and
**Occlusion Strength and Radius change nothing visible** on that pixel no matter how far they are pushed —
the map is already winning. That is also why the same sliders behave normally on plain URP Lit geometry.

`directAmbientOcclusion` is **not** clamped this way, so **Occlusion On Direct Light** still responds on
avatars even when the indirect term is pinned by the map.

## Settings

The Graphics tab carries an **Ambient Occlusion** section: enable, Occlusion Source, Occlusion Quality,
Strength, Radius, Noise Reduction, Occlusion On Direct Light, and whether avatars cast occlusion. **Occlusion Source is hidden
on a GPU that cannot ray trace**, since every entry would resolve to the same fallback. **Show Occlusion
Buffer** lives on the Developer tab with the other debug views. The effect is off by default. `BasisRTAOIntegration` in the framework subscribes to the
settings system, maps the dropdown strings through `BasisRTAOSettingsMap`, and clamps the quality against
the graphics quality tier the way shadows and HDR do, rather than writing the player's dropdown back down.

⚠️ **Occlusion Quality is a performance tier and owns cost only** — ray count, trace resolution, temporal
frames, denoise passes, blur radii, and the avatar re-pose budget. It deliberately does **not** own intensity,
radius, power, direct strength, fade or the biases; those are look, and the authored values survive. An
earlier version replaced the whole settings struct with the preset, which silently discarded everything
authored on the feature — if a value you drag in the inspector appears to do nothing, that is the shape of
bug to look for. **Override Quality Preset** hands the cost knobs over too. Runtime code can steer the feature
without touching the renderer asset:

```csharp
BasisRTAOFeature.RuntimeEnabled = true;
BasisRTAOFeature.HasQualityOverride = true;
BasisRTAOFeature.QualityOverride = BasisRTAOQuality.High;
BasisRTAOFeature.CameraFilter = camera => ReferenceEquals(camera, BasisLocalCameraDriver.CameraInstance);
```

## Cameras

Mirrors and the handheld camera are separate `Game` cameras, and each one records its own prepass, trace,
denoise and composite. That is deliberate: a mirror showing the room without its contact shadows reads as a
different room, and a photo that does not match the view is worse still. **Occlusion In Mirrors And Camera**
turns it off when the frame gets tight, and the player's own view is never dropped either way.

What is shared and what is not:

| Per frame, once | Per camera |
| --- | --- |
| renderer rescan, transform sweep, avatar re-bakes, acceleration structure build | prepass, trace, temporal, denoise, composite, and its own history |

`BasisRTAOScene.Refresh` carries the once-per-frame guard itself rather than the pass, so `MarkDirty` can
punch through it — keeping it in the pass keyed on `Time.frameCount` would swallow every dirty in edit mode,
where the frame counter never advances. The avatar bake budget spends itself around
`BasisRTAOFeature.ViewerPosition` (the player), not around whichever camera happens to be recording.

History is keyed per camera, so each mirror carries its own accumulation buffers at its own resolution.
That is the memory cost of the setting.

### Renderers

The feature ships on both `DesktopRenderer.asset` and `DirectToScreenRenderer.asset`. Both start with
**Active unchecked** — it costs real frame time and the traced path needs a ray tracing GPU.
`Packages/com.basis.framework/Rendering/BasisRTAOIntegration.cs` installs the camera filter and the settings
bridge.

## Enabling it

Tick **Active** on the `BasisRTAOFeature` block of `DesktopRenderer.asset` (and `DirectToScreenRenderer.asset`
for the stream output), in the renderer inspector or by setting `m_Active: 1` in the asset.

Turn on **Show Occlusion Buffer** (Developer tab) to draw the resolved occlusion over the frame instead of
reading it through lighting. Expect it to look stronger than the shaded result — see the section above.

## Tests

`Basis.RTAO.Tests` covers settings validation, renderer filtering, per camera history and ping pong, the
resolution and stereo plumbing, and shader compilation. Beyond that it runs real GPU work:

- `BasisRTAOCommonHlslTests` dispatches the shipped `BasisRTAOCommon.hlsl` and checks the octahedral round
  trip, that the hemisphere sampler is cosine weighted (mean `cos θ` of 2/3, not a uniform 1/2), that the
  Hammersley sequence is stratified, that two positions inside one noise cell produce the same offset —
  the property stereo coherence rests on — and that sixteen consecutive frames of one pixel keep a minimum
  pairwise separation white noise does not reach, which is what stops a re-hash quietly replacing the R2
  advance.
- `BasisRTAODenoiseTests` covers the accumulation arithmetic with the variance clamp off, then the clamp on
  its own: that it pulls back history the frame disagrees with, that it leaves agreeing history untouched,
  and that the sigma floor keeps the box open when nine taps agree by luck. It also checks the blur narrows
  both as history matures and where the rays struck close.
- `BasisRTAOTraceTests` builds an acceleration structure, traces the real ray generation shader with
  `depth = 2`, and checks that a ceiling occludes the ground under it, that a flat surface does not self
  occlude, and that the two array slices are traced from their own inputs.
- `BasisRTAOFallbackTests` checks the backend resolution table — Auto lands on screen space without ray
  tracing, Ray Traced refuses to fall back, nothing survives a device with no compute — then dispatches the
  screen space kernel to check a flat plane stays open, a raised ledge darkens the surface beside it, a
  radius too short to reach that ledge sees nothing, and both eyes agree.
- `BasisRTAOEndToEndTests` stands up a URP asset with the feature, renders a box on a plane, and checks the
  contact region reads darker than open ground, that removing the blocker restores it, and that the camera
  filter and runtime toggle both stop the effect.
- `BasisRTAOLocalizationTests` parses the settings panel source and en.json, and fails if the panel asks for
  a string that does not exist, if a dropdown option has no text, if a binding key carries uppercase the
  settings system would never match, or if a binding is missing from `LoadAll`.

GPU tests skip themselves with a clear message when no ray tracing backend is present.
