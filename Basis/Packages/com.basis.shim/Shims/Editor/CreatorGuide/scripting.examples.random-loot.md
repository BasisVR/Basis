```csharp title="LootChest.cs (Cilbox)"
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class LootChest : MonoBehaviour
{
    public GameObject[] items;
    public int seed = 1234;
    public double refreshSeconds = 600d;
    long shownWindow = -1;

    void Update()
    {
        long window = BasisSharedRandomShim.WindowIndex(refreshSeconds);
        if (window == shownWindow) return;
        shownWindow = window;
        int pick = BasisSharedRandomShim.RangeInt(seed, window, 0, items.Length);
        for (int i = 0; i < items.Length; i++) items[i].SetActive(i == pick);
    }
}
```

- Every 10 minutes of server time the chest rerolls, and every client rolls the same item.
- Give each chest its own `seed` so they do not all show the same thing.
- For a roll a player triggers, send just the round number as a network event and let each client compute `RangeInt(seed, round, ...)`.
