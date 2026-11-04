## Setup

1. Model or import a gun. Add a collider and a **Rigidbody**.
2. Add **Basis Pickup Interactable**.
3. Add **Basis Pickup Sync Networking** and assign the pickup. This syncs the gun's position and who holds it.
4. Add an **AudioSource** and a **ParticleSystem** for the muzzle flash, and a child transform at the muzzle.
5. Add **PickupGun** and assign everything.

## Script

```csharp title="PickupGun.cs (Cilbox)"
using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class PickupGun : MonoBehaviour
{
    public BasisPickupInteractable pickup;
    public Transform muzzle;
    public AudioSource shotSound;
    public ParticleSystem flash;
    public float range = 50f;
    public float cooldown = 0.2f;

    BasisNetworkShim net;
    bool held;
    float nextShot;

    void Start()
    {
        net = GetComponent<BasisNetworkShim>();
        net.NetworkMessageReceived += OnMessage;
        pickup.OnInteractStartEvent.AddListener(OnGrab);
        pickup.OnInteractEndEvent.AddListener(OnDrop);
        pickup.OnPickupUse.AddListener(OnUse);
    }

    void OnDestroy()
    {
        if (net != null) net.NetworkMessageReceived -= OnMessage;
    }

    void OnGrab(BasisInput input) { held = true; }
    void OnDrop(BasisInput input) { held = false; }

    void OnUse(BasisPickUpUseMode mode)
    {
        if (!held || mode != BasisPickUpUseMode.OnPickUpUseDown || Time.time < nextShot) return;
        nextShot = Time.time + cooldown;

        PlayShot();
        BasisHapticsShim.Play(this, BasisHapticHand.Both, 0.6f, 0.08f);
        if (net.HasNetworkID) net.SendCustomNetworkEvent(new byte[] { 1 }, DeliveryMethod.ReliableUnordered);

        if (Physics.Raycast(muzzle.position, muzzle.forward, out RaycastHit hit, range))
        {
            Debug.Log($"hit {hit.collider.name}");
        }
    }

    void OnMessage(ushort sender, byte[] buffer, DeliveryMethod method)
    {
        if (buffer != null && buffer.Length == 1 && buffer[0] == 1) PlayShot();
    }

    void PlayShot()
    {
        shotSound.Play();
        flash.Play();
    }
}
```

## How it works

- **Basis Pickup Sync Networking** takes ownership when the gun is grabbed and streams its pose. Everyone sees it in the holder's hand without any code.
- The shot is an event, not state. Nobody who joins later needs to know a shot happened, and there is no late joiner code.
- Only the shooter raycasts. Hit results that matter for gameplay (damage, score) should be decided by the shooter and sent as their own event, or routed to an owner like in the [Scoreboard](/en/docs/scripting/examples/scoreboard).

> [!info]
> Set **CanNetworkSteal** off on the sync component if other players should not be able to pull the gun out of someone's hand.
