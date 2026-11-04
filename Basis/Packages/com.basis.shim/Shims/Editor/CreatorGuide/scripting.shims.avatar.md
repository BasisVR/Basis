## BasisAvatarShim

In the avatar sandbox, `BasisAvatar` is served as `BasisAvatarShim`. Write `BasisAvatar`.

```csharp
using Basis.Scripts.BasisSdk;

BasisAvatar avatar;

void Start()
{
    avatar = BasisAvatar.GetGameObject(this).GetComponent<BasisAvatar>();
    avatar.OnAvatarReady += OnReady;
}

void OnReady(bool isOwner)
{
    if (!isOwner) return;
    Animator animator = avatar.Animator;
    SkinnedMeshRenderer face = avatar.FaceVisemeMesh;
    float scale = avatar.HumanScale;
    if (avatar.TryGetLinkedPlayer(out ushort playerId)) { }
}
```

More: [Avatar Scripting](/en/docs/scripting/avatar-scripting).

## BasisVixxyShim

Reads and drives Vixxy avatar menu items. Values only resolve on the wearer's client.

```csharp
using Basis.Shims;
using HVR.Vixxy;

public HVRVixxyMenuItem glow;

void OnReady(bool isOwner)
{
    if (!isOwner || !BasisVixxyShim.HasControl(glow)) return;

    string title = BasisVixxyShim.Title(glow);
    if (BasisVixxyShim.IsToggle(glow)) glow.ApplyValue(1f);
    if (BasisVixxyShim.IsSlider(glow)) glow.ApplyValue(Mathf.Lerp(BasisVixxyShim.MinValue(glow), BasisVixxyShim.MaxValue(glow), 0.5f));

    for (int i = 0; i < BasisVixxyShim.ChoiceCount(glow); i++)
    {
        Debug.Log($"{BasisVixxyShim.ChoiceTitle(glow, i)} = {BasisVixxyShim.ChoiceValue(glow, i)}");
    }
}
```
