# Hand blocks (BasisHandBlockShim)

Status: implemented in `Shims/BasisHandBlockShim.cs`, on top of `BasisHandBlocks` in `com.basis.framework/Interactions/BasisHandBlocks.cs`. Contact gating has editor tests in `BasisContactSystemTests`; the rest is untested.

## Why

Content that lives on or near a hand needs that hand to stop acting on the world for a while. A wrist or freehand menu is the usual case: while the player works the menu, the same hand keeps pointing at things, grabbing the mirror behind it and pressing world buttons it brushes against.

## API

```csharp
namespace Basis.Shims
{
    [Flags]
    public enum BasisHandBlock { None = 0, Raycast = 1, Pickup = 2, Touch = 4, All = 7 }

    public static class BasisHandBlockShim
    {
        public const int Left = 0, Right = 1, Both = 2;
        public static bool Block(object self, int hand, BasisHandBlock kinds);
        public static bool Unblock(object self, int hand, BasisHandBlock kinds);
        public static bool UnblockAll(object self);
        public static bool IsBlocking(object self, int hand, BasisHandBlock kinds); // this script holds all of them
        public static bool IsBlocked(object self, int hand, BasisHandBlock kinds);  // anyone holds any of them
    }
}
```

Pass `this`. The return value is `false` when the call was refused or the arguments are invalid.

```csharp
void OnMenuOpened() => BasisHandBlockShim.Block(this, BasisHandBlockShim.Right, BasisHandBlock.Pickup | BasisHandBlock.Touch);
void OnMenuClosed() => BasisHandBlockShim.UnblockAll(this);
```

## What each kind stops

| Kind | Stops | Keeps working |
| --- | --- | --- |
| `Raycast` | The hand's pointer: UI ray, ray hover and selection of interactables, prop placement aim. The UI pointer is hidden. | Proximity hover and grabbing, finger touch on UI. |
| `Pickup` | Starting any interaction with an interactable: ray or proximity hover, grip grabs, trigger grabs, jiggle (hair, tail) grabs, and code that hands a spawned prop to that hand. | UI. Whatever the hand already holds stays held until the player lets go. |
| `Touch` | The hand's palm and finger contacts reaching contact receivers in the world and in props. | Receivers on the player's own avatar, the feet, haptics. |

Blocks from every script and every Basis system add up per hand. A hand is blocked for a kind while anyone holds it.

## Hands

`Left` and `Right` are the tracked hands. On desktop the camera pointer counts as the dominant hand, the same way `BasisGrabShim` treats it. `Both` covers both hands in one call.

## Who can block

Worlds, props the local player spawned, and the avatar the local player is wearing. Someone else's avatar or prop gets `false`, with no prompt. A script can only unblock what it blocked itself.

## Lifetime

- Disabling or destroying the script releases everything it holds on the next frame.
- Respawning from the menu releases every script block.
- `UnblockAll` releases both hands and every kind.

## Limits

- `Touch` only covers contact receivers this client evaluates: the world, props, and the local avatar. Other players' avatars evaluate their own receivers on their machines, so a blocked hand still touches them.
- Authored contact senders an avatar puts on its own hands are not affected. Only the built-in hand and finger shapes are.
- Finger touch on UI panels is not affected by any kind.

## From Basis code

`BasisHandBlocks` takes an owner key instead of a script, the same way `BasisLocks` does.

```csharp
BasisHandBlocks.Add("MyMenu", BasisHandBlocks.Right, BasisHandBlockKind.Pickup);
BasisHandBlocks.Remove("MyMenu", BasisHandBlocks.Right, BasisHandBlockKind.Pickup);
BasisHandBlocks.RemoveOwner("MyMenu");
bool blocked = BasisHandBlocks.IsBlocked(input, BasisHandBlockKind.Raycast);
```

Keys are not released for you. Use a key nothing else uses, and remove it when you are done.

## Permissions

`BasisHandBlockShim.Block` is listed under "yourPlayer" in the script consent prompt. Avatar boxes whitelist `BasisHandBlockShim` and `BasisHandBlock` by name. Prop and world boxes get them through `Basis.Shims.*`.
