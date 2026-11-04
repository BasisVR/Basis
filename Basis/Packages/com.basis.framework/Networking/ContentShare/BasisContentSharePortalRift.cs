using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

[AutoStaticsCleanup]
public partial class BasisContentSharePortalRift : MonoBehaviour
{
    public static float Radius = 0.9f, RimWidth = 0.07f, RimJitter = 0.09f, SwirlSpeed = 0.18f, SwirlTwist = 3.2f, OpenSeconds = 0.6f, TurnSpeed = 4f;
    public static Color CoreColor = new Color(0.35f, 0.06f, 0.65f, 1f), EdgeColor = new Color(0.2f, 0.9f, 1f, 1f);
    private const int Rings = 12, Segments = 64, TextureSize = 128;
    private static readonly int baseMapId = Shader.PropertyToID("_BaseMap"), mainTexId = Shader.PropertyToID("_MainTex"), emissionMapId = Shader.PropertyToID("_EmissionMap"), emissionColorId = Shader.PropertyToID("_EmissionColor"), baseColorId = Shader.PropertyToID("_BaseColor"), cullId = Shader.PropertyToID("_Cull");
    private Mesh coreMesh, rimMesh;
    private Material coreMaterial, rimMaterial;
    private Texture2D swirlTexture, rimTexture;
    private Vector2[] coreUvs;
    private Vector3[] rimVertices;
    private float age, seed;
    public static BasisContentSharePortalRift Create(Transform parent, Material source)
    {
        GameObject go = new GameObject("PortalRift");
        go.transform.SetParent(parent, false);
        BasisContentSharePortalRift rift = go.AddComponent<BasisContentSharePortalRift>();
        rift.Build(source);
        return rift;
    }
    private void Build(Material source)
    {
        seed = Random.value * 100f;
        swirlTexture = BuildSwirlTexture(seed);
        rimTexture = new Texture2D(1, 1);
        rimTexture.SetPixel(0, 0, Color.white);
        rimTexture.Apply();
        coreMaterial = CloneMaterial(source, swirlTexture, Color.white, Color.white * 1.6f);
        rimMaterial = CloneMaterial(source, rimTexture, EdgeColor, EdgeColor * 4f);
        coreMesh = BuildCoreMesh(out coreUvs);
        rimVertices = new Vector3[Segments * 2];
        rimMesh = BuildRimMesh();
        AddLayer("Core", coreMesh, coreMaterial);
        AddLayer("Rim", rimMesh, rimMaterial);
        BoxCollider box = gameObject.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(Radius * 2f, Radius * 2f, 0.3f);
        transform.localScale = new Vector3(1f, 0.001f, 1f);
        UpdateCore(0f);
        UpdateRim(0f);
    }
    private void AddLayer(string name, Mesh mesh, Material material)
    {
        GameObject layer = new GameObject(name);
        layer.transform.SetParent(transform, false);
        layer.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = layer.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }
    private static Material CloneMaterial(Material source, Texture2D texture, Color baseColor, Color emission)
    {
        Material material = new Material(source);
        if (material.HasProperty(baseMapId)) material.SetTexture(baseMapId, texture);
        if (material.HasProperty(mainTexId)) material.SetTexture(mainTexId, texture);
        if (material.HasProperty(emissionMapId)) material.SetTexture(emissionMapId, texture);
        if (material.HasProperty(baseColorId)) material.SetColor(baseColorId, baseColor);
        if (material.HasProperty(emissionColorId)) material.SetColor(emissionColorId, emission);
        if (material.HasProperty(cullId)) material.SetFloat(cullId, 0f);
        return material;
    }
    private static Texture2D BuildSwirlTexture(float seed)
    {
        Texture2D texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
        Color[] pixels = new Color[TextureSize * TextureSize];
        for (int y = 0; y < TextureSize; y++)
        {
            for (int x = 0; x < TextureSize; x++)
            {
                float u = (x + 0.5f) / TextureSize * 2f - 1f, v = (y + 0.5f) / TextureSize * 2f - 1f;
                float r = Mathf.Clamp01(Mathf.Sqrt(u * u + v * v)), angle = Mathf.Atan2(v, u);
                float arms = 0.5f + 0.5f * Mathf.Sin(angle * 3f + r * 9f + Mathf.PerlinNoise(seed + u * 3f, seed + v * 3f) * 4f);
                float grain = Mathf.PerlinNoise(seed * 2f + u * 9f, seed * 2f + v * 9f);
                float glow = Mathf.Pow(r, 3f);
                Color color = Color.Lerp(Color.black, CoreColor, Mathf.Clamp01(arms * (0.35f + 0.9f * r) * (0.6f + 0.6f * grain)));
                color += EdgeColor * (glow * (0.4f + 0.6f * arms));
                color.a = 1f;
                pixels[y * TextureSize + x] = color;
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(true, true);
        return texture;
    }
    private static Mesh BuildCoreMesh(out Vector2[] uvs)
    {
        int count = (Rings + 1) * Segments;
        Vector3[] vertices = new Vector3[count];
        uvs = new Vector2[count];
        int[] triangles = new int[Rings * Segments * 6];
        for (int ring = 0, t = 0; ring <= Rings; ring++)
        {
            float r = (float)ring / Rings;
            for (int s = 0; s < Segments; s++)
            {
                float angle = s * Mathf.PI * 2f / Segments;
                vertices[ring * Segments + s] = new Vector3(Mathf.Cos(angle) * Radius * r, Mathf.Sin(angle) * Radius * r, 0f);
                if (ring == Rings) continue;
                int a = ring * Segments + s, b = ring * Segments + (s + 1) % Segments, c = a + Segments, d = b + Segments;
                triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
            }
        }
        Mesh mesh = new Mesh { name = "PortalRiftCore" };
        mesh.MarkDynamic();
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(Radius * 2.5f, Radius * 2.5f, 0.1f));
        return mesh;
    }
    private Mesh BuildRimMesh()
    {
        int[] triangles = new int[Segments * 6];
        Vector2[] uvs = new Vector2[Segments * 2];
        for (int s = 0, t = 0; s < Segments; s++)
        {
            int a = s * 2, b = a + 1, c = (s + 1) % Segments * 2, d = c + 1;
            triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
            triangles[t++] = c; triangles[t++] = b; triangles[t++] = d;
            uvs[a] = uvs[b] = new Vector2(0.5f, 0.5f);
        }
        Mesh mesh = new Mesh { name = "PortalRiftRim" };
        mesh.MarkDynamic();
        mesh.vertices = rimVertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        Vector3[] normals = new Vector3[Segments * 2];
        for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.back;
        mesh.normals = normals;
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(Radius * 2.5f, Radius * 2.5f, 0.1f));
        return mesh;
    }
    private void UpdateCore(float time)
    {
        float spin = time * SwirlSpeed * Mathf.PI * 2f;
        for (int ring = 0; ring <= Rings; ring++)
        {
            float r = (float)ring / Rings, twist = spin * (1.6f - r) + (1f - r) * SwirlTwist;
            for (int s = 0; s < Segments; s++)
            {
                float angle = s * Mathf.PI * 2f / Segments + twist;
                coreUvs[ring * Segments + s] = new Vector2(0.5f + 0.5f * r * Mathf.Cos(angle), 0.5f + 0.5f * r * Mathf.Sin(angle));
            }
        }
        coreMesh.uv = coreUvs;
    }
    private void UpdateRim(float time)
    {
        for (int s = 0; s < Segments; s++)
        {
            float angle = s * Mathf.PI * 2f / Segments, cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            float jag = Mathf.PerlinNoise(seed + s * 0.37f, time * 2.2f) * 0.7f + Mathf.PerlinNoise(seed + s * 1.9f, time * 7f) * 0.3f;
            float outer = RimWidth + RimJitter * jag, inner = RimWidth * 0.25f * Mathf.PerlinNoise(seed + s * 0.8f, time * 3f);
            rimVertices[s * 2] = new Vector3(cos * (Radius - inner), sin * (Radius - inner), 0f);
            rimVertices[s * 2 + 1] = new Vector3(cos * (Radius + outer), sin * (Radius + outer), 0f);
        }
        rimMesh.vertices = rimVertices;
    }
    private void Update()
    {
        float deltaTime = Time.deltaTime;
        age += deltaTime;
        float open = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(age / OpenSeconds)), breathe = 1f + 0.025f * Mathf.Sin(age * 2.3f);
        transform.localScale = new Vector3(breathe * Mathf.Lerp(0.15f, 1f, open), breathe * Mathf.Max(open, 0.001f), 1f);
        UpdateCore(age);
        UpdateRim(age);
        BasisLocalPlayer local = BasisLocalPlayer.Instance;
        if (local == null) return;
        Vector3 toPlayer = local.transform.position - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(-toPlayer.normalized, Vector3.up), 1f - Mathf.Exp(-TurnSpeed * deltaTime));
    }
    private void OnDestroy()
    {
        Destroy(coreMesh);
        Destroy(rimMesh);
        Destroy(coreMaterial);
        Destroy(rimMaterial);
        Destroy(swirlTexture);
        Destroy(rimTexture);
    }
}
