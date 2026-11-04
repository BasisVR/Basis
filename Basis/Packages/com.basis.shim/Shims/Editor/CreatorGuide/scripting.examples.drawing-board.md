```csharp title="DrawingBoard.cs (Cilbox)"
using System.Collections.Generic;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class DrawingBoard : MonoBehaviour
{
    public LineRenderer line;
    public int maxPoints = 4096;

    BasisNetworkShim net;
    readonly List<Vector3> points = new List<Vector3>();

    void Start()
    {
        net = GetComponent<BasisNetworkShim>();
        net.NetworkMessageReceived += OnMessage;
        net.PlayerJoined += OnPlayerJoined;
    }

    public void AddPoint(Vector3 localPoint)
    {
        if (points.Count >= maxPoints) return;
        net.TakeOwnership();
        points.Add(localPoint);
        Redraw();
        byte[] one = new byte[12];
        BasisBinaryShim.WriteVector3(one, 0, localPoint);
        if (net.HasNetworkID) net.SendCustomNetworkEvent(one, DeliveryMethod.ReliableOrdered);
    }

    void OnMessage(ushort sender, byte[] data, DeliveryMethod method)
    {
        if (data == null) return;
        if (data.Length == 12)
        {
            points.Add(BasisBinaryShim.ReadVector3(data, 0));
            Redraw();
            return;
        }
        byte[] all = BasisCompressionShim.Decompress(data);
        if (all == null) return;
        Vector3[] loaded = new Vector3[all.Length / 12];
        BasisBinaryShim.ReadVector3s(all, 0, loaded);
        points.Clear();
        points.AddRange(loaded);
        Redraw();
    }

    void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (points.Count == 0 || !net.HasNetworkID || !net.IsOwnedLocallyOnServer) return;
        byte[] all = new byte[points.Count * 12];
        BasisBinaryShim.WriteVector3s(all, 0, points.ToArray());
        net.SendCustomNetworkEvent(BasisCompressionShim.Compress(all), DeliveryMethod.ReliableOrdered, new[] { player.playerId });
    }

    void Redraw()
    {
        line.positionCount = points.Count;
        line.SetPositions(points.ToArray());
    }
}
```

- Live points are 12 bytes each; the whole drawing for a late joiner is one compressed message.
- A compressed snapshot is told apart from a single point by its length. A real board would add a message type byte.
- The last player to draw owns the board and is the one who answers late joiners.
