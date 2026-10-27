Pointing works everywhere, but in VR it feels better to press a button with your finger. This reads the local player's hand positions and fires when one comes close.

```csharp title="TouchButton.cs (Cilbox)"
using Basis.Scripts.BasisSdk.Players;
using Basis.Shims;
using Cilbox;
using UnityEngine;
using UnityEngine.Events;

[Cilboxable]
public class TouchButton : MonoBehaviour
{
    public float radius = 0.06f;
    public float cooldown = 0.5f;
    public AudioSource click;
    public GameObject target;

    bool touching;
    float nextPress;

    void Update()
    {
        IBasisPlayer local = BasisPlayersShim.Local;
        if (local == null) return;
        Vector3 center = transform.position;
        float left = (local.GetBonePosition(HumanBodyBones.LeftIndexDistal) - center).sqrMagnitude;
        float right = (local.GetBonePosition(HumanBodyBones.RightIndexDistal) - center).sqrMagnitude;
        bool now = Mathf.Min(left, right) < radius * radius;
        if (now && !touching && Time.time >= nextPress)
        {
            nextPress = Time.time + cooldown;
            Press();
        }
        touching = now;
    }

    void Press()
    {
        click.Play();
        target.SetActive(!target.activeSelf);
        BasisHapticsShim.Play(this, BasisHapticHand.Both, 0.5f, 0.05f);
    }
}
```

- Fingertip bones need an avatar with finger bones. Use `LeftHand` and `RightHand` with a larger radius for a palm press.
- `touching` makes it fire once per touch instead of every frame.
- To share the result, send it as a [network event](/en/docs/scripting/examples/synced-toggle) from `Press`.
