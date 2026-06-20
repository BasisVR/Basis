# Basis LTCGI Integration

Drives [LTCGI](https://ltcgi.dev/) realtime area lighting from a Basis Media Player, so a
video screen casts coloured light that matches whatever is playing.

The Basis Media Player exposes the current video frame as a `Texture`
(`BasisMediaPlayer.OutputTexture`) and never owns a RenderTexture. LTCGI samples a single
shared video texture through its blur chain. This package's `BasisLTCGIVideoAdapter`
bridges the two: it copies each video frame into a RenderTexture and hands it to LTCGI, so
the screen's light updates live with the video.

## Requirements

This package compiles only when both of these are present:

- `com.basis.mediaplayer` — the Basis Media Player, which ships with Basis.
- `at.pimaker.ltcgi` — [LTCGI](https://github.com/PiMaker/ltcgi). It isn't on a public
  registry, so add it to your `Packages/manifest.json` (a git URL or a local `file:` clone)
  before this package will build:

  ```json
  "at.pimaker.ltcgi": "https://github.com/PiMaker/ltcgi.git"
  ```

Until both are present the package is inert — its assembly compiles to nothing — so it is
safe to leave in any project.

Basis renders with URP. LTCGI's URP-compatible surface shaders (`LTCGI_Surface_v2_URP`,
`LTCGI_Simple_URP`) let geometry lit by LTCGI render correctly under URP.

## Setup

The result is a video screen (the Media Player quad) that emits area light onto nearby
surfaces. There are two halves: telling LTCGI the screen is a dynamic light source, and
attaching the adapter that feeds it the video.

### 1. Add and bake an LTCGI controller

If your scene doesn't already have one, add an **LTCGI Controller** to the scene. It is the
object that bakes lighting data and hosts the runtime adapter.

### 2. Mark the Media Player screen as a dynamic LTCGI screen

On the `MediaPlayerStreaming` object's quad (the renderer that displays the video):

1. Add an **`LTCGI_Screen`** component.
2. Tick **Dynamic** — the video changes every frame, so the screen must update at runtime.
3. Set **Color Mode** to **Texture**, and leave the texture selection on **`[Live Video]`**
   (index 0). This tells the screen to sample the live video feed rather than a solid
   colour or a static texture.

### 3. Bake

On the LTCGI Controller, click **Force Update** (or press Ctrl-S). This bakes the screen in
and wires up the runtime adapter. You must re-bake any time you change the screen's
**Dynamic** flag or **Color Mode**.

> The controller will show a *"Video Texture is not set!"* warning. **This is expected** —
> the video is supplied at runtime by the adapter (next step), not assigned on the
> controller. You can ignore the warning. There is no edit-mode preview; the screen only
> emits light once the player is decoding frames in Play mode.

### 4. Add the video adapter

Add a **`Basis LTCGI Video Adapter`** (`BasisLTCGIVideoAdapter`) component to the
`MediaPlayerStreaming` object. The two references resolve automatically:

- **Player** — found on the same object / parents.
- **Target** — the LTCGI runtime adapter in the scene.

Assign them manually only if auto-resolve picks the wrong one (e.g. multiple players or
controllers in the scene).

### 5. Play

Enter Play mode with a video loaded and playing. The screen should light the room with the
video's colours. In the Console you'll see:

```
LTCGI adapter started for 1 (1 dynamic) screens ...
```

The `(1 dynamic)` confirms the screen registered. If it says *"going to sleep"*, the screen
isn't registered as dynamic — re-check steps 2–3.

## Component reference

| Field | Default | Purpose |
| --- | --- | --- |
| **Player** | auto | The `BasisMediaPlayer` to read frames from. Falls back to `GetComponentInParent`. |
| **Target** | auto | The `LTCGI_UdonAdapter` to feed. Falls back to the first one in the scene. |
| **Flip Vertically** | On | Corrects native GPU textures, which arrive top-left origin (upside-down for Unity). |
| **Flip Horizontally** | On | Together with Flip Vertically, matches the standard Basis media-player quad's UVs. |

Both flips are **on by default** because that orientation is correct for the standard Basis
media-player quad. If you use a differently-mapped mesh and the reflected image looks
mirrored, toggle whichever axis is wrong.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| Screen emits a solid colour, not the video | Color Mode is **Static**. Set it to **Texture** with **`[Live Video]`** selected, then **Force Update**. |
| No light at all; Console logs *"going to sleep"* | Screen isn't dynamic. Tick **Dynamic** on the `LTCGI_Screen` and **Force Update**. |
| Reflection is mirrored / upside-down | Adjust **Flip Vertically** / **Flip Horizontally** on the adapter. |
| Console: *"no LTCGI_UdonAdapter in the scene"* | The controller hasn't been baked. Click **Force Update** first. |
| Console: *"LTCGI_UdonAdapter has no BlurCRTInput"* | LTCGI's blur chain isn't available. This happens when **Fast Sampling** is on — which is **forced on for Android/Quest** build targets. The blur-chain video path requires a desktop/standalone target with Fast Sampling off. |
| *"Video Texture is not set!"* on the controller | Expected — the adapter feeds the texture at runtime. Ignore it. |

## How it works

`BasisLTCGIVideoAdapter` subscribes to `BasisMediaPlayer.OnOutputTextureChanged`, blits each
frame into a RenderTexture it owns (applying the configured flips), and pushes that texture
into LTCGI via `LTCGI_UdonAdapter._SetVideoTexture`. The blur chain re-prefilters it every
frame, so the emitted light tracks the video.

If something resets LTCGI's global shader state at runtime — for example a Desktop/VR mode
switch — the adapter detects the dropped binding and re-asserts the feed on the next frame,
so the lighting recovers on its own.
