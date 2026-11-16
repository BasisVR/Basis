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

## BasisCameraShim

Worlds and props can call `Camera.Render()` on a camera inside their own content, as long as it has a target texture. A world gets 4 renders a frame and a prop gets 1. Extra calls that frame are skipped.

```csharp
public Camera securityCamera;

void Update()
{
    if (BasisCameraShim.RemainingRenders(this) > 0) securityCamera.Render();
}
```

Rendering the main camera, a camera from other content, or calling `Render` while a frame is drawing throws. Props can also set `cullingMask` and `orthographicSize` on their own cameras.

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

## BasisSteamAudioShim

Turns on Steam Audio for an `AudioSource` in your own content and tunes it. Worlds, props and avatars can use it. A world can add 64 sources (16 with reflections), a prop 8 (2) and an avatar 4 (1). Every call returns false when Steam Audio is not available, as in web builds.

```csharp
AudioSource radio = GetComponent<AudioSource>();
if (!BasisSteamAudioShim.Add(this, radio)) return;

BasisSteamAudioShim.SetOcclusion(this, radio, true);
BasisSteamAudioShim.SetTransmission(this, radio, true, true, 2);
BasisSteamAudioShim.SetAirAbsorption(this, radio, true);
BasisSteamAudioShim.SetReflections(this, radio, true, 1f, false);

float heard = BasisSteamAudioShim.GetOcclusion(this, radio);
int reflectionsLeft = BasisSteamAudioShim.RemainingReflectionSources(this);
```

`SetHrtf`, `SetDistanceAttenuation`, `SetDirectivity` and `SetDirectMix` cover the rest. Worlds can also turn their own readable meshes into acoustic geometry, up to 512,000 triangles. The seven numbers are absorption low, mid and high, scattering, then transmission low, mid and high, each 0 to 1.

```csharp
BasisSteamAudioShim.AddGeometry(this, wallFilter);
BasisSteamAudioShim.AddGeometry(this, curtainFilter, 0.6f, 0.7f, 0.8f, 0.05f, 0.1f, 0.05f, 0.02f);
BasisSteamAudioShim.RemoveGeometry(this, wallFilter);
```

## BasisRemotePlayerAudioShim

World only. Overrides Steam Audio on a remote player's voice, for example turning occlusion off for whoever is on stage. Overrides end when that player leaves or the world unloads.

```csharp
using Basis.Scripts.Networking.NetworkedAvatar;

public void OnStageEntered(BasisNetworkPlayer performer)
{
    BasisRemotePlayerAudioShim.SetOcclusion(this, performer, false);
    BasisRemotePlayerAudioShim.SetReflections(this, performer, true, 1.5f);
}

public void OnStageLeft(BasisNetworkPlayer performer)
{
    BasisRemotePlayerAudioShim.Clear(this, performer);
}
```

The listener's own settings cap how many voices get reflections; `RemainingReflectionPlayers(this)` says how many are left. `ClearAll(this)` drops every override the world made.

## VideoPlayerShim

Write `VideoPlayer` as usual; the sandbox swaps in a version that asks the player before loading a URL.

```csharp
using UnityEngine.Video;

var video = GetComponent<VideoPlayer>();
video.url = "https://example.com/trailer.mp4";
video.Play();
```

For synced video, use the **Basis Media Player** component ([Media Player](/en/docs/world/media-player)). Scripts can read its `OutputTexture`; playback controls are blocked so the URL prompt cannot be skipped.
