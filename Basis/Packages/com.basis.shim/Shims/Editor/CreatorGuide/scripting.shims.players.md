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

Hips, spine, chest, neck and head claims are applied before the arms and legs are solved, so tracked hands and feet still reach their targets from the turned body. A claimed arm or leg bone is applied right after that limb is solved.

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
| `SetEffectorAnchored(this, bone, anchor, localPosition, localRotation)` | Holds the hand or foot on another bone of the same player, at a pose relative to that bone. False for a bone of the same limb |
| `SetEffectorBlend(this, bone, 0..1)` | Mixes from the controller (0) to your target (1). With no controller it fades in from the animated pose |
| `TryGetTracked(this, bone, out position, out rotation)` | Where the controller or tracker is this frame. False when there isn't one |
| `ReleaseEffector(this, bone)` | Gives the hand or foot back to the player |

To make a hand hold another part of the body, work out the offset once when it grabs and leave it there. The solve reads the anchor bone after it has been posed this frame, so chains like a hand on a forearm whose hand holds a knee line up without any ordering on your side, and they follow a turned head or chest.

```csharp
public void Grab(HumanBodyBones held, Transform heldBone, Transform hand)
{
    BasisPoseOverrideShim.ClaimEffector(this, HumanBodyBones.RightHand);
    Quaternion inverse = Quaternion.Inverse(heldBone.rotation);
    BasisPoseOverrideShim.SetEffectorAnchored(this, HumanBodyBones.RightHand, held, inverse * (hand.position - heldBone.position), inverse * hand.rotation);
}
```

When two limbs hold each other, one of them reads where the other was last frame.

Effector and bone claims are separate, so one script can move the hand with an effector while another turns it with `Claim`. A target the arm can't reach leaves the arm stretched toward it. Feet can't be claimed while seated.

## BasisRemotePoseShim

Runs your own IK on other players' bodies. Declare `void OnRemotePose(int playerId)` and fetch the component once; the callback then runs every frame for each player you may pose, right after their networked pose is applied and before jiggle, eyes and rendering. What you write is only seen on this client. When the callback runs, the bones start again from the networked pose, and they go back to it once your script stops posing them.

An avatar poses the player wearing it and a prop poses the player who spawned it, with no tracking needed. Worlds pick who to pose with `Track` or `TrackAll`. The local player never gets the callback; use `BasisPoseOverrideShim` for them.

```csharp
BasisRemotePoseShim pose;
public Transform rail;

void Start()
{
    pose = GetComponent<BasisRemotePoseShim>();
    pose.TrackAll(true);
}

void OnRemotePose(int playerId)
{
    if (!pose.TryGetBone(playerId, HumanBodyBones.RightHand, out Vector3 hand, out Quaternion handRotation)) return;
    if (!pose.TryGetBone(playerId, HumanBodyBones.RightLowerArm, out Vector3 elbow, out _)) return;
    Vector3 onRail = rail.position + Vector3.Project(hand - rail.position, rail.forward);
    float weight = Mathf.InverseLerp(0.3f, 0.1f, Vector3.Distance(hand, onRail));
    pose.SolveLimb(playerId, HumanBodyBones.RightHand, onRail, handRotation, elbow + Vector3.down * 0.2f, weight);
}
```

| Call | Does |
| --- | --- |
| `Track(playerId)`, `Untrack(playerId)`, `TrackAll(bool)`, `UntrackAll()` | Worlds: who gets the callback. Players who leave are dropped |
| `CanPose(playerId)`, `IsTracked(playerId)` | Whether your content may pose them, and whether they get the callback |
| `TryGetBone(playerId, bone, out position, out rotation)` | World pose of any remote player's bone, including your writes so far this frame |
| `TryGetBoneLocalRotation(playerId, bone, out rotation)` | Local rotation of a bone |
| `SetBoneRotation(playerId, bone, rotation)`, `SetBoneLocalRotation(...)` | Turns one bone. Eyes and jaw are left to the face system |
| `SetHips(playerId, position, rotation)` | Moves the hips, and the body with them |
| `SolveLimb(playerId, tip, target, rotation, hint, weight)` | Two-bone IK. `tip` is a hand or foot, `hint` is a world point the elbow or knee bends toward, `weight` mixes from their pose (0) to the solve (1) |

Reads and writes only work inside `OnRemotePose`, which is the one point in the frame where remote skeletons are settled. Writes go to the player the callback is for; reads can look at any remote player.

The callback doesn't run for every player every frame. Each frame a fixed number of players get a run, nearest and longest-waiting first, and the pass stops early once it has used about a millisecond. Between runs the bones you wrote keep the pose your script last gave them, applied on worker threads together with the networked pose, so a crowd of scripted avatars costs a few callbacks a frame rather than one per player. Write the bones you want every time the callback runs: a bone you skip goes back to the networked pose. The nameplate and mouth position still follow the networked head.

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
