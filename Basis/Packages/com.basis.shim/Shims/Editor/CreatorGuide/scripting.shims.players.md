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

## BasisAvatarSwitchShim

Tells you when a player changes avatar. It follows the player, so it keeps working across switches. Dispose it when done.

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

The local player's roles answer immediately. Asking about another player is a round trip, so the answer arrives on a callback.

```csharp
if (BasisPermissionsShim.LocalIsModerator || BasisPermissionsShim.LocalIsAdmin) staffDoor.SetActive(true);

BasisPermissionsShim.IsModerator(player, isMod =>
{
    badge.SetActive(isMod);
});

bool canKick = BasisPermissionsShim.LocalHasPermission("basis.moderation.kick");
```

For changes while the player is in the world, use `BasisPermissionEventShim` ([Events](/en/docs/scripting/shims/events)).
