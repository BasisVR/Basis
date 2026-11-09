# Player locks (BasisLockShim)

Status: implemented in `Shims/BasisLockShim.cs`, prompt in `com.basis.framework/BasisUI/BasisPlayerLockRequests.cs`. No tests yet.

## Why

Content often needs to hold the local player still for a moment. A seat in a world, a cutscene, handcuffs, a hug, a minigame that takes over the view. Basis already does this internally through `BasisLocks`, a registry of named owners per lock (movement, look rotation, crouching). The player is locked while any owner is listed. Scripts had no way in.

Letting scripts add owners directly would be unsafe. A script could pick any owner name, so it could also remove locks that seats, menus or moderation put there, and nothing would take its lock away again if it forgot or broke. Avatars and props also run on every client, so someone else's content would be able to freeze you.

## API

```csharp
namespace Basis.Shims
{
    [Flags]
    public enum BasisPlayerLock { None = 0, Movement = 1, LookRotation = 2, Crouching = 4, All = 7 }

    public enum BasisLockResult { Locked, Declined, Blocked, Busy, Unsupported, Throttled }

    public static class BasisLockShim
    {
        public static bool Lock(object self, BasisPlayerLock kinds);
        public static bool Lock(object self, BasisPlayerLock kinds, Action<BasisLockResult> onResult);
        public static bool Unlock(object self, BasisPlayerLock kinds);
        public static void UnlockAll(object self);
        public static bool IsLocking(object self, BasisPlayerLock kinds); // this script holds all of them
        public static bool IsLocked(object self, BasisPlayerLock kinds);  // anyone holds any of them
        public static bool IsGranted(object self, BasisPlayerLock kinds); // this content may lock them now
    }
}
```

Scripts pass `this` as `self`. Kinds can be combined, and a combined call produces a single prompt.

```csharp
BasisLockShim.Lock(this, BasisPlayerLock.Movement | BasisPlayerLock.LookRotation, result =>
{
    if (result == BasisLockResult.Locked) StartCutscene();
});
```

## Ownership

Every script gets its own owner key in `BasisLocks`, built from its proxy. A script can only remove the key it added, so it can never release a lock held by a seat, a menu, the camera, moderation or another script. `Unlock` on something the script never locked does nothing.

## Who can lock

Content the local player is responsible for locks straight away:

- the world,
- props the local player spawned,
- the avatar the local player is wearing.

Content someone else owns has to ask first:

- another player's avatar (avatar scripts run on every client),
- a prop another player spawned.

Content with no owner we can find is refused. So is an avatar script running on a remote avatar that has no linked player.

## Asking

When remote content calls `Lock` without a grant, the local player sees a prompt that names the content, its kind, the player who owns it and the kinds it wants. The accept button turns on after one second so a click already in progress cannot accept it. In VR or Do Not Disturb the prompt goes to notifications instead and waits there.

`Lock` returns false while the prompt is open. The callback, if any, gets the answer later. If the player accepts, the lock is taken for every script that was waiting on that prompt.

A yes is a grant for that one content instance and only for the kinds that were shown. The content can then lock and unlock those kinds without asking again, which is what a hug or a grab interaction needs. Asking for a kind outside the grant opens a new prompt.

Only one prompt is open at a time. A second request from the same content for kinds already being asked about waits on the open prompt. Anything else gets `Busy`.

## Declines count against the player, not the prop

When the local player declines, the strike is recorded against the player who owns the content (their UUID), not against the prop or avatar that asked.

After each decline that player cannot ask again for 30 seconds. After three declines in a session they are muted: every request from any content they own returns `Blocked` without a prompt. Accepting a request from them clears their strikes.

This is deliberate. If strikes were kept per prop, someone could spawn a fresh copy of a prop after every no and keep the prompts coming. Keyed by player, spawning more content, swapping avatars or reloading does not reset anything. Strikes last until the client restarts.

## Getting free

Locks never outlive their script. Every frame, locks are released for any script whose proxy or box has been disabled or destroyed. That covers a prop being despawned, the owner leaving, an avatar swap and a world change.

Respawning from the main menu is the escape hatch. It:

- releases every lock any script holds,
- revokes every grant, so remote content has to ask again,
- cancels an open prompt,
- refuses new locks from any script for 10 seconds.

Only the menu respawn does this. The automatic respawn after falling out of the world does not, so worlds can keep using locks in their own respawn flow. The hook is `BasisSceneFactory.OnUserRespawn`, raised by `RespawnProvider`.

## Throttling

Each script can take a lock at most 6 times in any 2 second window. Past that `Lock` returns `Throttled`. Holding a lock costs nothing, only taking it counts, and `UnlockAll` does not reset the count. This stops content from flickering the player's controls by locking and unlocking every frame.

## Permissions

`BasisLockShim.Lock` is listed under "yourPlayer" in the script consent prompt and marked severe. Avatar boxes whitelist `BasisLockShim`, `BasisPlayerLock` and `BasisLockResult` by name. Prop and world boxes get them through `Basis.Shims.*`.

## Results

| Result | Meaning |
| --- | --- |
| `Locked` | The script now holds every kind it asked for. |
| `Declined` | The player said no, dismissed the notification, or respawned while the prompt was open. |
| `Blocked` | Not allowed: invalid kinds, unknown owner, the owner is muted or cooling down, or the respawn cooldown is running. |
| `Busy` | Another prompt is open. |
| `Unsupported` | Headless server, or the prompt could not be shown. |
| `Throttled` | This script took locks too often in the last 2 seconds. |
