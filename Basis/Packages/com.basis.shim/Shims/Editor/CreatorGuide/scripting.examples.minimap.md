## Setup

1. Create a **Render Texture** asset, for example 512 x 512.
2. Create a **Camera** looking straight down. Set **Projection** to Orthographic, **Target Texture** to the render texture, and a **Culling Mask** that leaves out players and UI.
3. Show the render texture on a **Raw Image** in a canvas, or on a material.
4. Add **Minimap** to the camera.

## Script

```csharp title="Minimap.cs (Cilbox)"
using Basis.Scripts.BasisSdk.Players;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class Minimap : MonoBehaviour
{
    public float height = 40f;
    public bool rotateWithPlayer = true;
    public Transform playerMarker;

    void Update()
    {
        BasisLocalPlayer local = BasisLocalPlayer.Instance;
        if (local == null) return;
        local.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);

        float yaw = rotateWithPlayer ? rotation.eulerAngles.y : 0f;
        transform.SetPositionAndRotation(position + Vector3.up * height, Quaternion.Euler(90f, yaw, 0f));

        if (playerMarker != null) playerMarker.localRotation = Quaternion.Euler(0f, 0f, rotateWithPlayer ? 0f : -rotation.eulerAngles.y);
    }
}
```

## Round map with Graphics.Blit

To mask the square texture into a circle, blit it through a material each frame. `Graphics.Blit` is the one drawing call available to Cilbox scripts.

```csharp title="MinimapMask.cs (Cilbox)"
using Cilbox;
using UnityEngine;

[Cilboxable]
public class MinimapMask : MonoBehaviour
{
    public RenderTexture source;
    public RenderTexture destination;
    public Material circleMask;

    void Update()
    {
        Graphics.Blit(source, destination, circleMask);
    }
}
```

> [!info]
> A minimap camera renders the scene a second time. Keep its render texture small, its culling mask tight, and turn it off on low quality tiers with `BasisGraphicsSettingsShim.QualityTier`.
