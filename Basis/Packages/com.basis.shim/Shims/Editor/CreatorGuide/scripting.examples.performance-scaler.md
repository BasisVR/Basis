```csharp title="PerformanceScaler.cs (Cilbox)"
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class PerformanceScaler : MonoBehaviour
{
    public GameObject[] gpuHeavy;
    public GameObject[] cpuHeavy;
    float nextCheck;
    bool reduced, lowQuality;

    void Start()
    {
        GetComponent<BasisGraphicsSettingsEventShim>();
    }

    void OnGraphicsSettingsChanged(string qualityLevel, int qualityTier)
    {
        lowQuality = qualityTier <= BasisGraphicsSettingsShim.TierLow;
        Apply();
    }

    void Update()
    {
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + 2f;
        float target = BasisStatsShim.TargetFramesPerSecond;
        if (target <= 0f) return;
        float fps = BasisStatsShim.FramesPerSecond;
        if (!reduced && fps < target * 0.8f) { reduced = true; Apply(); }
        else if (reduced && fps > target * 0.95f) { reduced = false; Apply(); }
    }

    void Apply()
    {
        bool cutGpu = lowQuality || (reduced && BasisStatsShim.Bottleneck == BasisStatsShim.BottleneckGpu);
        bool cutCpu = reduced && BasisStatsShim.Bottleneck == BasisStatsShim.BottleneckCpu;
        foreach (GameObject g in gpuHeavy) g.SetActive(!cutGpu);
        foreach (GameObject g in cpuHeavy) g.SetActive(!cutCpu);
    }
}
```

- Two thresholds (80% to cut, 95% to restore) stop it flickering on and off.
- The bottleneck says which side to cut: mirrors and particles for GPU, crowds and scripts for CPU.
