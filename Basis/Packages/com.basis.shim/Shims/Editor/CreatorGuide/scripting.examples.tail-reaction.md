```csharp title="TailReaction.cs (Cilbox, avatar)"
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class TailReaction : MonoBehaviour
{
    public AudioSource squeak;
    public Animator animator;
    BasisJiggleEventShim jiggle;
    int tail = -1;

    void Start()
    {
        jiggle = GetComponent<BasisJiggleEventShim>();
        tail = jiggle.FindJiggleRig("Tail");
    }

    void OnJiggleGrab(int playerId, bool began, int hand, int rigIndex)
    {
        if (rigIndex != tail) return;
        if (began) squeak.Play();
        animator.SetBool("Wagging", began);
    }

    void OnJiggleTouch(int playerId, bool began, int hand, int rigIndex)
    {
        if (rigIndex == tail && began) animator.SetTrigger("Flinch");
    }
}
```

- Every client that renders the avatar sees the grab. The reaction plays for everyone without networking.
- Detection only runs while a script has opted in. Avatars without it cost nothing.
