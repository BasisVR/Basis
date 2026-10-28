A player who joins loads the scene exactly as you built it. Anything that changed since (a door opened, a score went up, a light was turned off) is invisible to them until someone tells them.

## Synced variables: handled for you

`BasisSyncedObject` and everything built on it (`BasisSyncedParameters`, `BasisSyncedTransform`, `BasisSyncedRigidbody`, `BasisPickupSyncNetworking`) send a full keyframe whenever a player joins. If the owner has left and nobody owns the object yet, a player who has the state sends it to the newcomer instead. You do not need to do anything.

## Network events: send the state again

Events are not remembered. If an event changed lasting state, re-send that **state** (not the event history) to the new player. Only the owner does it. The newcomer gets one message instead of one from every player.

```csharp title="Native"
public override void OnPlayerJoined(BasisNetworkPlayer player)
{
    if (!IsLocalOwner()) return;
    SendCustomNetworkEvent(PackState(), DeliveryMethod.ReliableOrdered, new[] { player.playerId });
}
```

```csharp title="Cilbox"
void OnPlayerJoined(BasisNetworkPlayer player)
{
    if (!net.HasNetworkID || !net.IsOwnedLocallyOnServer) return;
    net.SendCustomNetworkEvent(PackState(), DeliveryMethod.ReliableOrdered, new[] { player.playerId });
}
```

Design your messages so applying the full state is the same code path as applying a change. The [synced toggle example](/en/docs/scripting/examples/synced-toggle) does this with one byte.

> [!warn]
> Do not replay a list of past events to a newcomer (for example "door opened, door closed, door opened"). Send the current result. Replays get long, arrive out of date, and break when an event depends on timing.

## Things to check

- Does it work if the owner is not the first player? Test with three clients: A joins, B joins and changes something, A leaves, C joins.
- Is anything set from `Start` that should come from the network? A late joiner's `Start` runs before their first network message. Show a neutral state until the first message or synced value arrives.
- Persistent props. Props spawned from a load config need **Persistent** set so they exist for players who join later. See [Scene Props](/en/docs/world/scene-props).
