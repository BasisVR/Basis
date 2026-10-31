## Setup

1. Add **Basis Seat** to a chair and position its sit point. The `SimpleSeat` prefab in com.basis.examples is a ready made one.
2. Add **Basis Seat Sync** so everyone sees who is sitting and two players cannot take the same seat.

Players can sit by pointing at the seat. The script below adds a button that seats them, turns the chair, and lets them stand up.

```csharp title="SwivelChair.cs (Cilbox)"
using Basis.Scripts.BasisSdk.Interactions;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class SwivelChair : MonoBehaviour
{
    public BasisSeat seat;
    public float turnDegrees = 45f;
    float yaw;

    public void SitHere()
    {
        if (seat.IsAvailable) seat.TrySeatLocalPlayer();
    }

    public void TurnLeft() => Turn(-turnDegrees);
    public void TurnRight() => Turn(turnDegrees);

    void Turn(float delta)
    {
        if (!seat.IsLocalPlayerSeated) return;
        yaw += delta;
        seat.SetOccupantYaw(yaw);
    }

    public void StandUp()
    {
        if (seat.IsLocalPlayerSeated) seat.EjectLocalPlayer();
    }
}
```

| Member | Meaning |
| --- | --- |
| `IsAvailable` | Nobody is sitting and seats are not disabled in the player's settings. |
| `IsLocalPlayerSeated` | The local player is in this seat. |
| `HasOccupant` | Anyone is in this seat. |
| `TrySeatLocalPlayer()` | Sits the local player down if the seat is free. |
| `SetOccupantYaw(degrees)` | Turns the seated player, within the seat's rotation limits. |
| `HipsLock` | `Locked` (default) holds the hips at the seat. `UnlessTracked` releases them when the player has a hips tracker. `Free` always lets them follow the head and trackers, while the seat still carries the player. |
| `LeftFootLock`, `RightFootLock` | `Locked` (default) poses that leg into the seat. `UnlessTracked` releases it when the player has a tracker on that leg. `Free` always leaves it to trackers and foot placement. |
| `OccupantOffsetLimit` | How far `SetOccupantOffset` may move the player, in the seat's local units. 0 holds them on the fit point. |
| `SetOccupantOffset(offset)` | Moves the seated player relative to the fit point, in the seat's local space. Resets when they stand. |
| `EjectLocalPlayer()` | Stands the local player up. |

> [!info]
> Seats only move the local player and the script needs no networking of its own. **Basis Seat Sync** handles who is sitting.
