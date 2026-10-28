Navigation is available in worlds (not props or avatars).

## Setup

1. Bake a NavMesh for your walkable floors (**Window > AI > Navigation**).
2. Create the NPC with a **NavMeshAgent** and add **NpcFollow**.

## Script

```csharp title="NpcFollow.cs (Cilbox, world)"
using System;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Shims;
using Cilbox;
using UnityEngine;
using UnityEngine.AI;

[Cilboxable]
public class NpcFollow : MonoBehaviour
{
    public NavMeshAgent agent;
    public float stopDistance = 1.5f;

    BasisNetworkShim net;
    float nextPath, nextSend;
    Vector3 remotePosition;
    float remoteYaw;

    void Start()
    {
        net = GetComponent<BasisNetworkShim>();
        net.NetworkMessageReceived += OnMessage;
        net.RequestOwnershipIfNone();
        remotePosition = transform.position;
    }

    void OnDestroy()
    {
        if (net != null) net.NetworkMessageReceived -= OnMessage;
    }

    void Update()
    {
        bool owner = net.HasNetworkID && net.IsOwnedLocallyOnServer;
        agent.enabled = owner;
        if (!owner)
        {
            transform.SetPositionAndRotation(Vector3.Lerp(transform.position, remotePosition, Time.deltaTime * 10f), Quaternion.Euler(0f, remoteYaw, 0f));
            return;
        }
        if (Time.time >= nextPath)
        {
            nextPath = Time.time + 0.5f;
            IBasisPlayer target = BasisPlayersShim.Nearest(transform.position);
            if (target != null)
            {
                Vector3 goal = target.GetPosition();
                if ((goal - transform.position).sqrMagnitude < stopDistance * stopDistance) agent.ResetPath();
                else agent.SetDestination(goal);
            }
        }
        if (Time.time >= nextSend)
        {
            nextSend = Time.time + 0.1f;
            Vector3 p = transform.position;
            byte[] data = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(p.x), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p.y), 0, data, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p.z), 0, data, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(transform.eulerAngles.y), 0, data, 12, 4);
            net.SendCustomNetworkEvent(data, DeliveryMethod.Sequenced);
        }
    }

    void OnMessage(ushort sender, byte[] buffer, DeliveryMethod method)
    {
        if (buffer == null || buffer.Length < 16) return;
        remotePosition = new Vector3(BitConverter.ToSingle(buffer, 0), BitConverter.ToSingle(buffer, 4), BitConverter.ToSingle(buffer, 8));
        remoteYaw = BitConverter.ToSingle(buffer, 12);
    }
}
```

## How it works

- One simulation. Only the owner runs the agent; everyone else disables it and eases toward the received position. Two clients pathing the same NPC would fight.
- `Sequenced` delivery drops late packets. An old position never overrides a newer one. Ten updates a second is plenty with smoothing.
- No late joiner code. The next update arrives within 0.1 seconds.
- Owner changes are free. If the owner leaves, the new owner enables its agent at the last position it received.

> [!info]
> Check whether `Buffer` and `BitConverter` are allowed in your sandbox in **Basis > Tools > Cilbox Permissions**; `BinaryWriter` (see [Network Events](/en/docs/scripting/networking/network-events#packing-a-payload)) works too.
