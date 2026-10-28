## Setup

1. Create a **Quad** for the sign and give it a material with an unlit or lit texture slot.
2. Add **RemoteImage** and assign the renderer. Set the URL and an optional placeholder texture.

## Script

```csharp title="RemoteImage.cs (Cilbox)"
using Basis;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class RemoteImage : MonoBehaviour
{
    public string url = "https://example.com/poster.png";
    public Renderer target;
    public Texture placeholder;
    public float refreshMinutes = 0f;

    float nextRefresh;

    void Start()
    {
        if (placeholder != null) target.material.mainTexture = placeholder;
        Load();
    }

    void Update()
    {
        if (refreshMinutes <= 0f || Time.time < nextRefresh) return;
        Load();
    }

    void Load()
    {
        nextRefresh = Time.time + refreshMinutes * 60f;
        var downloader = new BasisImageDownloader();
        downloader.DownloadImage(url, result =>
        {
            if (result.Success) target.material.mainTexture = result.Result;
            else Debug.LogWarning($"poster failed: {result.Error}");
        });
    }
}
```

## Rules

- https only. Plain http, raw IP addresses and anything resolving to a local network address are refused before a request is made.
- No redirects. Host the image at its final address.
- Players approve hosts. The first time content asks for a new host, the player is prompted. Until they accept, the download fails and your placeholder stays.
- Every player downloads the image themselves; there is nothing to sync. To make everyone show the same changing image, sync the **URL index** (see [Synced Toggle](/en/docs/scripting/examples/synced-toggle) for the pattern) and load locally.

> [!info]
> For text, use `BasisStringDownloader.DownloadString` with the same rules, and `BasisJson.Parse` to read JSON.
