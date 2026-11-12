# Eye tracking (BasisEyeTrackingShim)

Status: implemented in `Shims/BasisEyeTrackingShim.cs`, reading `BasisEyeTrackingManager.Current` in `com.basis.framework/Device Management/EyeTracking`. Untested.

## Why

Gaze menus: look at a button to highlight it, hold the look (dwell) or blink to press it. Content can also aim with the eyes, or fade things in when the player looks at them.

## API

```csharp
namespace Basis.Shims
{
    public enum BasisEyeGazeSource { None = 0, Headset = 1, FaceTracking = 2 }

    public static class BasisEyeTrackingShim
    {
        public static bool IsAvailable(object self);                 // eye gaze this frame
        public static bool HasOpenness(object self);                 // eyelid data this frame
        public static BasisEyeGazeSource GetSource(object self);
        public static bool TryGetGaze(object self, out Vector3 origin, out Vector3 direction);
        public static bool GetGazeOrHead(object self, out Vector3 origin, out Vector3 direction);
        public static bool TryGetEyeAngles(object self, out Vector2 left, out Vector2 right);
        public static bool TryGetOpenness(object self, out float left, out float right);
        public static int Fill(object self, float[] state);
        public static int FindLookTarget(object self, float[] centersX, float[] centersY, float[] centersZ,
            float[] radii, int count, float toleranceDegrees, bool headFallback);
    }
}
```

Pass `this`. Every call returns `false`, `None`, `0` or `-1` when it was refused.

| Call | Gives |
| --- | --- |
| `TryGetGaze` | The combined world-space gaze ray. The direction is normalized. |
| `GetGazeOrHead` | The gaze ray when there is one and returns `true`. Otherwise it gives the camera's forward ray and returns `false`. On desktop that is the centre of the screen, so one menu works for everyone. |
| `TryGetEyeAngles` | Each eye's yaw (`x`, right is positive) and pitch (`y`, up is positive) in degrees, relative to the head. |
| `TryGetOpenness` | Each eyelid, 0 closed to 1 open. Gives 1 and `false` without data. Eyelids only come from face tracking: headset eye gaze alone has none. |
| `GetSource` | Where the gaze came from: the headset's eye tracker or face tracking (OSC, MediaPipe). |

## Picking what the player looks at

`FindLookTarget` takes spheres (centres plus optional radii, `null` for points) and returns the index of the one the gaze points at, or `-1`. A sphere counts when the gaze passes within `toleranceDegrees` of its edge (clamped to 0..30). When the gaze is inside several, the one whose centre is closest to the gaze wins. Eye trackers jitter by a degree or two, so a tolerance of 2 to 4 degrees keeps small buttons easy to hold. Set `headFallback` to fall back to the camera ray without eye tracking.

```csharp
float[] xs, ys, zs, radii; // one entry per button, refilled when the menu moves
int hovered = -1;
float held;

void Update()
{
    int look = BasisEyeTrackingShim.FindLookTarget(this, xs, ys, zs, radii, xs.Length, 3f, true);
    held = look == hovered ? held + Time.deltaTime : 0f;
    hovered = look;
    if (hovered >= 0 && held > 0.8f) { Press(hovered); held = float.NegativeInfinity; }
}
```

Blink to press:

```csharp
if (BasisEyeTrackingShim.TryGetOpenness(this, out float l, out float r) && l < 0.2f && r < 0.2f) ...
```

Normal blinks last about 0.1 to 0.3 s, so require the eyes closed for longer than that before acting.

## One call per frame

Cilbox pays per call, so `Fill` writes everything into a `float[]` of at least `BasisEyeTrackingShim.Length` and returns flags:

| Index | Value |
| --- | --- |
| `OriginX..OriginZ`, `DirectionX..DirectionZ` | Gaze ray, or the camera ray when `FlagHead` is set |
| `LeftYaw`, `LeftPitch`, `RightYaw`, `RightPitch` | Degrees, valid with `FlagAngles` |
| `LeftOpenness`, `RightOpenness` | 0..1, valid with `FlagOpenness` |

`FlagGaze` means the ray is eye gaze. `0` means refused, or no gaze and no camera.

## Timing

Eye data is merged once per frame in LateUpdate, so `Update` reads the previous frame's sample. That is one frame behind, which a menu does not notice.

## Who can read it

Worlds, props the local player spawned, and the avatar the local player is wearing. Someone else's avatar or prop is refused, with no prompt. A script only ever gets the local player's eyes; nothing about other players' gaze is exposed.

Gaze shows what the player pays attention to. Basis does not send it anywhere for the script, but a world script can send what it reads over the network like any other value.

## Permissions

The shim is listed as "See where you are looking" in the script consent prompt, as a severe line. Avatar boxes whitelist `BasisEyeTrackingShim` and `BasisEyeGazeSource` by name. Prop and world boxes get them through `Basis.Shims.*`.
