Cilbox interprets your script's compiled code inside a sandbox. Most C# works, but these rules catch nearly everyone once.

## Lifecycle

- **No `LateUpdate`, `OnGUI`, `OnAnimatorIK`, `OnParticleCollision` or coroutines.** Use `Update` and [delayed events](/en/docs/scripting/common-usages#timers-and-delayed-events).
- **The first `OnEnable` is dropped.** Set up in `Start`, tear down in `OnDestroy`.
- **Do not derive from `BasisNetworkBehaviour`.** The proxy that replaces your script is not one. Use `BasisNetworkShim` (see [Network Events](/en/docs/scripting/networking/network-events#cilbox)).

## Structs are shared, not copied

Copying a Unity struct (`Vector3`, `Quaternion`, `Color`) into a variable shares the same value. Changing a field on the copy changes the original.

```csharp
// Wrong: also moves `target`.
Vector3 p = target;
p.y = 0f;

// Right: build a new value.
Vector3 p = new Vector3(target.x, 0f, target.z);
```

- `points[i].x = 5f` on an array is silently lost. Write `points[i] = new Vector3(5f, points[i].y, points[i].z)`.
- `points[i] += offset` throws. Write `points[i] = points[i] + offset`.
- Values from Unity calls and operators (`a + b`, `transform.position`, `new Vector3(...)`) are fresh and safe to change.

## Calls and permissions

- **A whitelisted type allows all its public methods** unless that type also has a method restriction. **Fields are denied** unless listed.
- **Parameter, generic and return types are checked too.** A call can pass the name check and still be refused because of one of its types.
- **`this.GetComponent` always works.** `gameObject.GetComponent` is restricted, and the prop sandbox does not allow walking the hierarchy with it.
- **`AddComponent`, `SendMessage` and `BroadcastMessage` are always blocked.** Cilbox adds a `BasisNetworkShim` and the event shims for you on `GetComponent`.
- **`Debug.DrawLine` and `Debug.DrawRay` do not exist** in the sandbox's `Debug`, and referencing them stops every script in the content from loading.
- **Constants are free.** A `const` needs no whitelist entry.

## Events and delegates

Each time you use a method as a delegate it becomes a new wrapper, so `-=` may not remove the one you added. For publishers that outlive your script, poll a value in `Update` instead of subscribing.

## Content police

Any component in your content that is neither `[Cilboxable]` nor on the allowlist is **destroyed on load**. Converting a script and allowing a component are separate. See [Allowed Components](/en/docs/scripting/allowed-components).

## Avatar scripts run on everyone's machine

Every Cilbox script on an avatar runs on every client that loads the avatar, up to the per avatar behaviour limit. Gate wearer only logic on `avatar.IsOwnedLocally` (see [Avatar Scripting](/en/docs/scripting/avatar-scripting)).

## Performance

Interpreted code is much slower than native code. Rough costs on a desktop PC:

| Operation | Cost |
| --- | --- |
| Simple arithmetic on floats | ~15 ns |
| Calling one of your own methods | ~270 ns |
| A `Vector3` operator (`a + b`) | ~1.7 µs, plus garbage |
| A Unity call such as `Mathf.Sqrt` | ~0.9 µs |

So:

- **Do work on events, not every frame.** Check timers, distances and UI a few times a second.
- **Prefer floats over `Vector3` math in loops.** Vector math in a hot loop is about 100x slower than the same math on separate floats.
- **Let components do the heavy lifting.** Animators, particle systems, physics and constraints run natively.
- **Pool objects** instead of calling `Instantiate` every shot.
- **Batch transform work.** `BasisTransformSyncShim` reads, writes or copies whole arrays of transforms in one call, and `BasisBlendShapeSyncShim` does the same for blendshapes.

> [!info]
> **Basis > Tools > Cilbox Permissions** shows each sandbox's whitelist, and its **Script Check** tab scans your scripts for blocked calls before you upload.
