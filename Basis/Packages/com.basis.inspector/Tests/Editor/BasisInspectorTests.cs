using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

public class BasisInspectorTests
{
    class Sample : MonoBehaviour
    {
        public float speed;
        [SerializeField] int hiddenCount;
        int privateValue;
        [HideInInspector] public int skipped;
        [NonSerialized] public int notSerialized;
        [NoAutoStaticsCleanup] public static int shared;
        public string password;
        public string displayName;
        public string unlockKey;
        public int keyCount;
        public int Touch() => hiddenCount + privateValue;
    }
    readonly List<GameObject> spawned = new List<GameObject>();
    GameObject Make(string name, Transform parent = null)
    {
        GameObject go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        if (parent == null) spawned.Add(go);
        return go;
    }
    [TearDown]
    public void TearDown()
    {
        foreach (GameObject go in spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
        spawned.Clear();
    }
    [TestCase("m_LocalPosition", "Local Position")]
    [TestCase("_speed", "Speed")]
    [TestCase("k_MaxShift", "Max Shift")]
    [TestCase("activeInHierarchy", "Active In Hierarchy")]
    [TestCase("HDRColor", "HDR Color")]
    [TestCase("uv2", "Uv 2")]
    [TestCase("simple", "Simple")]
    public void Nicify_MatchesInspectorNames(string input, string expected) => Assert.That(BasisInspectorReflection.Nicify(input), Is.EqualTo(expected));
    [Test]
    public void SerializedFields_FollowUnityRules()
    {
        string[] labels = BasisInspectorReflection.GetMembers(typeof(Sample)).Select(m => m.Label).ToArray();
        Assert.That(labels, Is.EqualTo(new[] { "Speed", "Hidden Count", "Password", "Display Name", "Unlock Key", "Key Count" }));
    }
    [Test]
    public void SensitiveStrings_AreHidden()
    {
        Dictionary<string, bool> hidden = BasisInspectorReflection.GetMembers(typeof(Sample)).ToDictionary(m => m.Label, m => m.Hidden);
        Assert.That(hidden["Password"], Is.True);
        Assert.That(hidden["Unlock Key"], Is.True);
        Assert.That(hidden["Display Name"], Is.False);
        Assert.That(hidden["Key Count"], Is.False);
        Assert.That(BasisInspectorReflection.IsSensitive("authToken", typeof(string)), Is.True);
        Assert.That(BasisInspectorReflection.IsSensitive("Pass", typeof(byte[])), Is.True);
        Assert.That(BasisInspectorReflection.IsSensitive("compass", typeof(float)), Is.False);
    }
    [Test]
    public void NativeComponents_SkipInstancingGetters()
    {
        string[] labels = BasisInspectorReflection.GetMembers(typeof(MeshRenderer)).Select(m => m.Label).ToArray();
        Assert.That(labels, Does.Not.Contain("Material"));
        Assert.That(labels, Does.Not.Contain("Materials"));
        Assert.That(labels, Does.Contain("Shared Material"));
        Assert.That(BasisInspectorReflection.GetMembers(typeof(MeshFilter)).Select(m => m.Label), Does.Not.Contain("Mesh"));
    }
    [Test]
    public void NativeComponents_ReadWithoutSideEffects()
    {
        GameObject go = Make("Reader");
        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        go.AddComponent<MeshFilter>();
        foreach (BasisInspectorMember member in BasisInspectorReflection.GetMembers(typeof(MeshRenderer))) BasisInspectorReflection.Read(member, renderer);
        foreach (BasisInspectorMember member in BasisInspectorReflection.GetMembers(typeof(MeshFilter))) BasisInspectorReflection.Read(member, go.GetComponent<MeshFilter>());
        Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(0));
        Assert.That(go.GetComponent<MeshFilter>().sharedMesh, Is.Null);
    }
    [Test]
    public void Assets_SkipCopyingArraysAndMissingPropertyGetters()
    {
        string[] mesh = BasisInspectorReflection.GetMembers(typeof(Mesh)).Select(m => m.Label).ToArray();
        Assert.That(mesh, Does.Not.Contain("Vertices"));
        Assert.That(mesh, Does.Not.Contain("Triangles"));
        Assert.That(mesh, Does.Contain("Vertex Count"));
        string[] material = BasisInspectorReflection.GetMembers(typeof(Material)).Select(m => m.Label).ToArray();
        Assert.That(material, Does.Not.Contain("Color"));
        Assert.That(material, Does.Not.Contain("Main Texture"));
        Assert.That(material, Does.Contain("Render Queue"));
        Assert.That(BasisInspectorStyle.Classify(typeof(Material)), Is.EqualTo(BasisInspectorCategory.Material));
        Assert.That(BasisInspectorStyle.Classify(typeof(Texture2D)), Is.EqualTo(BasisInspectorCategory.Texture));
    }
    [Test]
    public void Transform_ShowsLocalThenWorld()
    {
        string[] labels = BasisInspectorReflection.GetMembers(typeof(Transform)).Select(m => m.Label).ToArray();
        Assert.That(labels.Take(3), Is.EqualTo(new[] { "Local Position", "Local Euler Angles", "Local Scale" }));
        Assert.That(BasisInspectorReflection.GetMembers(typeof(RectTransform)).Select(m => m.Label), Does.Contain("Anchored Position"));
    }
    [Test]
    public void Format_Values()
    {
        Assert.That(BasisInspectorReflection.Format(new Vector3(1f, 2.5f, -0.1234f)), Is.EqualTo("(1, 2.5, -0.123)"));
        Assert.That(BasisInspectorReflection.Format(3), Is.EqualTo("3"));
        Assert.That(BasisInspectorReflection.Format(new int[4]), Is.EqualTo("Int32[4]"));
        Assert.That(BasisInspectorReflection.Format(new List<string> { "a" }), Is.EqualTo("List<String> [1]"));
        Assert.That(BasisInspectorReflection.Format(Color.red), Is.EqualTo("#FF0000FF"));
        Assert.That(BasisInspectorReflection.Format(new string('x', 500)).Length, Is.EqualTo(BasisInspectorReflection.MaxText));
        Assert.That(BasisInspectorReflection.Format("a\nb"), Is.EqualTo("a b"));
        GameObject go = Make("Named");
        Assert.That(BasisInspectorReflection.Format(go.transform), Is.EqualTo("Named (Transform)"));
    }
    static List<string> Names(BasisInspectorHierarchy hierarchy)
    {
        List<string> names = new List<string>();
        for (int i = 0; i < hierarchy.Count; i++)
        {
            Assert.That(hierarchy.TryGet(i, out BasisInspectorHierarchy.Node node), Is.True);
            names.Add(node.Transform.name);
        }
        return names;
    }
    [Test]
    public void Hierarchy_FlattensOnlyExpanded()
    {
        GameObject a = Make("A");
        GameObject b = Make("B", a.transform);
        Make("C", b.transform);
        Make("D", a.transform);
        BasisInspectorHierarchy hierarchy = new BasisInspectorHierarchy();
        Transform[] roots = { a.transform };
        hierarchy.Refresh(roots);
        Assert.That(Names(hierarchy), Is.EqualTo(new[] { "A" }));
        hierarchy.TryGet(0, out BasisInspectorHierarchy.Node first);
        Assert.That(first.HasChildren, Is.True);
        hierarchy.Toggle(a.transform);
        hierarchy.Refresh(roots);
        Assert.That(Names(hierarchy), Is.EqualTo(new[] { "A", "B", "D" }));
        hierarchy.TryGet(2, out BasisInspectorHierarchy.Node third);
        Assert.That(third.Depth, Is.EqualTo(1));
        hierarchy.Expanded.Clear();
        hierarchy.Reveal(b.transform.GetChild(0));
        hierarchy.Refresh(roots);
        Assert.That(Names(hierarchy), Is.EqualTo(new[] { "A", "B", "C", "D" }));
        Assert.That(hierarchy.IndexOf(b.transform.GetChild(0)), Is.EqualTo(2));
        Assert.That(hierarchy.IndexOf(a.transform.GetChild(1)), Is.EqualTo(3));
    }
    [Test]
    public void Hierarchy_IndexesLargeTreesWithoutWalkingThem()
    {
        GameObject a = Make("A");
        GameObject b = Make("B");
        const int children = 20000;
        for (int i = 0; i < children; i++) Make("c" + i, a.transform);
        Make("deep", a.transform.GetChild(10).transform);
        BasisInspectorHierarchy hierarchy = new BasisInspectorHierarchy();
        Transform[] roots = { a.transform, b.transform };
        hierarchy.Toggle(a.transform);
        hierarchy.Toggle(a.transform.GetChild(10));
        hierarchy.Refresh(roots);
        Assert.That(hierarchy.Count, Is.EqualTo(2 + children + 1));
        hierarchy.TryGet(12, out BasisInspectorHierarchy.Node deep);
        Assert.That(deep.Transform.name, Is.EqualTo("deep"));
        Assert.That(deep.Depth, Is.EqualTo(2));
        hierarchy.TryGet(13, out BasisInspectorHierarchy.Node after);
        Assert.That(after.Transform.name, Is.EqualTo("c11"));
        hierarchy.TryGet(hierarchy.Count - 2, out BasisInspectorHierarchy.Node last);
        Assert.That(last.Transform.name, Is.EqualTo("c" + (children - 1)));
        hierarchy.TryGet(hierarchy.Count - 1, out BasisInspectorHierarchy.Node root);
        Assert.That(root.Transform, Is.EqualTo(b.transform));
        Assert.That(hierarchy.IndexOf(a.transform.GetChild(children - 1)), Is.EqualTo(hierarchy.Count - 2));
        Assert.That(hierarchy.TryGet(hierarchy.Count, out _), Is.False);
    }
    [Test]
    public void Hierarchy_DropsDestroyedExpansion()
    {
        GameObject a = Make("A");
        GameObject b = Make("B", a.transform);
        BasisInspectorHierarchy hierarchy = new BasisInspectorHierarchy();
        hierarchy.Toggle(a.transform);
        hierarchy.Toggle(b.transform);
        UnityEngine.Object.DestroyImmediate(b);
        hierarchy.Refresh(new[] { a.transform });
        Assert.That(hierarchy.Expanded.Count, Is.EqualTo(1));
        Assert.That(hierarchy.Count, Is.EqualTo(1));
    }
    [Test]
    public void WarmAsync_FillsTheCacheOffThread()
    {
        System.Threading.Tasks.Task task = BasisInspectorReflection.WarmAsync(new[] { typeof(Sample), typeof(Light), typeof(AudioSource) });
        Assert.That(task.Wait(10000), Is.True);
        Assert.That(BasisInspectorReflection.IsCached(typeof(Light)), Is.True);
        Assert.That(BasisInspectorReflection.IsCached(typeof(Sample)), Is.True);
    }
}
