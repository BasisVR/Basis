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

What the player's own performance overlay shows. Measured only while a script keeps reading, so poll it.

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
