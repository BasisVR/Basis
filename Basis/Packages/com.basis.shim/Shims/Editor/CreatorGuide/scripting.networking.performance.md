## Keep it cheap

- Send state, not frames. Synced variables only send when values change. Do not call `SendCustomNetworkEvent` from `Update`.
- Use the smallest type. A `Byte` or `Bool` instead of an `Int`, a ranged float instead of a raw one.
- Use unreliable delivery for high rate data. Reliable messages are resent until they arrive, which costs more and adds delay when packets drop.
- Leave `DistanceReduction` on. Far away players get fewer updates. Turn on `RelevanceCulling` for objects nobody outside a room needs to see.
- One object, many values. One `BasisSyncedParameters` with ten values is cheaper than ten objects with one value each, because each object carries its own header and keyframes.
- Batch events. Pack several changes into one message instead of sending one message per change.

> [!info]
> Idle synced objects cost almost nothing: nothing is sent while values are unchanged, and keyframes back off. The cost to avoid is values that change every frame when they do not need to.

## Common sync bugs

| Symptom | Usual cause |
| --- | --- |
| Works for you, nobody else sees it | The change was applied locally and never sent, or the object was not network ready yet (`HasNetworkID` false). |
| Late joiners see the scene default | The state was sent as an event and not re-sent in `OnPlayerJoined`. See [Late Joiners](/en/docs/scripting/networking/late-joiners). |
| Two players fight over a value | Both write it. Only the owner should write; take ownership when a player acts. |
| Value snaps back after you change it | You wrote a synced variable without owning the object, so the owner's stream overwrote it. |
| Everything resets when someone joins | Ownership is taken in `Start` or `OnNetworkReady`, so the newcomer becomes owner with default values. |
| Event handlers fire twice | Subscribed in `Start` and again in `OnEnable`, or never unsubscribed in `OnDestroy`. |

## Tools

- Inspectors in play mode. Synced object, pickup and seat inspectors show the owner, network ID, buffer depth and bytes per second.
- Sync gizmos. Remote synced objects draw where they were and where they are heading and show interpolation and extrapolation as they happen.
- Multiple clients. Build a client and run it next to the editor, both connected to a local server. See [Testing](/en/docs/world/testing).
- `BasisStatsShim` (Cilbox) reports round trip time and seconds since the last packet, useful for showing a "reconnecting" sign.
