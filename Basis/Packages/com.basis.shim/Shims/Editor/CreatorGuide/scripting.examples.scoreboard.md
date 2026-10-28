> This is a native example for client builds: `BasisSyncedParameters` and custom scripts are not on the default allowlist for uploaded content. For a Cilbox scoreboard, use the owner request pattern below with network events, like the [Synced Toggle](/en/docs/scripting/examples/synced-toggle).

A score must be exact and must survive players joining and leaving. Two players adding a point at the same moment must give +2, not +1. This example keeps the score on one owner and routes every change through them.

## Setup

1. Add **Basis Synced Parameters** to the scoreboard object. Add two parameters named `Red` and `Blue`, type **Int**, **Interpolate** off.
2. Add **Scoreboard** to the same object and assign the parameters component and two labels.
3. Hook up buttons (or goal triggers) to `AddRed`, `AddBlue` and `ResetScores`.

## Script

```csharp title="Scoreboard.cs (Native)"
using Basis.Network.Core;
using Basis.Scripts.Networking.Sync;
using TMPro;
using UnityEngine;

public class Scoreboard : MonoBehaviour
{
    const byte AddRedPoint = 1, AddBluePoint = 2, Reset = 3;

    public BasisSyncedParameters sync;
    public TextMeshPro redLabel, blueLabel;

    int redId, blueId, shownRed = -1, shownBlue = -1;

    void Start()
    {
        redId = sync.GetParameterId("Red");
        blueId = sync.GetParameterId("Blue");
    }

    public void AddRed() => Request(AddRedPoint);
    public void AddBlue() => Request(AddBluePoint);
    public void ResetScores() => Request(Reset);

    void Request(byte action)
    {
        if (sync.IsLocalOwner())
        {
            Apply(action);
            return;
        }
        if (!sync.HasNetworkID) return;
        sync.SendCustomNetworkEvent(new[] { action }, DeliveryMethod.ReliableOrdered, new[] { sync.CurrentOwnerId });
    }

    public void OnRequestReceived(byte[] buffer)
    {
        if (!sync.IsLocalOwner() || buffer == null || buffer.Length < 1) return;
        Apply(buffer[0]);
    }

    void Apply(byte action)
    {
        switch (action)
        {
            case AddRedPoint: sync.SetInt(redId, sync.GetInt(redId) + 1); break;
            case AddBluePoint: sync.SetInt(blueId, sync.GetInt(blueId) + 1); break;
            case Reset: sync.SetInt(redId, 0); sync.SetInt(blueId, 0); break;
        }
    }

    void Update()
    {
        int red = sync.GetInt(redId), blue = sync.GetInt(blueId);
        if (red != shownRed) { shownRed = red; redLabel.text = red.ToString(); }
        if (blue != shownBlue) { shownBlue = blue; blueLabel.text = blue.ToString(); }
    }
}
```

`BasisSyncedParameters` already receives network messages for its own stream. Route requests to the scoreboard with a small subclass:

```csharp title="ScoreboardParameters.cs (Native)"
using Basis.Network.Core;
using Basis.Scripts.Networking.Sync;

public class ScoreboardParameters : BasisSyncedParameters
{
    public Scoreboard board;

    public override void OnNetworkMessage(ushort playerId, byte[] buffer, DeliveryMethod method)
    {
        if (buffer != null && buffer.Length == 1)
        {
            board.OnRequestReceived(buffer);
            return;
        }
        base.OnNetworkMessage(playerId, buffer, method);
    }
}
```

Use **ScoreboardParameters** in place of **Basis Synced Parameters** in the setup.

## How it works

- Only the owner writes the score. Simultaneous presses are applied one after another on the owner.
- Everyone else sends a request to `CurrentOwnerId` instead of taking ownership. Taking ownership would let two players each add 1 to the same old value.
- Late joiners need nothing extra. Synced parameters send new players a keyframe.
- If the owner leaves, ownership moves to the longest connected player, who already has the current score.

> [!info]
> Requests are one byte and sync packets are always larger, which is why checking the length is enough here. For more message types, prefix your own messages with a marker byte the sync stream never starts with, or put the request handling on a separate `BasisNetworkBehaviour`.
