Every networked object has at most one **owner**. The owner is the player whose copy is the truth: they write synced variables, they simulate the physics, and their version is what late joiners receive. Everyone else follows.

The server decides ownership and two players can never both own an object.

## Reading ownership

| Member | Meaning |
| --- | --- |
| `IsLocalOwner()` | True when the server has confirmed you own it. Use this for decisions. |
| `IsOwnedLocallyOnServer` | Same as above, as a field. Available to Cilbox. |
| `IsOwnedLocallyOnClient` | Set the moment you **request** ownership, before the server answers. Lets you start driving the object without waiting a round trip. |
| `CurrentOwnerId` | Player ID of the owner. |
| `currentOwnedPlayer` | The owner as a `BasisNetworkPlayer`. |
| `HasNetworkID` | False until the object is network ready. Ownership is meaningless before that. |

## Taking ownership

```csharp title="Native"
public void OnGrab()
{
    if (!IsLocalOwner()) TakeOwnership();
}
```

`TakeOwnership()` takes the object even if someone else owns it. If you need to know the result, await the async version:

```csharp title="Native"
BasisOwnershipResult result = await TakeOwnershipAsync();
if (result.Success) StartDriving();
```

Ask who owns it without taking it:

```csharp title="Native"
BasisOwnershipResult who = await RequestWhoIsOwnershipAsync();
if (who.Success) Debug.Log($"owner is {who.PlayerId}");
```

> [!info]
> Take ownership at the moment a player **acts** on the object: grabbing it, pressing its button, sitting in it. Do not take it in `Start` or `OnNetworkReady`; every player would grab it at load and the last one wins.

## Reacting to transfers

```csharp title="Native"
public override void OnOwnershipTransfer(BasisNetworkPlayer newOwner)
{
    bool mine = IsLocalOwner();
    body.isKinematic = !mine;
    ownerLabel.text = newOwner.Player != null ? newOwner.Player.SafeDisplayName : "";
}

public override void OnServerOwnershipDestroyed()
{
    // Nobody owns it any more.
}
```

```csharp title="Cilbox"
void Start()
{
    net = GetComponent<BasisNetworkShim>();
    net.OwnershipTransfer += OnOwnerChanged;
}

void OnOwnerChanged(BasisNetworkPlayer newOwner)
{
    body.isKinematic = !(net.HasNetworkID && net.IsOwnedLocallyOnServer);
}
```

## Who owns an object at first

When a networked object is first seen by the server, the first player who asks about it becomes its owner. In practice that is the first player in the instance. `RequestOwnershipIfNone()` on a `BasisNetworkShim` asks the server and gets ownership only if nobody has it.

## When the owner leaves

When the owner disconnects, the server hands each of their objects to the **longest connected player still in the instance**. Everyone gets `OnOwnershipTransfer`, and the new owner continues from the state it already had. Your script only needs to react to the transfer, not to the leave.

## Patterns

**Owner does the work, everyone shows the result.** Physics, timers and game rules run only on the owner. Others read synced variables or receive events.

```csharp
void Update()
{
    if (!IsLocalOwner()) return;
    roundTimer -= Time.deltaTime;
    if (roundTimer <= 0f) EndRound();
}
```

**Request through the owner.** When a non owner needs to change shared state without taking over (adding a point to someone else's scoreboard), send an event to the owner and let the owner apply it:

```csharp
public void AddPoint()
{
    if (IsLocalOwner()) { ApplyPoint(); return; }
    SendCustomNetworkEvent(new byte[] { 1 }, DeliveryMethod.ReliableOrdered, new[] { CurrentOwnerId });
}

public override void OnNetworkMessage(ushort sender, byte[] buffer, DeliveryMethod method)
{
    if (IsLocalOwner() && buffer != null && buffer.Length > 0 && buffer[0] == 1) ApplyPoint();
}
```

**Pickups take ownership automatically.** `BasisPickupSyncNetworking` takes ownership when grabbed. Set `CanNetworkSteal` off to stop other players grabbing an item out of someone's hand.
