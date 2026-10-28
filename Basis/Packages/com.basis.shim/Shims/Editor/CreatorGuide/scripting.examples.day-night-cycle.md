A day cycle driven by `Time.time` drifts apart between players who joined at different moments. Driving it from the server clock keeps everyone in step without any messages.

```csharp title="DayNightCycle.cs (Cilbox)"
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class DayNightCycle : MonoBehaviour
{
    public Light sun;
    public float minutesPerDay = 24f;
    public Gradient skyColor;

    void Update()
    {
        double seconds = BasisNetworkTimeShim.ServerTimeSeconds;
        float day = minutesPerDay * 60f;
        float t = (float)(seconds % day / day);
        sun.transform.rotation = Quaternion.Euler(t * 360f - 90f, 30f, 0f);
        sun.intensity = Mathf.Clamp01(Mathf.Sin(t * Mathf.PI * 2f) * 2f);
        RenderSettings.ambientLight = skyColor.Evaluate(t);
    }
}
```

- No ownership and no late joiner code: the clock is the shared state.
- In a heavy scene, update the sun a few times a second instead of every frame.
