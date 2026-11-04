# Cross-script communication (BasisScriptBus)

Status: implemented in `Shims/BasisScriptBusShim.cs`, tests in `Tests/Editor/BasisScriptBusShimTests.cs`.

## Why

Every piece of content with Cilbox scripts gets its own box: the world, each prop instance, each avatar. Scripts inside one box already talk to each other with plain C# calls, because they share an assembly. Scripts in different boxes had no supported way to reach each other. The only route was `GetComponent` on another content's proxy, which matches by class name, ignores which box the proxy belongs to, and hands over a live object instead of a copy.

Content needs this for ordinary things. A world scoreboard wants to hear from a ball prop. A prop wants to know whether it is sitting in a football world. A world wants to tell the local avatar to switch outfits for a race. A prop wants to know who spawned the other prop it just hit, or send it a recorded path.

## What other platforms do

- VRChat Udon: `SendCustomEvent(name)` plus `GetProgramVariable`/`SetProgramVariable` on a referenced behaviour. Simple, but the caller needs a direct object reference, writes go straight into the other program's state, and there is no identity on the call.
- Resonite: dynamic impulses (a tag broadcast down a hierarchy) and dynamic variables (typed values published into a named space that others read). Loose coupling, no direct references, the publisher owns its values.
- Browsers: `window.postMessage`. Messages are copied (structured clone), delivered asynchronously, and carry an origin the receiver checks. The receiver decides what to trust.

The bus takes the Resonite split between events and published values, and the browser rules for safety: copy everything, deliver later, attach an identity the sender cannot forge.

## Model

The unit of addressing is the content instance: one world, one prop instance, one avatar. Scripts never get a reference to another box's objects. They get a `BasisScriptTarget`, an opaque handle owned by Basis.

There are four parts.

1. Metadata. Read-only facts Basis fills in: kind, name, owner, network id, root pose.
2. Labels. Tags and descriptions the content sets about itself, so people reading code or debug output, and other scripts, can tell what it is and what its channels and keys mean.
3. Published values. Each content can publish up to 64 named values. Anyone can read them at any time without waking the publisher's script.
4. Messages. A script listens on a channel. Others send to a target or broadcast to every listener on the channel. Delivery happens on the next frame tick. A message carries a text string and optionally one typed value.

Everything is local to this client. Nothing here goes over the network; `BasisNetworkShim` stays the way to reach other players. Because each client runs every avatar and prop, the same messages happen on every client that runs the same content, which is usually what you want.

## API

```csharp
namespace Basis.Shims
{
    public static class BasisScriptBus
    {
        public const int KindWorld = 0, KindProp = 1, KindAvatar = 2;
        public const int FromWorld = 1, FromProps = 2, FromAvatars = 4, FromAnyone = 7;
        public const int TypeNone = 0, TypeText = 1, TypeNumber = 2, TypeBool = 3, TypeBytes = 4, TypeVector2 = 5, TypeVector3 = 6, TypeVector4 = 7, TypeQuaternion = 8, TypeColor = 9, TypeInts = 10, TypeFloats = 11, TypeStrings = 12, TypeVector3s = 13;

        // Finding content
        public static BasisScriptTarget Self(object self);
        public static BasisScriptTarget World { get; }
        public static BasisScriptTarget LocalAvatar { get; }
        public static BasisScriptTarget Of(GameObject gameObject);
        public static BasisScriptTarget Of(Component component);
        public static BasisScriptTarget AvatarOf(ushort playerId);
        public static BasisScriptTarget[] All { get; }
        public static BasisScriptTarget[] Listening(string channel);
        public static BasisScriptTarget[] Tagged(string tag);

        // Messages
        public static bool Listen(object self, string channel, Action<BasisScriptMessage> handler);
        public static bool Listen(object self, string channel, int from, Action<BasisScriptMessage> handler);
        public static void Unlisten(object self, string channel);
        public static int Send(object self, BasisScriptTarget target, string channel, string text);
        public static int Send(object self, BasisScriptTarget target, string channel, string text, object value);
        public static int Broadcast(object self, string channel, string text);
        public static int Broadcast(object self, string channel, string text, object value);

        // Published values
        public static bool Publish(object self, string key, object value);
        public static void Unpublish(object self, string key);

        // Labels
        public static bool Tag(object self, string tag);
        public static bool Untag(object self, string tag);
        public static bool Describe(object self, string description);
        public static bool Describe(object self, string name, string description);
    }

    public sealed class BasisScriptTarget
    {
        public bool IsValid { get; }
        public int Kind { get; }
        public string Name { get; }
        public bool HasScripts { get; }
        public bool BelongsToLocalPlayer { get; }
        public int OwnerPlayerId { get; }
        public string NetworkId { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }

        public string Description { get; }
        public string[] Tags { get; }
        public string[] Channels { get; }
        public string[] Keys { get; }
        public bool HasTag(string tag);
        public string DescriptionOf(string name);
        public bool Listens(string channel);

        public bool HasValue(string key);
        public int TypeOf(string key);
        public string GetString(string key, string fallback);
        public float GetFloat(string key, float fallback);
        public int GetInt(string key, int fallback);
        public bool GetBool(string key, bool fallback);
        public Vector2 GetVector2(string key, Vector2 fallback);
        public Vector3 GetVector3(string key, Vector3 fallback);
        public Vector4 GetVector4(string key, Vector4 fallback);
        public Quaternion GetQuaternion(string key, Quaternion fallback);
        public Color GetColor(string key, Color fallback);
        public byte[] GetBytes(string key);
        public int[] GetInts(string key);
        public float[] GetFloats(string key);
        public string[] GetStrings(string key);
        public Vector3[] GetVector3s(string key);
    }

    public sealed class BasisScriptMessage
    {
        public int Id { get; }
        public int ReplyTo { get; }
        public string Channel { get; }
        public string Text { get; }
        public BasisScriptTarget Sender { get; }
        public int ValueType { get; }
        public byte[] Data { get; }                 // same as GetBytes()
        public string GetString(string fallback);
        public float GetFloat(float fallback);
        public int GetInt(int fallback);
        public bool GetBool(bool fallback);
        public Vector2 GetVector2(Vector2 fallback);
        public Vector3 GetVector3(Vector3 fallback);
        public Vector4 GetVector4(Vector4 fallback);
        public Quaternion GetQuaternion(Quaternion fallback);
        public Color GetColor(Color fallback);
        public byte[] GetBytes();
        public int[] GetInts();
        public float[] GetFloats();
        public string[] GetStrings();
        public Vector3[] GetVector3s();
        public int Reply(string text);
        public int Reply(string text, object value);
    }
}
```

`self` is always `this`, the calling script.

### Targets

There is one handle per content instance, so `message.Sender == BasisScriptBus.World` works with plain reference equality. A handle stays valid until its content is destroyed. After that `IsValid` is false, getters return defaults and sends to it return 0. Ask for a fresh handle rather than caching one across world loads.

`Of` resolves the content that owns a GameObject, the same way the rest of Basis does (nearest `BasisContentBase` parent, then the scene's `BasisScene`). It works for content without scripts too, which is useful for metadata.

| Member | World | Prop | Avatar |
| --- | --- | --- | --- |
| `Kind` | `KindWorld` | `KindProp` | `KindAvatar` |
| `Name` | bundle name, else scene name | bundle name | bundle name |
| `BelongsToLocalPlayer` | false | spawned by the local player | worn by the local player |
| `OwnerPlayerId` | -1 | spawner's player id, -1 if unknown or gone | wearer's player id |
| `NetworkId` | load id, may be empty | `LoadedNetID` | load id, may be empty |
| `Position`, `Rotation` | content root | content root | content root |

Creator UUIDs stay out on purpose. They are prop-box only elsewhere in the shims, and the player id covers what content needs.

### Values

Published values and message values share one set of types. Anything else is refused, and so is the whole call it came with.

| Type constant | C# types accepted | Read with |
| --- | --- | --- |
| `TypeText` | `string` (up to 4096 chars) | `GetString` |
| `TypeNumber` | any primitive number (`int`, `float`, `double`, `long`, `byte`, ...) | `GetFloat`, `GetInt` (rounds), `GetString` |
| `TypeBool` | `bool` | `GetBool`, also `GetInt` and `GetFloat` as 0 or 1 |
| `TypeBytes` | `byte[]` (up to 65536 bytes) | `GetBytes`, `Data` |
| `TypeVector2`, `TypeVector3`, `TypeVector4` | `Vector2`, `Vector3`, `Vector4` | matching getter |
| `TypeQuaternion` | `Quaternion` | `GetQuaternion` |
| `TypeColor` | `Color` | `GetColor` |
| `TypeInts` | `int[]` (up to 4096) | `GetInts` |
| `TypeFloats` | `float[]` (up to 4096) | `GetFloats` |
| `TypeStrings` | `string[]` (up to 4096, 32768 chars in total, null becomes "") | `GetStrings` |
| `TypeVector3s` | `Vector3[]` (up to 4096) | `GetVector3s` |

NaN and infinity are refused everywhere. A getter for the wrong type returns its fallback, or null for arrays. Numbers come back as `GetString` in invariant culture. Use `TypeOf` or `ValueType` when a script needs to branch on what arrived.

Values are copied on the way in, so changing your array after `Publish` or `Send` changes nothing. Arrays are copied on the way out of a published value, so one reader cannot spoil it for the next. A message's arrays belong to that receiver alone. For anything richer, pack it into `byte[]` with `BasisBinaryShim`.

### Labels

Tags say what a content is. `Tag(this, "ball")`, then anyone can call `BasisScriptBus.Tagged("ball")` or `target.HasTag("ball")`. Tags are 1 to 32 characters from `a-z`, `0-9`, `.`, `-`, `_`, stored lower case, so `Ball` and `ball` are the same tag. Up to 16 per content, returned sorted. Dotted names (`sport.football`, `vehicle.car`) work well for grouping. There is no central list. Pick names other creators will guess.

Descriptions say what a content, a channel or a key is for, in a sentence a person can read. `Describe(this, "A football. Kick it into a goal.")` describes the content. `Describe(this, "goal", "Sent to the world when the ball crosses a line. Text is the team.")` describes a channel or published key by name. Readers use `target.Description` and `target.DescriptionOf(name)`. Descriptions are up to 256 characters; `<` and `>` are swapped for look-alike characters, control and bidi characters are removed, the same cleaning as menu text. An empty description removes it. Up to 96 named descriptions per content.

`target.Channels` lists the channels a content listens on and `target.Keys` the keys it publishes. Together with tags and descriptions, a debug view or another creator can see what a content offers without reading its source.

Labels are hints. They are set by the content itself and prove nothing about it, so a script should still check `Kind`, `OwnerPlayerId` and the values it receives.

### Published values

`Publish` writes into the caller's own content. Nobody can write into someone else's. Keys are 1 to 64 characters with no control characters. 64 keys and 256 KB of values per content. Publishing an existing key replaces it; `Unpublish` removes it.

Values die with the content. Use `BasisSaveShim` for anything that must survive.

### Messages

`Listen` registers the calling script for a channel. A script has one handler per channel; calling `Listen` again replaces it. `from` is a mask of `FromWorld`, `FromProps` and `FromAvatars`, checked by Basis before any interpreted code runs, so content can ignore whole kinds of sender for free. A content may hold 32 listeners in total.

`Send` delivers to every listener the target has on the channel. `Broadcast` delivers to every listener on the channel except the calling script itself. Other scripts in the same content still hear it, since a world can carry several boxes.

Both return a message id, or 0 when nothing was queued: refused call, bad channel, unsupported or oversized value, no listener with room, or a rate limit hit. A 0 return is the way to find out nobody is listening.

`Reply` sends back to `Sender` on the same channel with `ReplyTo` set to the original `Id`. It only works while the receiving script is running, which is always true inside the handler.

Delivery rules:

- Never inside the sender's call. Messages queue and arrive on the next `BasisFrameClock` tick, in send order. A message sent from inside a handler arrives on the tick after.
- At most 1024 deliveries per tick. Leftovers go first next tick.
- `Text` is never null on delivery. `ValueType` is `TypeNone` when no value was sent.
- A handler that throws gets its script disabled by the interpreter, same as any other callback. The bus logs it and carries on with the next message.
- Listeners whose script is destroyed or disabled, or whose box is disabled (consent revoked, fault), are dropped and get nothing more.

### Limits

| Limit | Value |
| --- | --- |
| Channel and key length | 1 to 64, no control characters |
| Text | 4096 characters |
| `byte[]` | 65536 bytes |
| Arrays | 4096 elements |
| Published values per content | 64 keys, 256 KB |
| Tags per content | 16, each 1 to 32 characters |
| Descriptions | 256 characters, 96 named per content |
| Listeners per content | 32 |
| Queued messages per receiving content | 256, extra messages to that content are dropped |
| Sends per content | 120 per second, broadcast counts once |
| Bytes per content | 1 MB per second, counted per receiver |

The interpreter's own per-frame time budget still applies to handlers.

## Rules the shim enforces

- Only a running script can call `Self`, `Listen`, `Unlisten`, `Send`, `Broadcast`, `Publish`, `Unpublish`, `Tag`, `Untag` and `Describe` (its box must be inside interpreted code). This is the same check `BasisMenuShim` uses.
- The handler must be a method of the calling script's own box. Host delegates and methods from another box are refused, so a script cannot get another box's code run under its listener.
- The sender identity is attached by Basis from the calling box. Scripts cannot construct a `BasisScriptTarget` or `BasisScriptMessage`, and every setter and internal member is a floor deny in `CilboxBasisCommon.CheckMethodAllowed` that consent cannot unlock.
- Only the value types listed above, strings and Basis handles cross the boundary. No interpreted object, Transform, component or other Unity object from one box ever reaches another.
- No consent prompt. The bus reaches nothing outside the content that chose to listen or publish, so it sits in the default sandbox for worlds, props and avatars.

## What receivers should check

Any running content can send to any listener, including avatars worn by other players and props spawned by them. Treat messages like network input:

- Filter with `from` first, then check `message.Sender` (`Kind`, `OwnerPlayerId`, `BelongsToLocalPlayer`) before acting. Tags and descriptions are the sender's own claims.
- Check `ValueType` and array lengths before using a value.
- Do not act on behalf of the local player from a message alone. Anything that needs a press should still go through the shims that check for one.

## Example

```csharp
using Basis.Shims;

// Ball prop
void Start()
{
    BasisScriptBus.Tag(this, "ball");
    BasisScriptBus.Describe(this, "A football. Kick it into a goal.");
    BasisScriptBus.Describe(this, "goal", "Sent to the world when the ball crosses a line. Text is the team, value is the hit point.");
    BasisScriptBus.Listen(this, "goal", BasisScriptBus.FromWorld, OnAnswer);
}

void Score(Vector3 where)
{
    BasisScriptTarget world = BasisScriptBus.World;
    if (world != null && world.HasTag("sport.football")) BasisScriptBus.Send(this, world, "goal", "blue", where);
}

void OnAnswer(BasisScriptMessage message) { }

// World
void Start()
{
    BasisScriptBus.Tag(this, "sport.football");
    BasisScriptBus.Listen(this, "goal", BasisScriptBus.FromProps, OnGoal);
}

void OnGoal(BasisScriptMessage message)
{
    if (!message.Sender.HasTag("ball") || message.ValueType != BasisScriptBus.TypeVector3) return;
    Vector3 where = message.GetVector3(Vector3.zero);
    message.Reply("counted");
}
```

## Box whitelists

Prop and scene boxes already allow `Basis.Shims.*`. The avatar box enumerates its shims, so `BasisScriptBus`, `BasisScriptTarget` and `BasisScriptMessage` are listed in `CilboxAvatarBasis`. `System.Action`1` is already in the common whitelist.

## Not covered

- Networking. Use `BasisNetworkShim`; a script can relay what it hears.
- Synchronous calls into another box. The bus is asynchronous on purpose: it keeps one box's time budget and faults from landing in another, and it stops two boxes recursing into each other inside one frame.
- Addressing a single script inside another content. Channels do that job.
- A shared tag registry or a debug panel listing labels. The data is all there for one.

## Known gap outside the bus

Cilbox's `GetComponent<T>` override for interpreted types matches proxies by class name without checking the proxy's box (`CilboxUsage.OverrideGetComponentC`). Two contents that both define a class with the same name can reach each other's proxies that way. The package is upstream code, so the fix belongs in a cilbox PR (compare `p.box` with the caller's box).
