`BasisScriptBus` lets scripts in separate content talk to each other on this client, for example a prop telling the world it scored. Worlds, props and avatars can all use it. Nothing goes over the network, and messages arrive on a later frame.

## Listening and sending

```csharp
using Basis.Shims;

void Start()
{
    BasisScriptBus.Listen(this, "score", OnScore);
    BasisScriptBus.Listen(this, "lights", BasisScriptBus.FromWorld, OnLights);
}

void OnScore(BasisScriptMessage message)
{
    int points = message.GetInt(0);
    message.Reply("thanks");
}

void OnLights(BasisScriptMessage message)
{
    lamp.SetActive(message.GetBool(false));
}

void Goal()
{
    BasisScriptBus.Send(this, BasisScriptBus.World, "score", "goal", 3);
    BasisScriptBus.Broadcast(this, "lights", "on", true);
}
```

The `from` filter takes `FromWorld`, `FromProps`, `FromAvatars` or `FromAnyone` (the default), combined with `|`. `Send` and `Broadcast` return the message id, or 0 when nobody is listening or you are over the limit of 120 sends and 1 MB a second. `Broadcast` reaches every listener on the channel except the script that sent it. A reply's `ReplyTo` holds the original `Id`. `BasisScriptBus.Unlisten(this, channel)` stops listening.

A value can be a number, bool, string, byte array, vector, quaternion, colour, matrix, an array of those, a target, a script reference, a shared transform or a shared texture. Read it with the matching `Get` call. The wrong type gives back the fallback, and `ValueType` says which `Type` constant it is.

## BasisScriptTarget

A handle to a world, prop or avatar.

```csharp
BasisScriptTarget world = BasisScriptBus.World;
BasisScriptTarget myAvatar = BasisScriptBus.LocalAvatar;
BasisScriptTarget theirAvatar = BasisScriptBus.AvatarOf(playerId);
BasisScriptTarget me = BasisScriptBus.Self(this);
BasisScriptTarget[] scoreboards = BasisScriptBus.Tagged("scoreboard");
BasisScriptTarget[] scoreListeners = BasisScriptBus.Listening("score");

void OnTriggerEnter(Collider other)
{
    BasisScriptTarget prop = BasisScriptBus.Of(other);
    if (prop == null || !prop.IsValid || prop.Kind != BasisScriptBus.KindProp) return;
    bool spawnedByMe = prop.BelongsToLocalPlayer;
    int owner = prop.OwnerPlayerId;
    if (prop.Listens("score")) BasisScriptBus.Send(this, prop, "score", "bonus", 1);
}
```

`BasisScriptBus.All` lists every content with scripts. Targets also carry `Name`, `NetworkId`, `Position`, `Rotation`, `Tags`, `Keys` and `Channels`.

## Publishing values

Each content can publish up to 64 named values (256 KB in total) that others read whenever they like, with no message. Tags and descriptions help other scripts find you and know what you accept.

```csharp
BasisScriptBus.Publish(this, "team", "red");
BasisScriptBus.Publish(this, "health", 80);
BasisScriptBus.Tag(this, "scoreboard");
BasisScriptBus.Describe(this, "Arena scoreboard");
BasisScriptBus.Describe(this, "score", "Send an int to add points.");

// Reading another content's values
string team = target.GetString("team", "none");
int health = target.GetInt("health", 100);
string help = target.DescriptionOf("score");
```

Tags are stored in lower case and may use letters, digits, `-`, `_` and `.`, up to 32 characters. `Unpublish` and `Untag` remove them again.

## BasisScriptReference

A handle to one script. `BasisScriptBus.SelfScript(this)` returns your own, and every message carries its `SenderScript`. `SendToScript` reaches that one script.

```csharp
void OnScore(BasisScriptMessage message)
{
    BasisScriptReference sender = message.SenderScript;
    if (sender != null && sender.IsValid) BasisScriptBus.SendToScript(this, sender, "score", "ack", 1);
}
```

`ClassName`, `Id` and `Target` describe it. `Resolve(this)` returns the script object only to a caller in the same content.

## BasisSharedTransform

Shares one of your own transforms with other content. Shares are read only unless you pass `writable` as true, and then others can move it, scale it and turn it on or off. Sending a GameObject or Component as a value shares it read only.

```csharp
BasisSharedTransform handle = BasisScriptBus.Share(this, anchor, true);
BasisScriptBus.Publish(this, "anchor", handle);

// In another script
BasisSharedTransform shared = BasisScriptBus.World.GetSharedTransform("anchor");
if (shared != null && shared.Writable) shared.SetPositionAndRotation(spot, Quaternion.identity);
Vector3 at = shared != null ? shared.Position : Vector3.zero;
```

`SetActive` takes effect on the next frame. `BasisScriptBus.Unshare(this, anchor)` takes back every handle to that transform.

## BasisSharedTexture

Shares a `Texture2D` or a 2D `RenderTexture` up to 4096 pixels a side. Other content gets its own copy that Basis keeps up to date, and the owner gets the original.

```csharp
BasisScriptBus.Publish(this, "screen", screenTexture);

// In another script
BasisSharedTexture screen = BasisScriptBus.World.GetSharedTexture("screen");
if (screen != null) display.material.mainTexture = screen.GetTexture(this);
```

`screen.Release(this)` frees your copy. Each content can hold 16 copies and 128 MB of them. `BasisScriptBus.Share(this, texture)` gives a handle without publishing it.

Names go up to 64 characters and text up to 4096. Each content can have 32 listeners and 256 queued messages.
