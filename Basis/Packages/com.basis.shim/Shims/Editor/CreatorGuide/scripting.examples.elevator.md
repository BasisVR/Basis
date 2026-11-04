Streaming the platform's position costs bandwidth every frame. An elevator only ever moves toward a floor. Sync the **target floor** and let every client move the platform itself.

## Setup

1. Create the platform with a collider.
2. Add **Elevator** and buttons that call `GoTo(floor)`. Set the floor heights in the inspector.

```csharp title="Elevator.cs (Cilbox)"
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class Elevator : MonoBehaviour
{
    public Transform platform;
    public float[] floorHeights;
    public float speed = 1.5f;

    BasisNetworkShim net;
    int target;

    void Start()
    {
        net = GetComponent<BasisNetworkShim>();
        net.NetworkMessageReceived += OnMessage;
        net.PlayerJoined += OnPlayerJoined;
    }

    public void GoTo(int floor)
    {
        net.TakeOwnership();
        target = floor;
        if (net.HasNetworkID) net.SendCustomNetworkEvent(new[] { (byte)floor }, DeliveryMethod.ReliableOrdered);
    }

    void OnMessage(ushort sender, byte[] data, DeliveryMethod method)
    {
        if (data == null) return;
        if (data.Length >= 1) target = data[0];
        if (data.Length == 5)
        {
            Vector3 p = platform.localPosition;
            platform.localPosition = new Vector3(p.x, BasisBinaryShim.ReadSingle(data, 1), p.z);
        }
    }

    void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (!net.HasNetworkID || !net.IsOwnedLocallyOnServer) return;
        byte[] state = new byte[5];
        state[0] = (byte)target;
        BasisBinaryShim.WriteSingle(state, 1, platform.localPosition.y);
        net.SendCustomNetworkEvent(state, DeliveryMethod.ReliableOrdered, new[] { player.playerId });
    }

    void Update()
    {
        Vector3 p = platform.localPosition;
        float y = Mathf.MoveTowards(p.y, floorHeights[target], speed * Time.deltaTime);
        platform.localPosition = new Vector3(p.x, y, p.z);
    }
}
```

- Every client runs the same motion. Riders see a smooth platform without a position stream.
- A late joiner gets the target floor and the current height in one 5 byte message.
- Note the `new Vector3(...)`: changing `p.y` directly would also change the value it was copied from in Cilbox (see [Sandbox Rules](/en/docs/scripting/sandbox-rules)).
