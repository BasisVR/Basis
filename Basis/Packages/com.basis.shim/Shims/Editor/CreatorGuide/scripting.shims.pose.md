## BasisPoseOverrideShim

Poses the local player's body. Claim a bone, set it, and Basis keeps applying your last value until you release it. Worlds, props the local player spawned and the wearer's avatar.

```csharp
using Basis.Shims;

void Start()
{
    BasisPoseOverrideShim.Claim(this, HumanBodyBones.Head);
    BasisPoseOverrideShim.ClaimEffector(this, HumanBodyBones.RightHand);
}

void Update()
{
    if (BasisPoseOverrideShim.TryGetUnoverridden(this, HumanBodyBones.Head, out Vector3 position, out Quaternion rotation))
    {
        BasisPoseOverrideShim.SetRotation(this, HumanBodyBones.Head, rotation * Quaternion.Euler(0f, 0f, 15f));
    }

    BasisPoseOverrideShim.SetEffectorAnchored(this, HumanBodyBones.RightHand, HumanBodyBones.Head, new Vector3(0.1f, 0f, 0.1f), Quaternion.identity);
}

void OnDestroy() { BasisPoseOverrideShim.ReleaseAll(this); }
```

Bones run from `Hips` to the toes, plus `UpperChest`. Fingers, eyes and jaw are not included. `Set` takes a position and rotation, `SetRotation` only a rotation, and `TryGetUnoverridden` reads where the bone would be without you.

Effectors are the IK targets for the hands and feet. `SetEffector` moves one, `SetEffectorAnchored` pins it to another body bone, and `TryGetTracked` reads where tracking puts it. `SetBlend` and `SetEffectorBlend` fade between tracking at 0 and your pose at 1.

Only one piece of content can hold a bone at a time; `Claim` returns false when someone else has it. Hip and leg bones cannot be claimed while seated, and sitting down takes them back. Claims drop when the script is disabled.

## BasisRemotePoseShim

Poses other players' avatars on this client only, after their network pose arrives and before the frame is drawn. Opt in with `GetComponent<BasisRemotePoseShim>()` and declare `OnRemotePose(int playerId)`. A world poses any player it tracks, a prop only the remote player who spawned it, and an avatar only the remote player wearing it.

```csharp
using Basis.Shims;

BasisRemotePoseShim pose;
public Transform target;
public Transform elbowHint;

void Start()
{
    pose = GetComponent<BasisRemotePoseShim>();
    pose.TrackAll(true);
}

void OnRemotePose(int playerId)
{
    if (pose.TryGetBone(playerId, HumanBodyBones.Head, out Vector3 head, out Quaternion headRotation))
    {
        pose.SetBoneLocalRotation(playerId, HumanBodyBones.Neck, Quaternion.Euler(20f, 0f, 0f));
    }

    pose.SolveLimb(playerId, HumanBodyBones.RightHand, target.position, target.rotation, elbowHint.position, 1f);
}
```

Reads and writes only work inside `OnRemotePose`, and writes only touch the player being posed. In a world, `Track(id)`, `Untrack(id)`, `TrackAll(bool)` and `UntrackAll()` pick the players; props and avatars get their owner with no tracking needed. `CanPose(id)` and `IsTracked(id)` check ahead of time.

`SetHips` moves the hips, `SetBoneRotation` and `SetBoneLocalRotation` turn the networked bones, and `SolveLimb` bends an arm or leg so the hand or foot reaches a target. `TryGetBoneLocalRotation` reads a local rotation.

Basis spends about 1 ms a frame here, between 4 and 64 players, closest and longest waiting first. Far players in a busy world can update less often.

## BasisLocalPoseShim

Reads the local player's avatar exactly as it will be drawn: after tracking, IK effectors, pose overrides, constraints and jiggle physics. Opt in with `GetComponent<BasisLocalPoseShim>()` and optionally declare `OnLocalPose()`, which runs once a frame right after the snapshot is taken. Worlds, props the local player spawned and the wearer's avatar.

```csharp
using Basis.Shims;

BasisLocalPoseShim pose;
Vector3[] positions = new Vector3[0];
Quaternion[] rotations = new Quaternion[0];
int tail;
int version = -1;

void Start()
{
    pose = GetComponent<BasisLocalPoseShim>();
}

void OnLocalPose()
{
    if (pose.Version != version)
    {
        version = pose.Version;
        positions = new Vector3[pose.Count];
        rotations = new Quaternion[pose.Count];
        tail = pose.Find("Tail");
    }

    pose.GetLocalPositions(positions);
    pose.GetLocalRotations(rotations);

    if (pose.TryGetBone(HumanBodyBones.Head, out Vector3 headPosition, out Quaternion headRotation)) { }
    if (tail >= 0 && pose.TryGet(tail, out Vector3 tailPosition, out Quaternion tailRotation)) { }
}
```

Values are raw `localPosition` and `localRotation`, relative to each transform's parent. Indices 0 to 54 are the humanoid bones in `HumanBodyBones` order, and every other transform under the avatar follows from 55 in hierarchy order, so jiggle chains, accessories and extra bones are all there. `AdditionalCount` and `TryGetAdditional` read only that second part. A humanoid bone the avatar does not have reads as false from `TryGet` and `Has`, and as zero and identity in the bulk arrays.

`GetName` and `GetParent` describe the layout, and `GetParents` copies every parent index at once; a parent of -1 means the avatar root. `TryGetRoot` gives the avatar root's world position and rotation, so the whole body can be rebuilt from the local values. `Version` changes whenever the layout does, which is on avatar swap or when transforms are added or removed, so resize and re-find after it changes. `Frame` is the frame the snapshot was taken.

The snapshot is taken in the last before-render slot of the frame and stays readable until the next one, so `Update` reads last frame's final pose. Nothing is captured while no script has asked for it. Content inside the avatar that belongs to something else, such as a prop parented to a bone, is left out.
