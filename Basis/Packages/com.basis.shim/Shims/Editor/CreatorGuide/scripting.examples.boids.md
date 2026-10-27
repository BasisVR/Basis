Moving 200 transforms one by one from a Cilbox script costs hundreds of interpreted calls per frame. Batching makes it one.

```csharp title="Flock.cs (Cilbox)"
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class Flock : MonoBehaviour
{
    public Transform[] birds;
    public float radius = 10f;
    public float speed = 0.4f;

    float[] xyz;
    float[] phase;

    void Start()
    {
        xyz = new float[birds.Length * 3];
        phase = new float[birds.Length];
        for (int i = 0; i < phase.Length; i++) phase[i] = i * 0.37f;
    }

    void Update()
    {
        float t = Time.time * speed;
        Vector3 c = transform.position;
        float cx = c.x, cy = c.y, cz = c.z;
        for (int i = 0; i < birds.Length; i++)
        {
            float a = t + phase[i];
            xyz[i * 3] = cx + Mathf.Cos(a) * radius;
            xyz[i * 3 + 1] = cy + Mathf.Sin(a * 2f) * 2f;
            xyz[i * 3 + 2] = cz + Mathf.Sin(a) * radius;
        }
        BasisTransformBatchShim.SetPositions(birds, xyz);
    }
}
```

- The maths runs on plain floats, which is much cheaper in the interpreter than `Vector3` operators.
- `SetPositions(Transform[], float[])` writes every bird in one native call.
- Use `BasisNetworkTimeShim.ServerTimeSeconds` instead of `Time.time` if the flock must line up exactly between players.
