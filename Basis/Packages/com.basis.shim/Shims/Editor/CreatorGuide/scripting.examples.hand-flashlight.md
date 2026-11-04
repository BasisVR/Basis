```csharp title="HandFlashlight.cs (Cilbox, world)"
using Basis.Scripts.BasisSdk.Players;
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class HandFlashlight : MonoBehaviour
{
    public Light beam;

    void Start()
    {
        GetComponent<BasisBeforeRenderShim>();
    }

    void Update()
    {
        if (BasisControllerInputShim.GetButtonDown(BasisControllerHand.Right, BasisControllerButton.Secondary))
            beam.enabled = !beam.enabled;
    }

    void OnBeforeRender()
    {
        IBasisPlayer me = BasisPlayersShim.Local;
        if (me == null) return;
        transform.SetPositionAndRotation(me.GetBonePosition(HumanBodyBones.RightHand), me.GetBoneRotation(HumanBodyBones.RightHand));
    }
}
```

- Writing the position in `Update` would trail the hand by one frame in VR, because IK finishes later. `OnBeforeRender` runs after IK.
- The light is local: each player gets their own, and others do not see it.
