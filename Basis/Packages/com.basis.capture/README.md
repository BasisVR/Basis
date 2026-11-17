# Basis Scene Capture

Press Record in Basis and get a folder that Blender can rebuild as an animated scene for high-quality
renders. Each take records:

- **Every avatar, every frame.** You and every other player as you see them. That covers all bones,
  including eyes, fingers, jiggle and hair. It also covers every blend shape (visemes, blinking,
  expressions, face tracking, toggles) and which renderers are visible.
- **Avatar geometry.** Meshes, skin weights, bind poses, blend shapes, materials and textures. Meshes
  that are not CPU-readable are read back from the GPU.
- **The world and props.** Static and moving meshes, lights (also keyed when they change), lightmaps,
  and objects that move during the take, such as doors, pickups and seats.
- **Cameras.** Your view and the handheld camera, with field of view and lens settings.
- **Your microphone** as a WAV lined up with the take.
- **Metadata.** World and prop URLs and versions, player ids and names, avatar URLs and versions, and
  timing against the server clock.

Your own avatar is recorded with its head at normal size, even when it is hidden from you in first
person.

## Who can record

Offline, anyone can record. On a server, only players with the `basis.capture.collect` permission
node can record (admins with `*` have it), and only while the server's capture lock is off. The lock
is on by default; the server keeps it in `config/capture.xml` (`<Locked>true</Locked>`). Players with
`basis.capture.lock` see a lock button in the capture section and can turn it off or on; the change
is saved and reaches everyone straight away. `basis.capture.record` lets an admin record while it is
locked. If the server locks during a take, the take stops. A server without the capture module
counts as locked.

The capture section only shows what you can use. Players who cannot record on the current server
see the lock status, their own recording-request answer and the microphone setting, not the record
button or recording options. The lock button only shows for players who can change it.

## Other players

Recording always asks. When an admin starts, the server sends every other player a recording
request, and sends one to anyone who joins during the take. Each player answers Allow or Decline, or
sets **Recording Requests** to Always Allow or Always Decline. Only players who allow are recorded
at all; everyone else is left out of the take, geometry included.

A player who allows also records their own avatar locally at full quality. Other players reach the
recorder over the network as compressed, interpolated motion, so when the take stops each of them
sends their own capture, peer to peer with a relay through the server as fallback, and Blender
splices it over the version the recorder saw, lined up by server time. **Send My Microphone**
controls whether their microphone goes with it.

Declining stops the same player asking again for two minutes, and the server drops repeat requests
sent within three seconds. Replies go through the server, which stamps them with the real sender.

The server side is a small module in `Runtime/Server` that compiles into `BasisNetworkServer`. Unity
host mode gets it automatically. A standalone server picks it up when the package is installed with
the Basis Package Manager (`basispm server-install`).

## Using it

1. Settings > General > Scene Capture: pick what to record, then **Start Capture**.
2. **Stop Capture**. Geometry and textures finish saving in the background. The status line shows
   progress, who allowed the recording, and the folder, which you can open with **Open Capture
   Folder**.
3. Takes are saved in `<persistentDataPath>/Captures/<date>_<time>/`.

## Blender add-on

The importer is in `Blender~/basis_capture` and needs Blender 4.2 or newer. It uses only numpy, which
ships with Blender.

- Install: zip the `basis_capture` folder, or build it with
  `blender --command extension build --source-dir Blender~/basis_capture`. Then in Blender go to
  Edit > Preferences > Get Extensions > Install from Disk.
- Import: File > Import > **Basis Capture (capture.json)** and pick the take's `capture.json`.

Options: avatars, world and props, cameras, audio, splicing player captures, resampling to the scene
frame rate, reading every Nth frame for long takes, and the light power conversion.

## Folder layout

| File | Contents |
|---|---|
| `capture.json` | Manifest: actors with bone hierarchy and rest pose, renderers, scene objects, cameras, meshes, textures, audio, timing |
| `frames.bin` | Frame stream in about 1 s raw-deflate chunks. Per frame: actor poses (changed bones and blend shapes only), scene transforms, lights, cameras |
| `materials.json` | A PBR mapping for each material, plus a dump of every shader property |
| `assets/mesh_N.bin` | Vertices, normals, tangents, colors, UVs, submeshes, bind poses, skin weights, blend shape deltas |
| `assets/tex_N.png` / `.exr` | Textures. Normal maps are unpacked to RGB. HDR textures are saved as EXR |
| `audio/<uuid>.wav` | Microphone, 16-bit mono |
| `peers/<uuid>/` | Each collected player's own take, in the same format |

All positions use Unity's space (left-handed, +Y up, meters) and matrices are column-major. The
importer converts to Blender's space.

## Not covered yet

- Particles, VFX, the skybox and terrain are not exported.
- Lightmaps are exported as textures and listed in the manifest, but the importer does not apply
  them to materials yet.
- Shader-specific looks (Poiyomi, lilToon toon ramps, rim lights) get the basic PBR mapping. Every
  shader property is saved on the Blender material so you can rebuild the look.
- Other players' voices are not recorded from your side. Each player's own microphone comes in
  through collection.
- Blend shapes with more than one frame keep all frames on readable meshes. On GPU-only meshes, only
  the full-weight frame is kept.
- Nothing is recorded from before Record is pressed.

## License

[MIT](LICENSE) © BasisVR
