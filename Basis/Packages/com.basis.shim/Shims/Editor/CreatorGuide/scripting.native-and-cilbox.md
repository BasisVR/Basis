Basis runs code in two ways. Which one you use depends on **where the script lives**: inside the application you build, or inside content that is loaded at runtime.

## Native: compiled into the application

A native script is an ordinary Unity `MonoBehaviour` that is built into the Basis client (or your own app built on the Basis framework). It is compiled with the application and ships with the next client build, not with a world or prop.

- Full C# and the full Unity and Basis API.
- Full native speed.
- Can derive from Basis classes such as `BasisNetworkBehaviour` and `BasisSyncedObject`.
- Only exists in clients built with it. Players on the public client do not have your script.

Use native scripts when you are **building an application or game on Basis**: your own menus, game rules, systems and any component your content relies on.

## Cilbox: runtime scripting inside content

A Cilbox script is a `MonoBehaviour` marked `[Cilboxable]` that ships **inside** a world, prop or avatar `.bee` file. When the content loads, Basis runs it in the Cilbox interpreter, inside a sandbox for that kind of content (Scene, Prop or Avatar).

- Works on any Basis client that loads the content. No client build needed.
- Sandboxed. Only whitelisted types and methods can be called, which keeps content from harming the player's machine.
- Slower than native, because the code is interpreted.
- Uses shims instead of base classes, for example `BasisNetworkShim` instead of deriving from `BasisNetworkBehaviour`.

Use Cilbox when you are **making user content**: worlds, props and avatars that other people load.

## At a glance

| | Native | Cilbox |
| --- | --- | --- |
| Lives in | The application build | The world, prop or avatar |
| Loaded | When the app is built | At runtime, with the content |
| Marked with | Nothing | `[Cilboxable]` |
| API | Everything | Whitelisted per sandbox |
| Speed | Native | Interpreted, much slower |
| Networking | Derive from `BasisNetworkBehaviour` | Use `BasisNetworkShim` |
| Who has it | Players on your build | Anyone who loads the content |

## The same script both ways

```csharp title="Native"
using Basis;
using Basis.Network.Core;

public class Door : BasisNetworkBehaviour
{
    public override void OnNetworkMessage(ushort sender, byte[] buffer, DeliveryMethod method)
    {
        gameObject.SetActive(buffer[0] != 0);
    }
}
```

```csharp title="Cilbox"
using Basis;
using Basis.Network.Core;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class Door : MonoBehaviour
{
    BasisNetworkShim net;

    void Start()
    {
        net = GetComponent<BasisNetworkShim>();
        net.NetworkMessageReceived += OnMessage;
    }

    void OnMessage(ushort sender, byte[] buffer, DeliveryMethod method)
    {
        gameObject.SetActive(buffer[0] != 0);
    }
}
```

## Shipping each one

- Native: add the script to your Unity project and build the client. If a native component is placed inside a world or prop bundle, also add its type to the matching content police selector, or it is removed when the content loads (see [Allowed Components](/en/docs/scripting/allowed-components)).
- Cilbox: add a **Cilbox Scene Basis** (world) or **Cilbox Prop Basis** (prop) to your content, mark scripts `[Cilboxable]`, and build the `.bee` as usual. See [Basics](/en/docs/scripting/examples/basics).

> [!info]
> Examples across these docs are labelled **Native** or **Cilbox**. Most Cilbox examples also compile as native scripts; native examples usually need changes to run in Cilbox. The rules to watch for are in [Sandbox Rules](/en/docs/scripting/sandbox-rules).
