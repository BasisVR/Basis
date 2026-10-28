> [!info]
> Saving, server storage and web requests ask the player for consent first. Until they agree, calls fail softly (false, null or `NotAllowed`) and the rest of your script keeps running.

## BasisSaveShim

Bytes saved on the player's machine. 256 KB per slot, 32 slots and 1 MB per store.

```csharp
using Basis.Shims;

BasisSaveShim.Save(this, "progress", bytes);
byte[] data = BasisSaveShim.Load(this, "progress");
BasisSaveShim.Delete(this, "progress");

// Shared between a world and its props.
BasisSaveShim.Save(this, "my-game", "progress", bytes);

// Readable by other content.
BasisSaveShim.Save(this, BasisSaveScope.ReadOnly, null, "badge", bytes);
byte[] badge = BasisSaveShim.LoadReadOnly(this, partnerContentId, null, "badge");
```

More: [Persistence example](/en/docs/scripting/examples/persistence).

## BasisServerStorageShim

Data saved **on the server**, per player. It follows the player between devices on that server. Every call is asynchronous. Keys are up to 64 bytes, values up to 64 KB; the server sets a total per player (16 KB by default).

```csharp
if (!BasisServerStorageShim.IsAvailable(this)) return;

BasisServerStorageShim.SaveString(this, "best-lap", lap.ToString("F2"), result =>
{
    if (result != BasisServerStorageResult.Ok) Debug.LogWarning(result.ToString());
});

BasisServerStorageShim.LoadString(this, "best-lap", (result, text) =>
{
    if (result == BasisServerStorageResult.Ok) bestLabel.text = text;
});

BasisServerStorageShim.Keys(this, (result, keys) => { });
BasisServerStorageShim.Usage(this, (result, used, limit) => usage.text = $"{used} / {limit} bytes");
```

Results include `Ok`, `NotFound`, `Full`, `RateLimited`, `NotConnected` and `NotAllowed`. More: [Server Leaderboard example](/en/docs/scripting/examples/server-best-time).

## BasisJson

Parses JSON on the host; your script only sees nodes and plain values.

```csharp
BasisJsonNode root = BasisJson.Parse(text, out string error);
if (root == null) { Debug.LogWarning(error); return; }

string title = root.GetString("title", "Untitled");
BasisJsonNode items = root.Get("items");
for (int i = 0; i < items.Count; i++)
{
    BasisJsonNode item = items.Get(i);
    string name = item.GetString("name", "");
    int price = item.GetInt("price", 0);
    string[] tags = item.GetStringArray("tags");
}
bool hasKey = root.Has("version");
string[] keys = root.Keys;
```

## BasisWebRequestShim

HTTPS GET and POST for worlds and props. 256 KB up, 4 MB down, 15 seconds, 30 requests a minute.

```csharp
BasisWebRequestShim.GetText(this, "https://api.example.com/scores", (result, status, text) =>
{
    if (result == BasisWebResult.Ok) ShowScores(text);
});

BasisWebRequestShim.PostText(this, "https://api.example.com/scores", "{\"score\":42}", BasisWebRequestShim.Json, (result, status, reply) => { });
```

## BasisImageDownloader and BasisStringDownloader

```csharp
using Basis;

new BasisImageDownloader().DownloadImage(url, result =>
{
    if (result.Success) poster.material.mainTexture = result.Result;
});

new BasisStringDownloader().DownloadString(url, result =>
{
    if (result.Success) Load(result.Result);
});
```

## BasisExternalLinkShim

Opens a website in the player's browser after they confirm. Call it from their own press.

```csharp
public void OnLinkPressed()
{
    BasisExternalLinkShim.Open(this, "https://example.com", result =>
    {
        if (result != BasisExternalLinkResult.Opened) label.text = "example.com";
    });
}
```
