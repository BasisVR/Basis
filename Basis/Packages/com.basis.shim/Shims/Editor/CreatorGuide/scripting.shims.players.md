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

## BasisPoseOverrideShim

Poses the local player's body on top of their tracking, for things like a hand gripping a wheel or an arm held in a sling. Worlds, the avatar the local player is wearing, and props the local player spawned can use it. Other players see the result.

```csharp
public Transform grip;
float blend;
bool holding;

public void Grab()
{
    holding = BasisPoseOverrideShim.Claim(this, HumanBodyBones.RightHand);
}

void Update()
{
    if (!BasisPoseOverrideShim.IsClaimed(this, HumanBodyBones.RightHand)) return;
    BasisPoseOverrideShim.SetRotation(this, HumanBodyBones.RightHand, grip.rotation);

    if (holding && BasisPoseOverrideShim.TryGetUnoverridden(this, HumanBodyBones.RightHand, out Vector3 wrist, out Quaternion realHand)
        && Quaternion.Angle(realHand, grip.rotation) > 45f) holding = false;

    blend = Mathf.MoveTowards(blend, holding ? 1f : 0f, Time.deltaTime * 5f);
    BasisPoseOverrideShim.SetBlend(this, HumanBodyBones.RightHand, blend);
    if (!holding && blend == 0f) BasisPoseOverrideShim.Release(this, HumanBodyBones.RightHand);
}

void OnDestroy() { BasisPoseOverrideShim.ReleaseAll(this); }
```

| Call | Does |
| --- | --- |
| `Claim(this, bone)` | Takes a bone. False if other content holds it, the bone has no override, or your content can't pose this player |
| `Set(this, bone, position, rotation)` | World pose. Only the hips use the position |
| `SetRotation(this, bone, rotation)` | World rotation |
| `SetBlend(this, bone, 0..1)` | Mixes from their tracking (0) to your pose (1). Use it to ease in and out |
| `TryGetUnoverridden(this, bone, out position, out rotation)` | The pose their tracking would have given this bone without any override |
| `Release(this, bone)`, `ReleaseAll(this)` | Gives bones back |

The bone does nothing until the first `Set` or `SetRotation`, so claiming early doesn't snap the hand. Any script in the same content can use a bone the content holds. The hips, legs, feet and toes can't be claimed while the player is seated, and sitting down takes them back. Bones are given back on their own when the script that claimed them is destroyed or disabled.

`TryGetUnoverridden` comes from the previous frame's solve, which is enough to tell whether the player moved their real hand away or how bent their arm is. Lerp with `SetBlend`, which takes effect in the same frame, rather than mixing that pose yourself.

### Hand and foot targets

`Claim` turns a bone after the body is solved. To move a hand or foot somewhere instead, claim it as an effector. The IK then bends the whole arm or leg to reach your position and rotation in place of the player's controller or tracker, and this works on desktop too, where there is no controller.

```csharp
public Transform handle;

void Start()
{
    BasisPoseOverrideShim.ClaimEffector(this, HumanBodyBones.LeftHand);
}

void Update()
{
    BasisPoseOverrideShim.SetEffector(this, HumanBodyBones.LeftHand, handle.position, handle.rotation);

    if (BasisPoseOverrideShim.TryGetTracked(this, HumanBodyBones.LeftHand, out Vector3 controller, out _)
        && Vector3.Distance(controller, handle.position) > 0.4f)
        BasisPoseOverrideShim.ReleaseEffector(this, HumanBodyBones.LeftHand);
}
```

| Call | Does |
| --- | --- |
| `ClaimEffector(this, bone)` | Takes the left or right hand or foot as a target |
| `SetEffector(this, bone, position, rotation)` | Where the hand or foot goes, and the world rotation it ends up with |
| `SetEffectorBlend(this, bone, 0..1)` | Mixes from the controller (0) to your target (1). With no controller it fades in from the animated pose |
| `TryGetTracked(this, bone, out position, out rotation)` | Where the controller or tracker is this frame. False when there isn't one |
| `ReleaseEffector(this, bone)` | Gives the hand or foot back to the player |

Effector and bone claims are separate, so one script can move the hand with an effector while another turns it with `Claim`. A target the arm can't reach leaves the arm stretched toward it. Feet can't be claimed while seated.

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
