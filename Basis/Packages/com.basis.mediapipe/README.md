# Basis MediaPipe Tracking

Webcam-driven avatar tracking for desktop Basis. It turns a normal webcam into "fake VR"
input: head/neck/upper-body **trackers**, **finger** curl/splay, **eye** gaze + blink, and
**face** blendshapes/visemes — so a desktop user can emote and move like a tracked VR user.

Inference uses the [MediaPipe Unity Plugin (homuler)](https://github.com/homuler/MediaPipeUnityPlugin)
(Apache-2.0). That plugin is an **optional dependency**: this package compiles and ships
**inert** without it, and lights up automatically once it is installed.

## Status

| Milestone | Scope | State |
|-----------|-------|-------|
| **M0** | Package, asmdefs, webcam capture, device-source lifecycle, backend seam | **done** |
| **M1** | FaceLandmarker → eye gaze/blink + 52 ARKit face blendshapes (via Basis.Comms) | **done** |
| **M2** | HandLandmarker → finger curl/splay (via BasisLocalHandDriver) | **done** |
| **M4 (partial)** | Settings tab: enable + camera select + feature toggles | **done** |
| M3 | Head/neck pose + upper-body trackers + calibration + desktop head hand-off | planned |
| M4 | en.json localization, debug overlay, per-platform packaging, perf | planned |

The homuler backend runs FaceLandmarker + HandLandmarker in **VIDEO mode** (synchronous, on the
main thread); M4 will move inference off-thread (LIVE_STREAM + TextureFrame).

## How it fits Basis

- `BasisMediaPipeManagement : BasisBaseTypeManagement` is a **device source**. Add it to the
  `BasisDeviceManagement` GameObject and include it in that object's **`BaseTypes`** list.
  Its per-frame work runs from `BasisDeviceManagement.Simulate()` — the central tick — so it
  adds no new `Update()` loop.
- Body poses are published as `BasisInputXRSimulate` trackers (the framework's existing fake
  device), assigned fixed roles (`Head`, `LeftHand`, `Hips`, …) via `InitalizeTracking(..., ForceAssignTrackedRole: true, role)`.
  Everything downstream (FBIK, the muscle/finger bitstream, bone networking) is reused as-is.
- Fingers will drive `BasisLocalHandDriver.LeftHand/RightHand`; eyes + face blendshapes will go
  through `HVR.Basis.Comms` `AcquisitionService` (already networked to remotes).

```
WebCamTexture ─► BasisMediaPipeCamera ─► IBasisMediaPipeBackend (homuler) ─► BasisMediaPipeResult
                                                                                   │
                                       BasisMediaPipeManagement.ApplyResult ◄──────┘
                                       ├─ trackers (Head/hands/upper body)
                                       ├─ BasisLocalHandDriver (fingers)
                                       └─ AcquisitionService (gaze/blink/blendshapes)
```

## Setup (already wired in this project)

These steps are **done** in this repo — listed so it's reproducible:

1. **Plugin** `com.github.homuler.mediapipe` **0.16.3** is installed via `Packages/manifest.json`
   (`"file:com.github.homuler.mediapipe-0.16.3.tgz"`, tarball alongside the other `*.tgz` deps).
   Unity auto-defines `BASIS_MEDIAPIPE` (via `versionDefines` on `BasisMediaPipe.Homuler.asmdef`),
   activating the homuler backend assembly. If it ever doesn't, add `BASIS_MEDIAPIPE` to
   **Project Settings → Player → Scripting Define Symbols**.
2. **Models** ship as Addressable `TextAsset`s (`.bytes`) in `Packages/com.basis.mediapipe/Models/`:
   `face_landmarker.task.bytes`, `hand_landmarker.task.bytes`, `pose_landmarker_lite.task.bytes`,
   organized into the dedicated **Basis MediaPipe Models** group (PackSeparately) by
   *Basis ▸ Addressables ▸ Organize Model Groups* and an importer on that folder. The loader reads
   them via `Addressables.LoadAssetAsync<TextAsset>(...).WaitForCompletion()` and hands the raw bytes
   to MediaPipe's `modelAssetBuffer`. See
   `com.basis.framework.editor/Editor/ADDRESSABLES.md` for the full group layout and tooling.
3. **Manager**: `BasisMediaPipeManagement` lives on the `BasisDeviceManagement` object and is in
   its `BaseTypes` list. The Settings tab also self-wires it if missing.
4. **For face/eyes to drive your avatar** the avatar needs HVR Basis Comms `AutomaticFaceTracking`
   plus ARKit- or Unified-Expressions-named blendshapes and eye bones (same requirement as
   VRCFaceTracking-over-OSC). **Fingers don't need this** — they drive `BasisLocalHandDriver` directly.

## Using it

Open **Settings -> Webcam Tracking**:
- **Enable Webcam Tracking**: turn it on/off. Every other setting applies live; nothing restarts the camera except a camera, resolution or frame-rate change.
- **Camera Preview**: the live feed with the pose skeleton, hands, face box and gaze drawn over it, plus a plain-language camera state (no camera, starting, stalled and restarting, black frames, tracking). Only rendered while the page is open.
- **Camera**, **Camera Resolution**, **Camera Frame Rate**: device and capture settings. A missing camera is retried every few seconds and picked up when plugged in; a camera that stops delivering frames is restarted.
- **Body Model**: Lite, Full or Heavy pose model, swapped without a restart.
- **Face & Eyes**, **Hands & Fingers**, **Head Rotation**, **Head Position**, **Arm Tracking**, **Body Lean/Twist**, **Mirror Camera**: per-feature toggles. Eye gaze comes from the iris landmarks; expressions and fingers ease back to neutral when the face or a hand leaves the frame.
- **Low Light Boost**, **Slow Camera In Low Light**, **Reject Tracking Glitches**: brighten a dark feed before inference, drop to 15 fps for a longer exposure while the room is dark, and drop single-frame landmark jumps (all on by default).
- **Eye Gaze Strength** and the smoothing sliders: converter tuning, applied on the spot.
- **Calibrate Head**: captures your neutral head pose and gaze centre. It is also captured automatically the first time your head is held still, and saved across sessions.

The three models run on their own threads for one frame at a time, so the tracking rate is set by the slowest model rather than the sum of all three. Results are triple-buffered, so a running session allocates nothing per frame.

## Platform notes

- **Windows / Linux / macOS desktop:** primary targets; `WebCamTexture` is the capture path.
- **Android phone:** works with the selfie camera (one more native-lib target to package).
- **Quest / standalone HMD:** **not practical** — there is no user-facing camera and passthrough
  cameras are locked down for apps. Use an external/USB camera or a phone as the source instead.

## Notes

- `.meta` files are generated by Unity on first import.
- Monocular webcam tracking estimates depth rather than measuring it: rotation and expression are
  strong, absolute position is approximate (M3 calibration mitigates this). Legs/lower body are
  intentionally out of scope for a seated webcam.
