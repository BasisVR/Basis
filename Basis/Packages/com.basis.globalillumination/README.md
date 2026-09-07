# Basis Global Illumination

Real-time global illumination for the Universal Render Pipeline, replacing the
`com.jiaozi158.unityssgiurp` integration Basis shipped previously.

Two modes share one denoise and composite chain, chosen by **Mode**:

| | Screen Space | Ray Traced |
| --- | --- | --- |
| Gathers | the colour buffer along a depth-buffer march | the scene itself, through a ray tracing acceleration structure |
| Sees | only what the camera drew this frame | everything, including behind the camera and outside the frustum |
| Radiance at a hit | whatever the frame already shaded there | material albedo relit by the real lights, plus its emission |
| Needs | nothing but a depth buffer | a ray tracing backend (falls back to Screen Space without one) |

## How Screen Space works

For every pixel of the traced buffer:

1. A world position and a normal are recovered from the **depth buffer**. The normal is reconstructed
   from depth by default, so nothing is required of the surface's shader beyond writing depth.
2. One or more cosine-weighted rays are marched **through the depth buffer** in screen space, coarse to
   fine - see [The march](#the-march) below.
3. Where a ray hits a surface, the **camera colour at that point is gathered as incoming radiance**.
   Because the colour sampled is the previous frame's, bounces compound over frames.
4. Rays that hit nothing fall back to the reflection probe or the sky.
5. Short hits accumulate **near-field obscurance**, the effect's own ambient occlusion term.
6. Registered **emitters** inject light analytically, for sources too small to survive at traced
   resolution or that are off screen entirely.
7. The result goes through the denoiser below, then is bilaterally upsampled and composited.

## Which cameras it runs on

The player's own camera always. Beyond that:

- **Mirrors** render it by default (`Mirrors` on the feature, `globalilluminationmirrors` as a setting).
  A mirror is recognised by `UniversalAdditionalCameraData.isMirrorReflectionCamera`, not by camera type -
  `CameraType.Reflection` means a reflection *probe* capture, which has its own separate toggle. Mirrors are
  deliberately **exempt from the Render Post Processing requirement**: they ship with that off, and this
  effect is not part of the post stack - it composites before transparents off the depth buffer. Gating on
  it left a mirror rendering the room with no bounce light beside a direct view of the same room with it,
  and nothing connected the two settings. Each mirror camera pays for its own gather and keeps its own
  temporal history, so a world with a large mirror pays roughly twice; that setting is the lever.
- **The handheld camera** registers itself, and a still capture forces Full resolution with the temporal
  filter off. The live preview stays at the player's own resolution with accumulation running, so a photo
  will not match the preview pixel for pixel - that is deliberate, not a defect. Two things about it are
  not like the player's own camera and both had to be answered for:
  - It **does not render every frame**. The panel offers a render rate limit, and the render gate stops it
    outright whenever nothing is showing the feed. The accumulation window between one render and the next
    is therefore counted in the camera's OWN renders rather than in application frames, or a camera capped
    to 30Hz on a 90Hz headset would discard its temporal filter on every single render and show a raw one
    sample per pixel trace beside a direct view of the same room that is fully denoised. See
    `BasisGlobalIlluminationHistory.AllowedGap`.
  - It **is not where the player is**. It can be flown across the room or set to follow from behind, and
    the ray traced mode shares ONE acceleration structure, light budget and emitter budget across every
    camera in the frame. Those are built around the set of every camera drawing the effect, distance
    measured to the nearest of them, rather than around whichever camera reached the refresh first - so an
    avatar standing in front of the handheld camera is baked into the structure even when the player is
    past Skinned Max Distance from it. See `BasisGlobalIlluminationRayViewerSet`. The budgets are then
    genuinely shared: a light close to the handheld camera can take a slot from one that was only just
    making the cut for the player, which is the price of two viewpoints out of one list and far cheaper
    than a second structure per camera per frame.
  - A capture can also carry a **per-photo override** of everything except resolution, ray budget and
    the temporal filter (already forced above) - mode, quality, intensity, emitters, reflections and the
    rest - set from the camera's own Settings panel, so a photographer can dial in a different look for
    their shot without touching their live settings. Off by default; see
    `BasisGlobalIlluminationCaptureOverride` and `SMModuleGlobalIlluminationURP.BeginCapture`.
- **360 capture** suspends the effect outright. A screen space gather resolves differently on each of the
  six cube faces, so the seams would be visible along every edge.
- **Reflection probes** are off by default; a realtime probe would pay for the effect once per face.

## Lightmapped worlds

Both halves of the double-count are handled now.

**Baked emission.** An emissive quad used as an area light is how a baked world is usually lit, and its
light was already written into the lightmap at bake time - the surface still renders bright because URP
draws emission regardless of how it was baked. Reading that brightness and injecting it again lights the
room twice from one lamp. A surface that is **both** flagged `BakedEmissive` **and** on a renderer carrying a
real lightmap is skipped by the ray traced gather (`Respect Baked Emission`, on by default). Both halves
are required: the flag alone would steal the light in a world nobody ever baked, and a lightmap index alone
says nothing about whether the emission was baked.

**Receiving.** The composite is `Blend DstColor Zero` emitting `obscurance + indirect`, so the frame becomes
`sceneColor * (obscurance + indirect)`. On a lightmapped surface `sceneColor` already contains the baked
bounce and the baked ambient occlusion, so the effect used to multiply a second bounce onto light that had
already bounced, and darken creases that were already dark - a carefully baked world read blown out and
crushed at the same time.

A **lightmap receive mask** answers "is this pixel lightmapped" per pixel: in a scene that has lightmaps at
all, the opaque renderer list is redrawn at traced resolution with a near-empty override pass whose only
output is `LIGHTMAP_ON ? 0 : 1`, depth tested by hand against the camera depth so only the frontmost
surface speaks. The composite then scales what a pixel receives by `lerp(Baked Surface Receive, 1, mask)` -
avatars, props and anything dynamic keep the full effect, and baked geometry keeps only the floor the
setting allows (0.25 by default), because the bounce an avatar throws onto a wall is real light no bake ever
saw. The gather is untouched: lightmapped surfaces still light the room, they just stop being relit by it.

Two decisions in that pass are load-bearing and were both learned the hard way:

- **The mask clears to ONE, and lightmapped surfaces write the zero.** The first version cleared to zero
  meaning "lightmapped", so every way the pass could fail to draw - a BatchRendererGroup refusing a variant,
  an empty renderer list, a culled pass, an unresolved depth texture - collapsed to "global illumination is
  gone". This way round, every failure degrades to the old behaviour instead. The keyword follows the same
  rule: it is set from the recorded texture handle, never from intent, because a bound keyword over an
  unbound texture samples zero and suppresses everything.
- **The pass has its own depth-only include.** `Blit.hlsl` cannot be included by a pass that draws geometry:
  it defines its own `Vert`, and under `DOTS_INSTANCING_ON` its include order breaks EntityLighting's
  lightmap array macros, which is what made the BatchRendererGroup skip the first version's draws entirely.
  `BasisGlobalIlluminationDepth.hlsl` carries the depth helpers with `Core.hlsl` alone, and the pass takes
  URP's `DOTS.hlsl` via `#include_with_pragmas` so the GPU Resident Drawer keeps drawing it.

A world with no lightmaps never records the pass, and `Baked Surface Receive` at 1 disables it exactly.

## The march

A uniform march spends its whole step budget evenly along the ray: **Ray Steps** steps across the whole of
**Max Ray Length**, which at the shipped default is twenty steps over sixteen metres. Near the origin each
stride is tens of texels, and a stride that long walks straight over anything thinner than itself. The
**Thickness** setting exists to paper over the result, and it papers over misses and false hits alike -
stepping past the leg of a chair loses the bounce beneath it, while accepting a surface the ray passed far
behind puts light where none belongs.

**Hierarchical Match** (on by default) splits the walk instead of compensating for it:

1. A two pass reduction builds a coarse summary of the depth buffer - one texel per **8x8 block of traced
   texels**, holding the **closest** real surface under it and the **furthest**. Sky contributes to
   neither, so a block of open sky is skipped by both tests below.
2. The ray crosses that summary a cell at a time. A cell is dismissed outright on either of two grounds,
   each a statement about the whole cell rather than any texel in it:
   - the ray stays **nearer than the closest** thing in the cell for its whole passage, so it cannot have
     hit anything inside; or
   - it is already **further than the furthest**, by more than the thickness the crossing test would
     accept, so it has passed clean out the far side.
3. Anything else is a maybe, and a maybe is answered properly: the march drops to about **a texel a step**
   through the real depth buffer, with the same crossing test and binary refine the uniform march uses.

Because a cosine hemisphere ray can be aimed back towards the viewer, which end of a cell is deepest is not
known in advance, so both bounds come off the pair of entry and exit depths rather than off the exit alone.
The "in front of the depth buffer" state is carried across the **whole** walk rather than rebuilt per cell,
which is what keeps the thickness test a crossing test rather than a proximity test.

Fine walking is capped per ray at four times Ray Steps. That is a backstop for a ray threading heavy depth
complexity, not a routine limiter, and the distinction is not subtle - see the note in
`BasisGlobalIlluminationTrace.hlsl` before tightening it.

**Measured** (2026-08-27, against the same estimator given six times the step budget, which is what it
converges to):

| probe | uniform, 20 steps | hierarchical | converged, 128 steps |
| --- | --- | --- | --- |
| bounce at a slat's contact with the floor | 0.6655 (**-9.1%**) | 0.7411 (**+1.2%**) | 0.7324 |
| open floor a metre and a half away | 0.3982 (**-10.5%**) | 0.4495 (**+1.0%**) | 0.4449 |

So the uniform march at the shipped budget was losing about a tenth of the bounce outright, and the
hierarchical one recovers essentially all of it without raising the budget. ⚠️ **GPU cost has not been
measured** - only correctness. The reason to expect it to be close is that empty space costs one tap per
eight texels where it used to cost a step per stride, but that is an argument, not a measurement.

## How Ray Traced works

For every pixel of the traced buffer:

1. A prepass recovers a world position and a normal from the **depth buffer**, the same way the screen
   space mode does, into a stereo-aware array texture.
2. Cosine-weighted rays are traced against a **scene acceleration structure** rather than the depth
   buffer, so a ray can hit a surface the camera never drew.
3. At a hit, the surface is **shaded rather than sampled**: emission is added, then the lights are
   evaluated against the hit's interpolated vertex normal by **resampled importance sampling** (below).
   Albedo becomes the path throughput and the ray bounces again, up to the quality ladder's bounce
   count.
4. Rays that hit nothing read the sky cubemap, at a mip chosen by the fallback setting.
5. Short first hits accumulate near-field obscurance, exactly as in the screen space mode.
6. The result goes through the same denoiser and composite.

## Denoising

One or two rays per pixel is a very sparse estimate of a hemisphere, and what makes it look like light
rather than like noise is entirely the filter behind it. The chain is temporal accumulation first, then a
spatial cascade, both driven by the same per-pixel statistics.

**Motion Vectors** (off by default, and off for a reason) would reproject through URP's motion vector
texture instead of the matrix below. The matrix carries the **camera's** motion and nothing else, so a
pixel on a moving surface is walked back onto whatever was behind it - its history is rejected on depth
every frame and never accumulates, which is why avatars can be the noisiest thing in a room. The code is
written against URP's own `CalcNdcMotionVectorFromCsPositions` and is believed correct, but it is
**unverified**: an EditMode render loop never advances the engine frame counter that URP's motion vector
pass differences against, so in this harness that texture holds a fixed vector unrelated to the scene -
measured at roughly 1.5 pixels with nothing moving and the camera bolted down. Everything the harness can
say about this setting is a measurement of that. Settling it needs play mode or a headset; until then the
matrix is what ships, because the matrix is what has been measured. `BasisGlobalIlluminationMotionTests`
guards itself on the frame counter and will start measuring for real wherever the engine is really ticking.

**Temporal accumulation** reprojects the previous frame through the previous view-projection, rejects
what has moved out from behind the camera or changed depth, and blends by how many frames the pixel has
already accumulated - a freshly disoccluded pixel takes the whole of this frame, a settled one keeps a
long tail down to the response slider's floor. Alongside the colour it accumulates the **first two
moments of the pixel's luminance**, which is where the variance the spatial filter runs on comes from.

**Neighbourhood clipping** is available (it is what rejects ghosting when a light moves) but its box is
never allowed to close below what a run of misses could plausibly have hidden. Zero bright samples out of
N is not evidence that the true mean is zero: at one or two rays a pixel misses a small bright source far
more often than it finds it, so a neighbourhood that all missed has no spread at all, and a box built
from that spread alone collapses onto zero and erases what the accumulation had already found. That is
what an emissive surface looked like when it flickered. The floor is the standard three-over-N bound on
how often such a hit could have been missed, written in each channel's own units - the firefly ceiling for
colour, the obscurance intensity for the occlusion term - so it tightens on its own as the ray budget
rises.

**The spatial filter** is an à-trous cascade: the same small separable kernel run again at double the
stride each level, so two or three cheap passes reach as far as one enormous one. Every tap is gated on
three things at once:

- **Plane distance**, not depth difference. Two surfaces meeting at a corner sit at almost the same depth
  and a depth difference cannot tell them apart, while one surface seen at a glancing angle spans a large
  depth over a few pixels and a depth difference rejects it from itself. How far a neighbour sits off the
  centre pixel's own plane does neither. The plane comes from the screen-space derivatives of the
  reconstructed world position, which in a fullscreen pass costs nothing.
- **Luminance**, with a gate opened by how *unresolved* the pair is rather than by a fixed width. A pixel
  with no history behind it lets everything through, which is the only way a bright sample that one ray in
  forty found ever reaches the pixels around it; a settled pixel narrows the gate to a few standard
  deviations of its own accumulated swing and keeps its detail. The gate is decided by the pair rather
  than by the centre alone, which makes it symmetric - an asymmetric gate is a one-way valve that lets
  noisy pixels take energy from settled ones without giving any back, and a sparse bounce drains into it.
- **Distance**, a plain Gaussian over the tap offset, scaled by the Smoothing setting.

The bilateral upsample back to full resolution uses the same plane test, which is what stops the bounce
haloing across a silhouette.

### What a hit knows about a surface

There is no way to bind one texture per instance to a trace, so each sub-mesh uploads a small
`BasisGlobalIlluminationRayInstance` carrying its albedo, its emission and where its geometry lives in
the shared arenas. Base and emission maps are folded in as an **average colour**, read once per
texture off the smallest mip of a scratch copy — almost every lit material leaves its base colour
white and puts the actual colour in the map, so without this a red carpet would bounce white.

Vertex normals and triangle indices are copied into two shared `StructuredBuffer` arenas, so a hit
interpolates a real shading normal. A mesh that shipped with Read/Write disabled cannot be read back;
it still occludes and still bounces its material colour, and the trace falls back to a view facing
normal on it.

### Skinned meshes

Avatars are skinned meshes, and a bind-pose avatar in the structure would occlude and bounce light
from the wrong place entirely. Each skinned renderer is baked into a mesh of its own and re-added on a
per-frame budget (`Skinned Budget` bakes per frame, no more often than `Skinned Interval` frames,
inside `Skinned Distance`). Topology never changes across a pose, so a re-bake keeps its arena blocks
and its instance ids and only rewrites the normals. `Off` leaves avatars out of the structure and
`Static` places them once.

### Backends, and Direct3D11

Hardware ray tracing needs Direct3D12 or Vulkan; DXR does not exist in the Direct3D11 API, so
`SystemInfo.supportsRayTracing` is false there and the mode falls back to the screen space gather with a
warning naming the reason.

There is a second backend. Unity's compute ray tracing path walks a software BVH in a compute shader and
needs nothing but `SystemInfo.supportsComputeShaders`, so it runs on Direct3D11. Enable **Ray Tracing
Compute Fallback** on the renderer feature to use it. It is far more expensive than tracing on hardware,
so the ray budget is capped at `ComputeBackendRayCeiling` rays and `ComputeBackendBounceCeiling` bounces
per pixel and raising Quality past that does nothing; a warning says so once. It is the right choice for
seeing the effect on a GPU without DXR, not for shipping a VR frame.

The backend's own kernels come from `RayTracingRenderPipelineResources` in the pipeline's global settings,
which is what carries them into a player build. If that entry is stripped, the compute backend refuses to
start in a build and says so.

### Lights

The lights a hit is shaded by are scene-wide rather than the culled visible list, because a hit can be
behind the camera or in a room the player is not in. They are re-scanned on the geometry's cadence and
re-read every frame so moving lights stay in step. Unity's **indirect multiplier** (`bounceIntensity`)
scales each one, and a light set to zero drops out. Registered emitters join the same list, so a world
that placed them for the screen space mode keeps working here - and they are given half the budget to
themselves when there are enough of them to want it, because an author placed them exactly where the
bounce needed help and they should not queue behind whatever the scene lights did not use.

**Resampled importance sampling** is what decides which of them a hit pays for. Weighing a light is
arithmetic; shadow-raying one is not, and shadow-raying every light at every hit of every bounce is what
used to force the budget down to a dozen. A budget that small is itself a source of flicker: a light drops
out of it as the player walks and takes all of its contribution with it. So every light is weighed by what
it would contribute unshadowed, one is drawn in proportion to those weights (the quality ladder buys more
draws), and only the survivors pay for a ray. Each is scaled by how likely it was to be drawn, which leaves
the estimate unbiased - its expected value is still the sum over every light - and makes a room with sixty
lights cost what a room with one costs. This is the idea behind ReSTIR and NVIDIA's RTXDI, in its simplest
single-frame form.

Whatever still has to be dropped at the edge of a budget - a light or an emitter - is **faded out before it
is displaced** rather than vanishing. The one that gets displaced is always the lowest-scoring one that was
kept, so that one alone is scaled by how clearly it beat the best that missed the cut: by the time the two
swap places they are both contributing nothing and the swap is invisible. A directional light is exempt,
because its rank cannot change as the viewer moves.

## Why no GBuffer

The colour buffer already holds `albedo * lighting` for every visible surface, so the light a ray
gathers needs no material data to be reconstructed, and the surface receiving the bounce uses its own
screen colour as the albedo it modulates the bounce by. That is what lets the effect work with
avatar shaders that have no `UniversalGBuffer` pass — the previous integration had to guess an albedo
for those, and content already built into asset bundles could never gain the pass.

## Reflections

`Reflections` turns on a specular gather: one mirror ray per pixel. What a missed ray is worth is the
`Fallback` setting's call, the same on both backends: under **Sky** a miss claims the bound
environment with full confidence — the explicit "reflections read the sky" opt-in; under **Reflection
Probe** (the default) a miss reports *no data*, and the lit shader keeps the reflection probes it
already sampled for that surface — local, box projected, blended per object — which no fullscreen
pass can see and no global environment guess should override. Every partial confidence the trace
produces (screen edges, rays aimed back at the eye, budget coverage, thin slivers) blends toward
those probes by the same alpha, so probes and traced reflections combine per pixel rather than
switching. It follows `Mode` the way the diffuse gather does, and has the same two backends:

- **Ray traced** — the ray walks the shared acceleration structure and is shaded at the hit by the
  same lights and emissive surfaces the diffuse bounce uses. It reflects things that are off screen,
  which no screen space method can.
- **Screen space** — the same mirror ray walked through the depth buffer with the same hierarchical
  march (and the same depth pyramid) the diffuse gather uses. The trace runs before the opaque draws,
  so the current frame's colour does not exist yet when it needs one; a capture pass at the end of
  each frame keeps the finished camera colour (after transparents, before post processing), and the
  trace reads that, reprojecting each hit through the stored view projection. A ray that misses
  geometry but points at sky the screen can see reads the rendered skybox out of that same capture —
  the environment cubemap is the baked reflection environment, which a world is free to have never
  baked while its skybox renders fine, and only the Sky fallback ever reads that cubemap for rays
  whose sky is off screen (at mip zero: a mirror wants the environment as an image, not the diffuse
  gather's irradiance mip). On the first frame, after a resize, or after a camera stops rendering
  for a while there is no colour to read and every ray reports no data — which is the reflection
  probe the shader had anyway. Ray Traced mode on a GPU without ray tracing falls back here, the
  same direction the diffuse gather falls.

  The trace also writes how far beyond its surface each pixel's reflection sits, and the specular
  temporal filter reprojects history through that *virtual* point rather than through the surface: a
  reflection moves with the parallax of the thing reflected, not of the mirror it sits on, and
  surface reprojection smears every reflection by the difference whenever the camera translates —
  which a head in a headset always does. Reflected sky carries the far sentinel and reprojects as
  pure rotation, which is exact for it.

  The trace is kept clean by a set of measures that were each found against a real artifact, looking
  at real frames. The ray's origin is snapped to the centre of the nearest full resolution depth
  texel before anything reads through it — at a reduced trace resolution the traced pixel centre
  lands exactly on the corner between four full resolution texels, and a point sample decided by
  sub-texel rounding on that knife edge handed every ray an arbitrary neighbour's surface, printing
  evenly spaced lines across every reflective surface. The reflection's own depth pyramid seeds each
  texel with the block's true (nearest, furthest) interval rather than the diffuse gather's unbiased
  representative, and the march reads it as an interval: in front means before the nearest, a
  crossing means past the furthest, in between is ambiguous and carried rather than guessed, and a
  block that borders sky keeps the sentinel as its furthest so silhouette edges are uncrossable
  rather than sometimes-hit. (The diffuse pyramid is unchanged, and with its channels equal the
  interval tests reduce to the exact arithmetic it always ran.) A hit whose surface faces *with* the
  ray, sits in the reflector's own plane with the reflector's own normal, or lands on a sky texel at
  full resolution is a misreading, and the march restarts from just past it — up to twice — instead
  of giving up; the restart distance converts the march's screen fraction to a world distance
  perspective-correctly. Hits on one-or-two-texel slivers keep reduced confidence instead of
  asserting a dashed thin reflection. The walk carries its refine bracket across coarse cells, lands
  each cell's last sample exactly on the cell exit, and gets a cell ceiling sized for a mirror ray's
  reach rather than a bounce's; where it still runs out of budget it reports how much of the ray it
  actually observed, and the miss confidence scales by it — not enough data blends out rather than
  cutting off. Rays aimed back at the eye fade their miss answers toward the reflection probe — what
  they reflect is behind the camera, which no screen space method can see. The march jitter is
  frozen per pixel rather than walked per frame: the mirror ray is deterministic, so the animation
  bought no convergence and cost a per-frame sparkle. And when the renderer is already producing the
  normals texture for the diffuse gather, the trace reflects about those normals instead of the
  depth-reconstructed ones, which is what makes a normal mapped or smooth-shaded surface reflect
  correctly off angle.

**Why a mirror ray and not a roughness-shaped lobe.** Nothing at trace time knows the roughness of the
surface the ray leaves — that is the same missing GBuffer as above. So the trace answers the one
question it can answer exactly, and the lit shader, which does know its own roughness, decides how
much of that answer applies. Below `Specular Max Roughness` the traced reflection is blended in;
above it the reflection probe is kept. There is no keyword: the shader branches on a uniform, so a
frame without reflections costs a scalar compare rather than doubling every lit shader's variants.

**Why it is a second render pass.** The diffuse gather composites into the camera image and runs at
`BeforeRenderingTransparents`. A reflection has to exist *before* the opaque draws, because those are
what consume it — so `SpecularPass` sits just after the prepasses, the way RTAO does. The two share
the kernel, the acceleration structure, the light list and the sky; what they cannot share is a
dispatch.

**What it cannot do.** The reflection direction comes from a normal reconstructed from depth, not from
the surface's normal map, so a strongly normal-mapped surface reflects along its geometric normal.
Transparents are excluded from the trace — they are drawn after the buffer is built and are not in the
depth it was reconstructed from (the screen space backend's *colour* does include them, one frame
late). The screen space backend can only reflect what some previous frame actually saw: content behind
the camera resolves to the sky or the probe, and a moving object's reflection runs one frame behind
the object, which the reflection accumulation's short tail absorbs. The published buffer is full
resolution so the bilateral upsample can keep reflections from bleeding across silhouettes, which
costs one RGBA16F screen-sized target — and the screen space backend keeps one camera-sized colour
target per camera on top of it.

## Setup

Add the **Basis Global Illumination** renderer feature to a URP renderer, then write
`BasisGlobalIlluminationSettings.Current`. In Basis the feature is on `DesktopRenderer` and
`SMModuleGlobalIlluminationURP` writes that object from the graphics settings panel.

**There is deliberately no VolumeComponent.** The settings used to be blended out of URP's volume stack
and that model cost far more than it paid: the settings module had to own a volume at priority 1000 to
beat anything a scene had authored, so a scene volume and the player's settings could disagree and the
player would never know which won; it wrote the player's values into the pipeline's SHARED default
profile assets and had to remember the authored values to put back, so a crash left a profile on disk
holding somebody's runtime state; and because the handheld camera renders on its own volume layer, a
duplicate volume had to be built per uncovered layer just so a second camera saw the same numbers.
Three mechanisms, all load-bearing, none visible in a debugger - and whether the effect ran at all
depended on all three agreeing. One object, written directly, answers the same question by reading it.

Mobile GPUs are not a target: the feature declines to render on them.

## Emitters

Add a `BasisGlobalIlluminationEmitter` to any GameObject to inject a spherical emitter. Emitters are
ranked by brightness over distance squared and the best `Max Emitters` for the active quality are uploaded
each frame; both modes rank through the same call, so a world looks the same either side of a mode switch.
Registration runs in edit mode too, so an author placing them sees their light in the scene view.

**Emitter Occlusion** tests the path from a shaded point to the emitter against the depth buffer. The path
is walked in world space and each point projected on its own, so an emitter that has passed behind the
camera keeps whatever shadow the taps nearest the surface can still see - interpolating between two
projected endpoints instead used to abandon the whole segment the moment that happened, and a wall stopped
casting its shadow at the instant the light behind it left the view. The walk is dithered per pixel, which
turns a hard on/off decision that every pixel flipped on the same frame into a soft edge the filter can
average.

It can only ever test what the camera drew, though: once the occluder itself leaves the frame there is
nothing left to test against and the emitter's light comes back. That is the floor of a screen-space
shadow, and it is the same reason emitters exist - a source the camera cannot see is exactly what they are
for. The transition is gradual rather than a step, which is what the tests hold it to.

Add a `BasisGlobalIlluminationRayExclude` to keep a renderer out of the ray traced acceleration
structure. It still renders normally.

## Design lineage

The screen space pipeline shape — raymarching, colour-buffer radiance gathering, near-field
obscurance, virtual emitters, reflection-probe fallback, and a bilateral/wide/temporal denoise chain —
follows the design of Kronnect's *Radiant Global Illumination*.
No code from that asset is used here; this is an independent implementation.
