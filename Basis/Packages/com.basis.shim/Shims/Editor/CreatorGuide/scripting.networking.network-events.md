A network event is a byte array sent from one player's copy of an object to the same object on other players' machines. It arrives in `OnNetworkMessage` (Native) or the `NetworkMessageReceived` event (Cilbox).

## Sending

```csharp
SendCustomNetworkEvent(byte[] buffer, DeliveryMethod method = DeliveryMethod.Unreliable, ushort[] recipients = null);
```

- `recipients = null` sends to everyone except you.
- Pass an array of player IDs to send to specific players. Include your own ID to have the message loop back to you over the network.
- The object must be network ready (`HasNetworkID` is true). Sending earlier logs an error and does nothing. Send from `OnNetworkReady` or later.

> [!warn]
> Events are **not remembered**. A player who joins after an event was sent never receives it. If the event changes lasting state, also handle [late joiners](/en/docs/scripting/networking/late-joiners).

## Delivery methods

| Method | Arrives? | In order? | Use for |
| --- | --- | --- | --- |
| `Unreliable` | Maybe | No | High rate updates where the next one replaces this one. |
| `Sequenced` | Maybe | Old ones dropped | Latest value wins, for example a live slider. |
| `ReliableUnordered` | Yes | No | Independent one off actions. |
| `ReliableSequenced` | Latest | Yes | A toggle where only the final value matters. |
| `ReliableOrdered` | Yes | Yes | Anything where every message and its order matters: scores, game steps. |

When unsure, use `ReliableOrdered`.

## Receiving

```csharp title="Native"
public override void OnNetworkMessage(ushort playerId, byte[] buffer, DeliveryMethod method)
{
    if (buffer == null || buffer.Length == 0) return;
    switch (buffer[0])
    {
        case 1: Open(); break;
        case 2: Close(); break;
    }
}
```

`playerId` is the sender. Look them up with `BasisNetworkPlayers.GetPlayerById(playerId, out BasisNetworkPlayer player)`.

Always validate the buffer length before reading. Anyone in the instance can send to your object.

## Packing a payload

For one or two values, `BitConverter` is enough. For more, use a `BinaryWriter`. Put a message type byte first so one object can handle several kinds of event.

```csharp
using System.IO;

byte[] PackHit(ushort target, float damage, Vector3 point)
{
    using (var stream = new MemoryStream())
    using (var writer = new BinaryWriter(stream))
    {
        writer.Write((byte)3);
        writer.Write(target);
        writer.Write(damage);
        writer.Write(point.x); writer.Write(point.y); writer.Write(point.z);
        return stream.ToArray();
    }
}

void ReadHit(byte[] buffer)
{
    using (var reader = new BinaryReader(new MemoryStream(buffer)))
    {
        reader.ReadByte();
        ushort target = reader.ReadUInt16();
        float damage = reader.ReadSingle();
        Vector3 point = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
```

Large payloads (a drawing, a level layout) can be compressed with `BasisCompressionShim.Compress` and `Decompress` (LZ4, up to 1 MB unpacked).

## Cilbox

A Cilbox script cannot derive from `BasisNetworkBehaviour`. Get a `BasisNetworkShim` instead; Cilbox adds the component when you call `GetComponent`. Subscribe in `Start` and unsubscribe in `OnDestroy`.

```csharp title="NetworkedCounter.cs (Cilbox)"
using System;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;
using TMPro;
using UnityEngine;

[Cilboxable]
public class NetworkedCounter : MonoBehaviour
{
    public TextMeshPro label;
    BasisNetworkShim net;
    int count;

    void Start()
    {
        net = GetComponent<BasisNetworkShim>();
        net.NetworkMessageReceived += OnMessage;
        net.PlayerJoined += OnPlayerJoined;
    }

    void OnDestroy()
    {
        if (net == null) return;
        net.NetworkMessageReceived -= OnMessage;
        net.PlayerJoined -= OnPlayerJoined;
    }

    public void Increment()
    {
        net.TakeOwnership();
        count++;
        Show();
        net.SendCustomNetworkEvent(BitConverter.GetBytes(count), DeliveryMethod.ReliableOrdered);
    }

    void OnMessage(ushort sender, byte[] buffer, DeliveryMethod method)
    {
        if (buffer == null || buffer.Length < 4) return;
        count = BitConverter.ToInt32(buffer, 0);
        Show();
    }

    void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (net.HasNetworkID && net.IsOwnedLocallyOnServer)
        {
            net.SendCustomNetworkEvent(BitConverter.GetBytes(count), DeliveryMethod.ReliableOrdered, new[] { player.playerId });
        }
    }

    void Show() => label.text = count.ToString();
}
```

The events on `BasisNetworkShim` mirror the overrides on `BasisNetworkBehaviour`:

| `BasisNetworkBehaviour` override | `BasisNetworkShim` event |
| --- | --- |
| `OnNetworkReady()` | `NetworkReady` |
| `OnNetworkMessage(...)` | `NetworkMessageReceived` |
| `OnOwnershipTransfer(...)` | `OwnershipTransfer` |
| `OnServerOwnershipDestroyed()` | `ServerOwnershipDestroyed` |
| `OnPlayerJoined(...)` | `PlayerJoined` |
| `OnPlayerLeft(...)` | `PlayerLeft` |

> [!info]
> `SafeUtil.MakeNetworkable(this)` does the same as `GetComponent<BasisNetworkShim>()` and returns the shim.

## Same result on every client without a message

If every client only needs to agree on a random number, `BasisSharedRandomShim` gives the same value for the same seed and index on every machine:

```csharp title="Cilbox"
using Basis.Shims;

int card = BasisSharedRandomShim.RangeInt(42, round, 0, 52);

// Same weather for everyone, changing every 5 minutes of server time.
int weather = BasisSharedRandomShim.NowRangeInt(7, 300d, 0, 4);
```
