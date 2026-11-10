using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class BasisGITestStage
{
    private static GameObject root;
    private static readonly List<(Transform transform, Vector3 centre, float radius, float speed, float phase)> orbiters = new List<(Transform, Vector3, float, float, float)>();
    private static bool moving = true;

    private static Material Lit(Color colour, Color emission)
    {
        Material m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.SetColor("_BaseColor", colour);
        if (emission.maxColorComponent > 0f)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        return m;
    }

    private static Transform Add(PrimitiveType type, string name, Vector3 position, Vector3 scale, Color colour, Color emission)
    {
        GameObject g = GameObject.CreatePrimitive(type);
        g.name = name;
        g.transform.SetParent(root.transform, false);
        g.transform.position = position;
        g.transform.localScale = scale;
        g.GetComponent<Renderer>().sharedMaterial = Lit(colour, emission);
        return g.transform;
    }

    private static Bounds AvatarBounds()
    {
        Bounds bounds = new Bounds(Vector3.zero, new Vector3(0.5f, 1.8f, 0.5f));
        bool found = false;
        foreach (SkinnedMeshRenderer r in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
        {
            if (r.name.Contains("ShadowOnly")) { continue; }
            if (!found) { bounds = r.bounds; found = true; } else { bounds.Encapsulate(r.bounds); }
        }
        return bounds;
    }

    public static void Build()
    {
        Clear();
        root = new GameObject("BasisGITestStage");
        Bounds a = AvatarBounds();
        Vector3 feet = new Vector3(a.center.x, a.min.y, a.center.z);
        Color wall = new Color(0.7f, 0.7f, 0.7f);
        Add(PrimitiveType.Cube, "Stage Floor", feet + new Vector3(0f, -0.05f, 0f), new Vector3(8f, 0.1f, 8f), wall, Color.black);
        Add(PrimitiveType.Cube, "Stage Ceiling", feet + new Vector3(0f, 3.05f, 0f), new Vector3(8f, 0.1f, 8f), wall, Color.black);
        Add(PrimitiveType.Cube, "Stage Back", feet + new Vector3(0f, 1.5f, -4.05f), new Vector3(8f, 3f, 0.1f), wall, Color.black);
        Add(PrimitiveType.Cube, "Stage Front", feet + new Vector3(0f, 1.5f, 4.05f), new Vector3(8f, 3f, 0.1f), wall, Color.black);
        Add(PrimitiveType.Cube, "Stage Left", feet + new Vector3(-4.05f, 1.5f, 0f), new Vector3(0.1f, 3f, 8f), new Color(0.8f, 0.15f, 0.15f), Color.black);
        Add(PrimitiveType.Cube, "Stage Right", feet + new Vector3(4.05f, 1.5f, 0f), new Vector3(0.1f, 3f, 8f), new Color(0.15f, 0.8f, 0.15f), Color.black);
        Add(PrimitiveType.Cube, "Stage Panel", feet + new Vector3(-2f, 2.98f, -1.5f), new Vector3(1.5f, 0.02f, 1.5f), Color.white, new Color(2f, 1.9f, 1.7f));
        Vector3 chest = feet + new Vector3(0f, a.size.y * 0.7f, 0f);
        orbiters.Add((Add(PrimitiveType.Sphere, "Stage Orb Warm", chest, Vector3.one * 0.16f, Color.white, new Color(12f, 7f, 2.5f)), chest, 0.55f, 0.9f, 0f));
        orbiters.Add((Add(PrimitiveType.Sphere, "Stage Orb Blue", chest, Vector3.one * 0.1f, Color.white, new Color(2f, 5f, 14f)), chest + new Vector3(0f, -0.5f, 0f), 0.45f, -1.4f, 2f));
        orbiters.Add((Add(PrimitiveType.Cube, "Stage Blocker", chest, new Vector3(0.6f, 1.2f, 0.1f), wall, Color.black), feet + new Vector3(0f, 0.6f, 0f), 1.6f, 0.5f, 1f));
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        Tick();
    }

    public static void Clear()
    {
        EditorApplication.update -= Tick;
        orbiters.Clear();
        if (root != null) { Object.DestroyImmediate(root); }
        GameObject stale = GameObject.Find("BasisGITestStage");
        if (stale != null) { Object.DestroyImmediate(stale); }
    }

    public static void Freeze() { moving = false; }
    public static void Move() { moving = true; }

    private static double frozenAt;

    private static void Tick()
    {
        if (root == null) { EditorApplication.update -= Tick; return; }
        if (moving) { frozenAt = EditorApplication.timeSinceStartup; }
        float t = (float)frozenAt;
        foreach ((Transform transform, Vector3 centre, float radius, float speed, float phase) o in orbiters)
        {
            if (o.transform == null) { continue; }
            float angle = t * o.speed + o.phase;
            o.transform.position = o.centre + new Vector3(Mathf.Cos(angle) * o.radius, Mathf.Sin(angle * 0.7f) * 0.15f, Mathf.Sin(angle) * o.radius);
        }
    }
}
