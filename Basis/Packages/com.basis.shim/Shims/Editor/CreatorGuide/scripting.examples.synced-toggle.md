This is the smallest complete networked script: one byte of state, sent when it changes and re-sent to anyone who joins.

## Setup

1. Create a world space canvas with a **Button**.
2. Add **SyncedToggle** to the canvas. Assign the button and the object to toggle (a light, a mirror, a door).

## Script

```csharp title="SyncedToggle.cs (Cilbox)"
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;
using UnityEngine;
using UnityEngine.UI;

[Cilboxable]
public class SyncedToggle : MonoBehaviour
{
    public Button button;
    public GameObject target;
    public bool startOn = true;

    BasisNetworkShim net;
    bool on;

    void Start()
    {
        on = startOn;
        Apply();
        button.onClick.AddListener(OnPressed);
        net = GetComponent<BasisNetworkShim>();
        net.NetworkMessageReceived += OnMessage;
        net.PlayerJoined += OnPlayerJoined;
    }

    void OnDestroy()
    {
        button.onClick.RemoveAllListeners();
        if (net == null) return;
        net.NetworkMessageReceived -= OnMessage;
        net.PlayerJoined -= OnPlayerJoined;
    }

    void OnPressed()
    {
        net.TakeOwnership();
        on = !on;
        Apply();
        Send(null);
    }

    void OnMessage(ushort sender, byte[] buffer, DeliveryMethod method)
    {
        if (buffer == null || buffer.Length < 1) return;
        on = buffer[0] != 0;
        Apply();
    }

    void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (net.HasNetworkID && net.IsOwnedLocallyOnServer) Send(new[] { player.playerId });
    }

    void Send(ushort[] recipients)
    {
        if (!net.HasNetworkID) return;
        net.SendCustomNetworkEvent(new[] { on ? (byte)1 : (byte)0 }, DeliveryMethod.ReliableSequenced, recipients);
    }

    void Apply()
    {
        target.SetActive(on);
    }
}
```

## How it works

- Pressing takes ownership, flips the value locally so the presser sees it instantly, and sends the new value to everyone else.
- `ReliableSequenced` guarantees the latest value arrives and drops older ones. A toggle only cares about the latest value.
- A joining player gets the current value from the owner only. Because the message is the whole state, the same `OnMessage` handles both live changes and catch up.

> [!info]
> Native version: derive from `BasisNetworkBehaviour`, replace the shim events with `OnNetworkMessage` and `OnPlayerJoined` overrides, and call `TakeOwnership()` and `SendCustomNetworkEvent` directly.
