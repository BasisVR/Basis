using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class BasisInspectorHierarchy
{
    public struct Node
    {
        public Transform Transform;
        public int Depth;
        public bool HasChildren;
    }
    struct Sibling
    {
        public Transform Transform;
        public int Index;
    }
    public const int LargeRootCount = 50000;
    public const float LargeRootRefill = 2f;
    public readonly HashSet<Transform> Expanded = new HashSet<Transform>();
    readonly List<GameObject> roots = new List<GameObject>(256);
    readonly List<Sibling> expandedRoots = new List<Sibling>(16);
    readonly Dictionary<Transform, List<Sibling>> expandedChildren = new Dictionary<Transform, List<Sibling>>();
    readonly Dictionary<Transform, int> sizes = new Dictionary<Transform, int>();
    readonly Stack<List<Sibling>> pool = new Stack<List<Sibling>>();
    [NoAutoStaticsCleanup] static readonly List<Sibling> none = new List<Sibling>(0);
    [NoAutoStaticsCleanup] static readonly Comparison<Sibling> bySibling = (a, b) => a.Index.CompareTo(b.Index);
    [NoAutoStaticsCleanup] static readonly Predicate<Transform> destroyed = t => t == null;
    IReadOnlyList<Transform> listRoots;
    Scene scene;
    bool sceneMode, rootsDirty = true;
    float nextRootRefill;
    public int Count { get; private set; }
    int liveRootCount;
    public int RootCount => sceneMode ? liveRootCount : listRoots != null ? listRoots.Count : 0;
    public bool RootsStale => sceneMode && rootsDirty;
    public void Refresh(Scene target, float now)
    {
        if (!sceneMode || target != scene) rootsDirty = true;
        sceneMode = true;
        scene = target;
        listRoots = null;
        if (!scene.IsValid() || !scene.isLoaded)
        {
            roots.Clear();
            liveRootCount = 0;
            Index();
            return;
        }
        liveRootCount = scene.rootCount;
        if (rootsDirty && (roots.Count == 0 || liveRootCount < LargeRootCount || now >= nextRootRefill)) Refill(now);
        Index();
    }
    void Refill(float now)
    {
        scene.GetRootGameObjects(roots);
        liveRootCount = roots.Count;
        rootsDirty = false;
        nextRootRefill = now + LargeRootRefill;
    }
    public bool RefillIfStale(float now)
    {
        if (!sceneMode || !rootsDirty || !scene.IsValid() || !scene.isLoaded) return false;
        if (liveRootCount >= LargeRootCount && now < nextRootRefill) return false;
        Refill(now);
        Index();
        return true;
    }
    public void Refresh(IReadOnlyList<Transform> rootTransforms)
    {
        sceneMode = false;
        listRoots = rootTransforms;
        Index();
    }
    Transform Root(int index)
    {
        if (!sceneMode) return listRoots != null && index < listRoots.Count ? listRoots[index] : null;
        if (index < 0) return null;
        if (index >= roots.Count)
        {
            rootsDirty = true;
            return null;
        }
        GameObject go = roots[index];
        if (go == null)
        {
            rootsDirty = true;
            return null;
        }
        Transform t = go.transform;
        if (t.parent != null || go.scene != scene || t.GetSiblingIndex() != index) rootsDirty = true;
        return t.parent == null ? t : null;
    }
    int RootIndex(Transform t)
    {
        if (sceneMode) return t.gameObject.scene == scene ? t.GetSiblingIndex() : -1;
        if (listRoots == null) return -1;
        for (int i = 0; i < listRoots.Count; i++) if (listRoots[i] == t) return i;
        return -1;
    }
    void Index()
    {
        foreach (List<Sibling> list in expandedChildren.Values)
        {
            list.Clear();
            pool.Push(list);
        }
        expandedChildren.Clear();
        expandedRoots.Clear();
        sizes.Clear();
        Expanded.RemoveWhere(destroyed);
        foreach (Transform t in Expanded)
        {
            if (t.childCount == 0) continue;
            Transform parent = t.parent;
            if (parent == null)
            {
                int index = RootIndex(t);
                if (index >= 0) expandedRoots.Add(new Sibling { Transform = t, Index = index });
                continue;
            }
            if (!expandedChildren.TryGetValue(parent, out List<Sibling> siblings)) expandedChildren[parent] = siblings = pool.Count > 0 ? pool.Pop() : new List<Sibling>(4);
            siblings.Add(new Sibling { Transform = t, Index = t.GetSiblingIndex() });
        }
        expandedRoots.Sort(bySibling);
        foreach (List<Sibling> list in expandedChildren.Values) if (list.Count > 1) list.Sort(bySibling);
        long total = RootCount;
        foreach (Sibling s in expandedRoots) total += Size(s.Transform) - 1;
        Count = (int)Math.Min(total, int.MaxValue);
    }
    List<Sibling> Children(Transform t) => expandedChildren.TryGetValue(t, out List<Sibling> list) ? list : none;
    int Size(Transform t)
    {
        if (sizes.TryGetValue(t, out int size)) return size;
        long total = 1L + t.childCount;
        foreach (Sibling s in Children(t)) total += Size(s.Transform) - 1;
        size = (int)Math.Min(total, int.MaxValue);
        sizes[t] = size;
        return size;
    }
    public bool TryGet(int index, out Node node)
    {
        node = default;
        if (index < 0 || index >= Count) return false;
        List<Sibling> list = expandedRoots;
        Transform parent = null;
        int depth = 0;
        while (true)
        {
            int extra = 0;
            Transform inside = null;
            foreach (Sibling s in list)
            {
                int start = s.Index + extra;
                if (index < start) break;
                int size = Size(s.Transform);
                if (index < start + size)
                {
                    if (index == start)
                    {
                        node = Make(s.Transform, depth);
                        return true;
                    }
                    index -= start + 1;
                    inside = s.Transform;
                    break;
                }
                extra += size - 1;
            }
            if (inside != null)
            {
                parent = inside;
                list = Children(inside);
                depth++;
                continue;
            }
            int sibling = index - extra;
            Transform t = parent == null ? Root(sibling) : sibling < parent.childCount ? parent.GetChild(sibling) : null;
            if (t == null) return false;
            node = Make(t, depth);
            return true;
        }
    }
    static Node Make(Transform t, int depth) => new Node { Transform = t, Depth = depth, HasChildren = t.childCount > 0 };
    public int IndexOf(Transform target)
    {
        if (target == null) return -1;
        Transform parent = target.parent;
        if (parent == null)
        {
            int index = RootIndex(target);
            return index < 0 ? -1 : Offset(expandedRoots, index);
        }
        if (!Expanded.Contains(parent)) return -1;
        int parentIndex = IndexOf(parent);
        return parentIndex < 0 ? -1 : parentIndex + 1 + Offset(Children(parent), target.GetSiblingIndex());
    }
    int Offset(List<Sibling> list, int sibling)
    {
        long offset = sibling;
        foreach (Sibling s in list)
        {
            if (s.Index >= sibling) break;
            offset += Size(s.Transform) - 1;
        }
        return (int)Math.Min(offset, int.MaxValue);
    }
    public void Reveal(Transform target)
    {
        if (target == null) return;
        for (Transform parent = target.parent; parent != null; parent = parent.parent) Expanded.Add(parent);
    }
    public bool Toggle(Transform target)
    {
        if (target == null) return false;
        if (Expanded.Remove(target)) return false;
        Expanded.Add(target);
        return true;
    }
}
