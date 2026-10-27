`BasisSaveShim` saves on the player's machine. `BasisServerStorageShim` saves on the **server**, per player, so a best time set on PC is still there when they visit from Quest.

```csharp title="ServerBestTime.cs (Cilbox)"
using Basis.Shims;
using Cilbox;
using TMPro;
using UnityEngine;

[Cilboxable]
public class ServerBestTime : MonoBehaviour
{
    const string Key = "best-lap";
    public TextMeshPro label;
    float best = float.MaxValue;

    void Start()
    {
        if (!BasisServerStorageShim.IsAvailable(this)) { label.text = "Times are not saved on this server"; return; }
        BasisServerStorageShim.LoadString(this, Key, (result, text) =>
        {
            if (result == BasisServerStorageResult.Ok && float.TryParse(text, out float saved)) best = saved;
            Show();
        });
    }

    public void LapFinished(float seconds)
    {
        if (seconds >= best) return;
        best = seconds;
        Show();
        BasisServerStorageShim.SaveString(this, Key, best.ToString("F3"), result =>
        {
            if (result != BasisServerStorageResult.Ok) Debug.LogWarning($"best lap not saved: {result}");
        });
    }

    void Show() => label.text = best < float.MaxValue ? $"Your best: {best:F2}s" : "No lap yet";
}
```

> [!info]
> Storage is per player and per server, with a small total (16 KB by default) shared across all content. Store compact values, not logs.
