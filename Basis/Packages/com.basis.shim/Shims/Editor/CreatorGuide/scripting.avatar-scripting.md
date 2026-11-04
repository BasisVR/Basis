An avatar's Cilbox scripts run on **every client** that loads the avatar, not just the wearer's. Most avatar logic should check who is running it.

## The avatar component

Inside the avatar sandbox, `BasisAvatar` resolves to `BasisAvatarShim`. Write `BasisAvatar` as usual.

```csharp title="AvatarGlow.cs (Cilbox, avatar)"
using Basis.Scripts.BasisSdk;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class AvatarGlow : MonoBehaviour
{
    public GameObject glow;
    BasisAvatar avatar;

    void Start()
    {
        avatar = BasisAvatar.GetGameObject(this).GetComponent<BasisAvatar>();
        avatar.OnAvatarReady += OnReady;
    }

    void OnReady(bool isOwner)
    {
        glow.SetActive(isOwner);
    }
}
```

| Member | Use |
| --- | --- |
| `OnAvatarReady(bool isOwner)` | Fires once the avatar is set up. `isOwner` is true on the wearer's client. |
| `IsOwnedLocally` | True on the wearer's client. |
| `Animator`, `FaceVisemeMesh`, `HumanScale` | Read the avatar's rig. |
| `TryGetLinkedPlayer(out ushort playerId)` | The wearer's player ID. |
| `PlayspaceInput` | Drive the wearer's own movement (wearer only). |

## Wearer only vs everyone

| Run on | For |
| --- | --- |
| **Wearer only** (`IsOwnedLocally`) | Menus, haptics, saves, input, sending network events. |
| **Everyone** | Visual effects that follow synced state: particles, materials, sounds. |

```csharp
void Update()
{
    if (!avatar.IsOwnedLocally) return;
    // wearer's input and decisions here
}
```

## Syncing avatar state

- Custom values: get a `BasisNetworkShim` and send [network events](/en/docs/scripting/networking/network-events#cilbox) from the wearer, then apply them on every client.
- Moving parts: `BasisSyncedTransform` is allowed on avatars.

## Settings tab on an avatar

```csharp
void OnReady(bool isOwner)
{
    if (!isOwner) return;
    BasisMenuPage page = BasisMenuShim.AddSettingsTab(this, "My Avatar");
    page?.AddToggle("Glow", true, on => glow.SetActive(on));
}
```

> [!warn]
> Anything the avatar sandbox allows, a stranger's avatar can do on your machine. That is why the avatar sandbox is the strictest of the three: no cameras, no lights, no web requests, and haptics only reach the wearer.
