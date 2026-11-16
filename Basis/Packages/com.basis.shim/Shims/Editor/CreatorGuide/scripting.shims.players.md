## BasisPlayersShim

The player roster. Players come back as read only `IBasisPlayer`; poses come back as copied vectors.

```csharp
using Basis.Shims;
using Basis.Scripts.BasisSdk.Players;

int count = BasisPlayersShim.Count;
IBasisPlayer me = BasisPlayersShim.Local;
IBasisPlayer nearest = BasisPlayersShim.Nearest(transform.position);
IBasisPlayer[] close = BasisPlayersShim.Within(transform.position, 5f);
IBasisPlayer byId = BasisPlayersShim.GetById(playerId);
IBasisPlayer byUuid = BasisPlayersShim.GetByUUID(uuid);
string[] names = BasisPlayersShim.DisplayNames;
int questPlayers = BasisPlayersShim.CountOnPlatform("Android");

Vector3 position = nearest.GetPosition();
Vector3 head = nearest.GetBonePosition(HumanBodyBones.Head);
float scale = nearest.GetAvatarScale();
bool loaded = nearest.IsAvatarLoaded();
ushort id = nearest.GetPlayerId();
```

## BasisVelocityShim

Worlds only. Pushes the local player in world space, in metres per second.

```csharp
Vector3 v = BasisVelocityShim.Velocity;
BasisVelocityShim.SetVelocity(new Vector3(v.x, 10f, v.z)); // jump pad
BasisVelocityShim.AddVelocity(transform.forward * 8f);     // boost
```

## BasisPlayspaceInputShim

Avatar only. Drives the wearer's own movement. `Additive` mixes with their input, `Override` replaces it.

```csharp
BasisPlayspaceInputShim input = avatar.PlayspaceInput;
input.SetLocomotion(BasisPlayerInputBlend.Additive, new Vector2(0f, 1f), 0f, false, 0f);
input.SetVerticalDelta(BasisPlayerInputBlend.Additive, 0.25f);
input.Clear();
```

## BasisSeatShim

```csharp
if (BasisSeatShim.IsSeated)
{
    BasisSeat seat = BasisSeatShim.CurrentSeat;
    seatLabel.text = seat.name;
}
```

## BasisLockShim

Locks the local player's movement, look rotation or crouching, in any mix. Worlds, props the local player spawned and the avatar they wear lock straight away. Someone else's avatar or prop has to ask first, and the player gets a prompt naming who wants it.

```csharp
BasisLockShim.Lock(this, BasisPlayerLock.Movement | BasisPlayerLock.Crouching, result =>
{
    if (result == BasisLockResult.Locked) countdown.SetActive(true);
});

bool mine = BasisLockShim.IsLocking(this, BasisPlayerLock.Movement);
bool anyone = BasisLockShim.IsLocked(this, BasisPlayerLock.LookRotation);

BasisLockShim.Unlock(this, BasisPlayerLock.Movement);
BasisLockShim.UnlockAll(this);
```

`Lock` returns true only when the lock took on the spot. A prompt answers later on the callback with `Locked`, `Declined`, `Blocked`, `Busy` (another prompt is open), `Unsupported` or `Throttled` (the script keeps re-locking). `IsGranted(this, kinds)` says whether the player already said yes. Locks lift when the script is disabled or the player respawns, and for 10 seconds after a respawn new locks are refused.

## BasisEyeTrackingShim

The local player's eye gaze, eye angles and how open their eyes are. Worlds, props the local player spawned and the wearer's avatar; anything else reads nothing.

```csharp
if (BasisEyeTrackingShim.TryGetGaze(this, out Vector3 origin, out Vector3 direction))
{
    gazeDot.position = origin + direction * 2f;
}

bool eyes = BasisEyeTrackingShim.GetGazeOrHead(this, out Vector3 from, out Vector3 forward); // false means head forward
BasisEyeTrackingShim.TryGetOpenness(this, out float leftOpen, out float rightOpen);
BasisEyeTrackingShim.TryGetEyeAngles(this, out Vector2 leftEye, out Vector2 rightEye); // yaw, pitch in degrees
BasisEyeGazeSource source = BasisEyeTrackingShim.GetSource(this);
```

`FindLookTarget(this, xs, ys, zs, radii, count, toleranceDegrees, headFallback)` returns the index of the sphere the player is looking at, or -1. `Fill(this, state)` writes everything into a float array of `BasisEyeTrackingShim.Length` and returns `FlagGaze`, `FlagAngles`, `FlagOpenness` and `FlagHead` bits for the parts that were valid.

## BasisAvatarSwitchShim

Tells you when a player changes avatar. It follows the player and keeps working across switches. Dispose it when done.

```csharp
BasisAvatarSwitchShim watcher;

void Start()
{
    watcher = new BasisAvatarSwitchShim(BasisPlayersShim.Local);
    watcher.Switched += OnSwitched;
}

void OnSwitched() { Debug.Log("new avatar"); }

void OnDestroy() { watcher?.Dispose(); }
```

## BasisPermissionsShim

The local player's roles answer immediately. Asking about another player is a round trip and the answer arrives on a callback.

```csharp
if (BasisPermissionsShim.LocalIsModerator || BasisPermissionsShim.LocalIsAdmin) staffDoor.SetActive(true);

BasisPermissionsShim.IsModerator(player, isMod =>
{
    badge.SetActive(isMod);
});

bool canKick = BasisPermissionsShim.LocalHasPermission("basis.moderation.kick");
```

For changes while the player is in the world, use `BasisPermissionEventShim` ([Events](/en/docs/scripting/shims/events)).
