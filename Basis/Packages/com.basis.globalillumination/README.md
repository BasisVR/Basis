# Basis Global Illumination

Real-time global illumination for the Universal Render Pipeline, replacing the
`com.jiaozi158.unityssgiurp` integration Basis shipped previously.

Two modes share one denoise and composite chain, chosen by the volume's **Mode**:

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
2. One or more cosine-weighted rays are marched **through the depth buffer** in screen space.
3. Where a ray hits a surface, the **camera colour at that point is gathered as incoming radiance**.
   Because the colour sampled is the previous frame's, bounces compound over frames.
4. Rays that hit nothing fall back to the reflection probe or the sky.
5. Short hits accumulate **near-field obscurance**, the effect's own ambient occlusion term.
6. Registered **emitters** inject light analytically, for sources too small to survive at traced
   resolution or that are off screen entirely.
7. The result goes through the denoiser below, then is bilaterally upsampled and composited.

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

## Setup

Add the **Basis Global Illumination** renderer feature to a URP renderer, then drive
`BasisGlobalIlluminationVolume` from any Volume. In Basis the feature is on `DesktopRenderer` and the
volume is driven by `SMModuleGlobalIlluminationURP` from the graphics settings panel.

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
obscurance, virtual emitters, reflection-probe fallback, and a bilateral/wide/temporal denoise chain
driven through the Volume system — follows the design of Kronnect's *Radiant Global Illumination*.
No code from that asset is used here; this is an independent implementation.
