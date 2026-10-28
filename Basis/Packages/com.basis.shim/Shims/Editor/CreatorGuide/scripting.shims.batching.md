Every call from a Cilbox script into Unity is expensive, often far more than the work it does. These shims take whole arrays across the boundary in one call and run the loop natively.

> [!info]
> If a loop in `Update` touches more than a handful of transforms or blendshapes, batch it.

## BasisTransformBatchShim

Read or write positions, rotations and scales of many transforms at once.

```csharp
using Basis.Shims;

public Transform[] boids;
Vector3[] positions;

void Start() { positions = new Vector3[boids.Length]; }

void Update()
{
    BasisTransformBatchShim.GetPositions(boids, positions);
    for (int i = 0; i < positions.Length; i++) positions[i] = positions[i] + Vector3.up * Time.deltaTime;
    BasisTransformBatchShim.SetPositions(boids, positions);
}
```

Also: `GetLocalPositions`, `SetLocalPositions`, `SetRotations`, `SetLocalRotations`, `SetLocalScales`, float `xyz` array versions, and `RotateWorld`.

## BasisTransformSyncShim

Copies one set of transforms onto another every frame, natively. Not networking: it mirrors transforms on this client, for example making a costume follow an avatar's bones.

```csharp
BasisTransformSyncShim mirror;

void Start()
{
    mirror = new BasisTransformSyncShim();
    mirror.Channels = BasisTransformSyncShim.ChannelPose;
    mirror.Space = BasisTransformSyncShim.SpaceLocal;
    mirror.BindTransforms(sourceBones, costumeBones, this);
}

void OnDestroy() { mirror.Dispose(); }
```

One shot static versions: `GetValues`, `SetValues` and `CopyValues`.

## BasisBlendShapeSyncShim

The same for blendshapes: read, write or copy every weight of a mesh in one call.

```csharp
float[] weights = new float[BasisBlendShapeSyncShim.ShapeCount(face)];
BasisBlendShapeSyncShim.GetWeights(face, weights);
BasisBlendShapeSyncShim.SetWeights(mask, weights, 0.01f);

// Or keep a mask's blendshapes following the face every frame.
var follow = new BasisBlendShapeSyncShim();
follow.BindMeshes(new[] { face }, new[] { mask }, this);
```

## BasisVectorArrayShim

Splits vectors into float arrays and joins them back. Maths on plain floats is much cheaper in the interpreter.

```csharp
BasisVectorArrayShim.Split(positions, xs, ys, zs);
for (int i = 0; i < xs.Length; i++) ys[i] += Mathf.Sin(xs[i] + Time.time) * 0.01f;
BasisVectorArrayShim.Join(xs, ys, zs, positions);
```

## BasisSphereCastShim

Ray and sphere tests over arrays, without physics colliders. Useful for bullets against many targets, or billiard balls.

```csharp
if (BasisSphereCastShim.RaySphere(muzzle.position, muzzle.forward, target.position, 0.25f, out Vector3 hit))
{
    Spark(hit);
}

uint hitMask = BasisSphereCastShim.RaySpheresMask(origin, direction, targetCenters, 0u, 0.25f);
```

Distances are squared (`radiusSq`). `skipMask` bits exclude spheres by index.
