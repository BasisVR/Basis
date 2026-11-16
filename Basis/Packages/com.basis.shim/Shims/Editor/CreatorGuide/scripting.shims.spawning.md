## BasisSpawnShim

World only. Lets world scripts choose where players arrive and respawn. Set **Spawn Mode** to **Cilbox Callback** on the Basis Scene component, then declare `OnSpawnRequested()`. Add `OnRespawnRequested()` if respawns should land somewhere else; without it respawns call `OnSpawnRequested()` too. Neither takes parameters.

```csharp
using Basis.Shims;

public Transform[] pads;
public Transform checkpoint;
int next;

void OnSpawnRequested()
{
    Transform pad = pads[next++ % pads.Length];
    BasisSpawnShim.SetSpawnLocation(this, pad.position, pad.rotation);
}

void OnRespawnRequested()
{
    BasisSpawnShim.SetSpawnLocation(this, checkpoint.position, checkpoint.rotation);
}
```

The location sticks until `BasisSpawnShim.ClearSpawnLocation(this)`. The scene's Spawn Point is used until a script answers, and Basis moves the player once a script that loaded late does. Only scripts in the main world scene count. `BasisSpawnShim.IsScriptedSpawn()` says whether the current world uses this mode.

## Instantiate

Write `Instantiate` as usual in worlds, props and avatars. The sandbox swaps in `BasisCilboxInstantiateShim`, which builds the clone while it is inactive, removes components your content type is not allowed, switches off persistent UnityEvent listeners and Animator events, and only then turns it on.

```csharp
public GameObject dartPrefab;
public Rigidbody ballPrefab;

void Fire()
{
    GameObject dart = Instantiate(dartPrefab, muzzle.position, muzzle.rotation);
    Rigidbody ball = Instantiate(ballPrefab, transform);
}
```

If the component you cloned from was removed, the generic overload returns null. Clones made by an avatar follow avatar rules unless the player lets that avatar spawn props. Materials, textures and other assets are copied directly. `InstantiateAsync` is not available.

## BasisModelShim

Builds a model from bytes in a world or prop. GLB, glTF, OBJ, STL and PLY work, picked from the file's contents. Basis never fetches anything for the model. Download the bytes with `BasisWebRequestShim` and embed anything a glTF refers to. The callback runs on a later frame.

```csharp
using Basis.Shims;

public Transform pedestal;
GameObject statue;

void Start()
{
    BasisWebRequestShim.Get(this, "https://example.com/statue.glb", (result, status, bytes) =>
    {
        if (result != BasisWebResult.Ok) return;
        BasisModelShim.Load(this, bytes, pedestal, (loaded, model) =>
        {
            if (loaded == BasisModelResult.Ok) statue = model;
            else Debug.LogWarning(loaded.ToString());
        });
    });
}

void LoadPart(byte[] stl)
{
    BasisModelShim.LoadMesh(this, stl, (loaded, mesh) =>
    {
        if (loaded == BasisModelResult.Ok) GetComponent<MeshFilter>().sharedMesh = mesh;
    });
}

void Remove() => BasisModelShim.Unload(this, statue);
```

`LoadMesh` takes OBJ, STL and PLY only. With no parent, `Load` puts the model under the calling script's object. Each world or prop shares the triangle and memory budget the player sets in Script Permissions (`RemainingTriangles(this)` and `RemainingBytes(this)` report what is left). Files are capped at 64 MB and each content can load two at a time. Results are `Ok`, `Invalid`, `Unsupported`, `TooLarge`, `Busy`, `NotAllowed` and `Failed`. Destroying a model or unloading the content frees it as well.
