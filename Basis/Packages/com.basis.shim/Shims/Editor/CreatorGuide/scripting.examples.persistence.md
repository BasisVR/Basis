`BasisSaveShim` stores bytes on the player's own machine, filed under your content's ID. It is per player: each player has their own save, and nothing is synced.

## Script

This saves which checkpoints a player reached and their best time, and puts them back at their last checkpoint when they return.

```csharp title="CourseProgress.cs (Cilbox)"
using System.IO;
using Basis.Scripts.BasisSdk.Players;
using Basis.Shims;
using Cilbox;
using TMPro;
using UnityEngine;

[Cilboxable]
public class CourseProgress : MonoBehaviour
{
    const string Slot = "progress";
    const byte Version = 1;

    public Transform[] checkpoints;
    public TextMeshPro bestLabel;

    int reached = -1;
    float bestTime = float.MaxValue;

    void Start()
    {
        Load();
        if (reached >= 0)
        {
            Transform point = checkpoints[reached];
            BasisLocalPlayer.Instance.Teleport(point.position, point.rotation, BasisTeleportMode.Instant);
        }
        ShowBest();
    }

    public void ReachCheckpoint(int index)
    {
        if (index <= reached) return;
        reached = index;
        Save();
    }

    public void FinishRun(float seconds)
    {
        if (seconds < bestTime) bestTime = seconds;
        reached = -1;
        Save();
        ShowBest();
    }

    void Save()
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Version);
            writer.Write(reached);
            writer.Write(bestTime);
            BasisSaveShim.Save(this, Slot, stream.ToArray());
        }
    }

    void Load()
    {
        byte[] data = BasisSaveShim.Load(this, Slot);
        if (data == null || data.Length < 9) return;
        using (var reader = new BinaryReader(new MemoryStream(data)))
        {
            if (reader.ReadByte() != Version) return;
            reached = Mathf.Clamp(reader.ReadInt32(), -1, checkpoints.Length - 1);
            bestTime = reader.ReadSingle();
        }
    }

    void ShowBest()
    {
        bestLabel.text = bestTime < float.MaxValue ? $"Best: {bestTime:F1}s" : "No time yet";
    }
}
```

## Rules

- **The player decides.** Until they allow saving for your content, `Save` returns false and `Load` returns null. Your script keeps running, so always handle a missing save.
- **Version your data.** Write a version byte first and refuse data you do not understand. Players keep old saves when you update your world.
- **Validate on load.** Clamp indices; never trust saved values to still match your scene.
- **Limits:** 256 KB per slot, 32 slots and 1 MB per store, 8 writes a second. Save on meaningful events, not every frame.

## Sharing between content

Name a store to share saves, for example between a world and the props it spawns:

```csharp
BasisSaveShim.Save(this, "my-game", "progress", bytes);
byte[] data = BasisSaveShim.Load(this, "my-game", "progress");
```

Scopes control who can read and write:

| Scope | Read | Write |
| --- | --- | --- |
| `BasisSaveScope.Protected` | This content only | This content only |
| `BasisSaveScope.ReadOnly` | Any content (with your Content ID) | This content only |
| `BasisSaveScope.Shared` | Any content that names the store | Any content that names the store |

```csharp
// Publish a badge other worlds can read.
BasisSaveShim.Save(this, BasisSaveScope.ReadOnly, null, "badge", bytes);

// Another world reads it.
byte[] badge = BasisSaveShim.LoadReadOnly(this, partnerContentId, null, "badge");
```

> [!warn]
> Saves live on the player's machine and can be edited by them. Do not use them for anything that must be trusted, such as leaderboards.
