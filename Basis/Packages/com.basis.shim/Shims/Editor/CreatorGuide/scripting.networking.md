Everything a script does runs on one player's machine. If a player opens a door, nobody else sees it until you tell the network. This guide covers the tools Basis gives you for that and when to use each one.

For how the transport itself works (server relay, delivery methods, distance based update rates), see [Network Syncing](/en/docs/technical/network-syncing).

## The four ideas

| Idea | What it is | Use it for |
| --- | --- | --- |
| **Synced variables** | Values the owner writes and everyone else reads, sent continuously and interpolated. | Positions, a lever angle, a score, anything that has a current state. |
| **Network events** | One off messages with a byte payload. | "A shot was fired", "the round started", "play this sound". |
| **Ownership** | Exactly one player owns each networked object and is the one allowed to write it. | Deciding who drives a pickup, who keeps the score. |
| **Late joiners** | Players who arrive after things changed. | Making sure they see the current state, not the scene defaults. |

> [!info]
> Rule of thumb: if a player who joins five minutes later needs to see it, it is **state** and belongs in a synced variable (or must be re-sent to them). If it only matters at the moment it happens, it is an **event**.

## The building blocks

- **`BasisNetworkBehaviour`** is the base class for networked scripts compiled into the client. It gets a network ID, an owner, and the callbacks `OnNetworkReady`, `OnNetworkMessage`, `OnOwnershipTransfer`, `OnPlayerJoined` and `OnPlayerLeft`.
- **`BasisNetworkShim`** is the same thing for Cilbox scripts. A Cilbox script cannot derive from `BasisNetworkBehaviour`, so it gets a `BasisNetworkShim` with `GetComponent` and subscribes to its events.
- **`BasisSyncedObject`** builds on `BasisNetworkBehaviour` with a compact, interpolated state stream. **`BasisSyncedParameters`**, **`BasisSyncedTransform`** and **`BasisSyncedRigidbody`** are ready made components on top of it.
- **`BasisPickupSyncNetworking`** and **`BasisSeatSync`** sync pickups and seats with no code.

## A networked script, start to finish

```csharp title="NetworkedCounter.cs (Native)"
using System;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using TMPro;
using UnityEngine;

public class NetworkedCounter : BasisNetworkBehaviour
{
    public TextMeshPro label;
    int count;

    public void Increment()
    {
        TakeOwnership();
        count++;
        Show();
        SendCustomNetworkEvent(BitConverter.GetBytes(count), DeliveryMethod.ReliableOrdered);
    }

    public override void OnNetworkMessage(ushort playerId, byte[] buffer, DeliveryMethod method)
    {
        if (buffer == null || buffer.Length < 4) return;
        count = BitConverter.ToInt32(buffer, 0);
        Show();
    }

    public override void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (IsLocalOwner())
        {
            SendCustomNetworkEvent(BitConverter.GetBytes(count), DeliveryMethod.ReliableOrdered, new[] { player.playerId });
        }
    }

    void Show() => label.text = count.ToString();
}
```

Native scripts like this one work in client builds. In uploaded content, write the same thing as a Cilbox script (below).

What it shows:

1. The player who presses the button **takes ownership**, so there is one source of truth.
2. The new value is **sent as an event** to everyone else (`Recipients = null` means everyone but you).
3. When someone joins, **only the owner** sends them the current value.

The same script for Cilbox is in [Network Events](/en/docs/scripting/networking/network-events#cilbox).

## Pages in this guide

- [Network Events](/en/docs/scripting/networking/network-events): sending messages, recipients, delivery methods, payloads.
- [Synced Variables](/en/docs/scripting/networking/synced-variables): `BasisSyncedParameters` and custom `BasisSyncedObject` fields.
- [Ownership](/en/docs/scripting/networking/ownership): who owns what, taking and checking ownership, what happens when the owner leaves.
- [Late Joiners](/en/docs/scripting/networking/late-joiners): getting new players up to date.
- [Network Components](/en/docs/scripting/networking/network-components): the no code sync components.
- [Performance and Debugging](/en/docs/scripting/networking/performance): bandwidth, send rates and finding sync bugs.
