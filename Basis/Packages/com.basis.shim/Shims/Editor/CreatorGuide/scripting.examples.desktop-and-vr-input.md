```csharp title="Blaster.cs (Cilbox, world)"
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class Blaster : MonoBehaviour
{
    public Transform muzzle;
    public AudioSource shot;
    public float cooldown = 0.25f;
    float next;

    void Update()
    {
        bool fire = false;
        if (BasisPlatformShim.IsVR)
        {
            fire = BasisControllerInputShim.GetButtonDown(BasisControllerHand.Right, BasisControllerButton.Trigger);
        }
        else if (BasisDesktopInputShim.HasMouse)
        {
            fire = BasisDesktopInputShim.GetMouseButtonDown(0);
        }
        if (!fire || Time.time < next) return;
        next = Time.time + cooldown;
        shot.Play();
        if (BasisPlatformShim.IsVR) BasisHapticsShim.Play(this, BasisHapticHand.Right, 0.7f, 0.06f);
        if (Physics.Raycast(muzzle.position, muzzle.forward, out RaycastHit hit, 50f)) Debug.Log(hit.collider.name);
    }
}
```

- For a held item, prefer **Basis Pickup Interactable** and `OnPickupUse` ([Pickup Gun](/en/docs/scripting/examples/pickup-gun)), which works the same on every platform.
- Controller input reads as released while the menu is open, so players do not fire while clicking menu buttons.
