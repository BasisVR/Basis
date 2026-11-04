Sending the remaining seconds every frame wastes bandwidth and drifts. Send the **end time** once and let every client count down against the shared server clock.

```csharp title="RoundTimer.cs (Cilbox)"
using System;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Shims;
using Cilbox;
using TMPro;
using UnityEngine;

[Cilboxable]
public class RoundTimer : MonoBehaviour
{
    public float roundSeconds = 120f;
    public TextMeshPro label;

    BasisNetworkShim net;
    double endTime;

    void Start()
    {
        net = GetComponent<BasisNetworkShim>();
        net.NetworkMessageReceived += OnMessage;
        net.PlayerJoined += OnPlayerJoined;
    }

    public void StartRound()
    {
        net.TakeOwnership();
        endTime = BasisNetworkTimeShim.ServerTimeSeconds + roundSeconds;
        Send(null);
    }

    void Send(ushort[] to)
    {
        if (net.HasNetworkID) net.SendCustomNetworkEvent(BitConverter.GetBytes(endTime), DeliveryMethod.ReliableOrdered, to);
    }

    void OnMessage(ushort sender, byte[] data, DeliveryMethod method)
    {
        if (data != null && data.Length == 8) endTime = BitConverter.ToDouble(data, 0);
    }

    void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (net.HasNetworkID && net.IsOwnedLocallyOnServer) Send(new[] { player.playerId });
    }

    void Update()
    {
        double left = endTime - BasisNetworkTimeShim.ServerTimeSeconds;
        label.text = left > 0 ? $"{(int)left / 60}:{(int)left % 60:00}" : "Round over";
    }
}
```

- One message per round, however long it lasts.
- Late joiners get the end time and immediately show the right countdown.
