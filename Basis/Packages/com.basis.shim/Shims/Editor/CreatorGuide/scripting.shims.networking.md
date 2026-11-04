## BasisNetworkShim

The Cilbox way to send network events and track ownership. Full guide: [Networking](/en/docs/scripting/networking).

```csharp
using Basis;
using Basis.Network.Core;

BasisNetworkShim net;

void Start()
{
    net = GetComponent<BasisNetworkShim>();
    net.NetworkReady += () => Debug.Log("ready");
    net.NetworkMessageReceived += OnMessage;
    net.OwnershipTransfer += owner => Debug.Log($"owner {owner.playerId}");
    net.PlayerJoined += player => { };
    net.PlayerLeft += player => { };
}

void Send() => net.SendCustomNetworkEvent(new byte[] { 1 }, DeliveryMethod.ReliableOrdered);
void OnMessage(ushort sender, byte[] data, DeliveryMethod method) { }
void Grab() => net.TakeOwnership();
bool Mine => net.HasNetworkID && net.IsOwnedLocallyOnServer;
```

Delayed callbacks replace coroutines: `net.SendCustomEventDelayedSeconds(Close, 3f)` and `net.SendCustomEventDelayedFrames(Rebuild, 2)`.

## BasisNetworkTimeShim

The server's clock, the same on every client (within network latency). Use it for countdowns and day cycles that everyone sees in step.

```csharp
using Basis.Shims;

double now = BasisNetworkTimeShim.ServerTimeSeconds;
System.DateTime utc = BasisNetworkTimeShim.ServerUtcNow;

// A 24 minute day cycle that matches for everyone.
float dayFraction = (float)(now % 1440.0 / 1440.0);
sun.transform.rotation = Quaternion.Euler(dayFraction * 360f - 90f, 30f, 0f);
```

## BasisSharedRandomShim

The same seed and index give the same number on every client, with no message.

```csharp
float roll = BasisSharedRandomShim.Value(42, round);
float speed = BasisSharedRandomShim.Range(42, round, 2f, 5f);
int card = BasisSharedRandomShim.RangeInt(42, round, 0, 52);

// Index taken from the server clock: a new value every 5 minutes, the same for everyone.
int weather = BasisSharedRandomShim.NowRangeInt(7, 300d, 0, 4);
long window = BasisSharedRandomShim.WindowIndex(300d);
```

## BasisBinaryShim

Packs numbers and vectors into a byte array in one call, much faster than `BinaryWriter` in the sandbox. Each `Write` returns the next offset.

```csharp
byte[] buffer = new byte[4 + 12 + 16];
int at = BasisBinaryShim.WriteInt32(buffer, 0, score);
at = BasisBinaryShim.WriteVector3(buffer, at, transform.position);
at = BasisBinaryShim.WriteQuaternion(buffer, at, transform.rotation);
net.SendCustomNetworkEvent(buffer, DeliveryMethod.Sequenced);

// Receiving
int s = BasisBinaryShim.ReadInt32(data, 0);
Vector3 p = BasisBinaryShim.ReadVector3(data, 4);
Quaternion r = BasisBinaryShim.ReadQuaternion(data, 16);
```

Array versions (`WriteSingles`, `WriteVector3s`, `ReadQuaternions` and so on) pack many values at once.

## BasisCompressionShim

LZ4 compression. Data that does not shrink costs one extra byte. `Decompress` returns null for anything malformed or larger than 1 MB unpacked.

```csharp
net.SendCustomNetworkEvent(BasisCompressionShim.Compress(drawing), DeliveryMethod.ReliableOrdered);

void OnMessage(ushort sender, byte[] data, DeliveryMethod method)
{
    byte[] drawing = BasisCompressionShim.Decompress(data);
    if (drawing == null) return;
}
```

## BasisConnectionShim

Asks the player to move to another server. Must be called from the player's own press or click; the player sees the address and has to accept.

```csharp
public void OnPortalPressed()
{
    BasisConnectionShim.RequestConnect(this, "play.example.com", 4296, null, result =>
    {
        if (result != BasisConnectionResult.Accepted) sign.text = "Maybe next time";
    });
}

public void OnLeavePressed() => BasisConnectionShim.RequestDisconnect(this);
```
