## BasisGraphicsSettingsShim

Reads the player's graphics settings. Tiers are numbers, cheapest first: `TierVeryLow` 0 to `TierUltra` 4.

```csharp
using Basis.Shims;

int quality = BasisGraphicsSettingsShim.QualityTier;
int shadows = BasisGraphicsSettingsShim.ShadowTier;
float resolution = BasisGraphicsSettingsShim.RenderResolution;

mirror.SetActive(quality >= BasisGraphicsSettingsShim.TierHigh);
bool hdr = BasisGraphicsSettingsShim.GetFlag(BasisGraphicsSettingsShim.KeyHdrSupport, false);
string[] readable = BasisGraphicsSettingsShim.ReadableKeys;
```

## BasisStatsShim

What the player's own performance overlay shows. Basis only measures while a script keeps reading. Poll it.

```csharp
float fps = BasisStatsShim.FramesPerSecond;
float target = BasisStatsShim.TargetFramesPerSecond;
if (target > 0f && fps < target * 0.8f)
{
    if (BasisStatsShim.Bottleneck == BasisStatsShim.BottleneckGpu) particles.SetActive(false);
    if (BasisStatsShim.Bottleneck == BasisStatsShim.BottleneckCpu) crowd.SetActive(false);
}

bool online = BasisStatsShim.IsConnected;
int ping = BasisStatsShim.RoundTripTimeMs;
bool stalled = BasisStatsShim.SecondsSinceLastPacket > 2f;
int maxPlayers = BasisStatsShim.PlayerLimit;
```

## BasisComputeShim

Compute shaders work with ordinary `ComputeShader` and `ComputeBuffer` calls. This shim reports support and your budget.

```csharp
if (!BasisComputeShim.IsSupported) { fallback.SetActive(true); return; }
long bytesLeft = BasisComputeShim.RemainingBytes(this);
long threadsLeft = BasisComputeShim.RemainingThreads(this);
```

A buffer over the memory budget throws; a dispatch over the frame's thread budget is skipped. Basis frees every buffer when the content unloads.

## BasisModelShim

Builds a model from bytes for world and prop scripts. Avatars cannot use it. The format comes from the file's contents: GLB, glTF, OBJ, STL or PLY. FBX cannot be loaded at runtime. Files can be up to 64 MB.

```csharp
using Basis.Shims;

BasisModelShim.Load(this, bytes, transform, (result, model) =>
{
    if (result != BasisModelResult.Ok) return;
    chair = model;
});

BasisModelShim.LoadMesh(this, objBytes, (result, mesh) => filter.sharedMesh = mesh);

BasisModelShim.Unload(this, chair);
long triangles = BasisModelShim.RemainingTriangles(this);
long bytesLeft = BasisModelShim.RemainingBytes(this);
```

The callback runs on a later frame. `Load` parents the model to a transform in your own content, or to the script's object when you pass null. `LoadMesh` takes OBJ, STL and PLY.

Basis never fetches anything a file points at, so a glTF must embed its buffers and images. Use `BasisWebRequestShim` to download the file; it caps downloads at 4 MB. Models come in with their meshes, materials, skins and blend shapes. Animations, cameras and lights are left out, along with sparse accessors, Draco, meshopt, KTX and GPU instancing. Textures must be PNG or JPEG.

Each world or prop shares a triangle and memory budget the player sets in Script Permissions. Over budget, the callback gets `TooLarge`. Models stay until you unload or destroy them, or until the content unloads. Unloading frees the meshes and textures, so anything you cloned from a model loses them too.

## BasisPlatformShim

```csharp
bool vr = BasisPlatformShim.IsVR;
bool desktop = BasisPlatformShim.IsDesktop;
bool mobile = BasisPlatformShim.IsMobileGpu;
bool worn = BasisPlatformShim.IsHeadsetWorn;
string platform = BasisPlatformShim.Platform;
bool android = BasisPlatformShim.IsDetected("Android");
string[] conditions = BasisPlatformShim.Conditions;
```

## BasisVoiceRoutingShim

Plays a player's voice from an object, such as a stage microphone feeding speakers. Consent is checked first and is not optional.

```csharp
using Basis.Scripts.Networking.NetworkedAvatar;

int route = -1;

public void StartBroadcast(BasisNetworkPlayer speaker)
{
    if (!BasisVoiceRoutingShim.HasConsent(speaker)) return;
    route = BasisVoiceRoutingShim.RouteVoiceToObject(speaker, speakerBox, 1f, 40f);
}

public void StopBroadcast()
{
    if (route >= 0) BasisVoiceRoutingShim.StopVoiceRoute(route);
    route = -1;
}
```

## VideoPlayerShim

Write `VideoPlayer` as usual; the sandbox swaps in a version that asks the player before loading a URL.

```csharp
using UnityEngine.Video;

var video = GetComponent<VideoPlayer>();
video.url = "https://example.com/trailer.mp4";
video.Play();
```

For synced video, use the **Basis Media Player** component ([Media Player](/en/docs/world/media-player)). Scripts can read its `OutputTexture`; playback controls are blocked so the URL prompt cannot be skipped.
