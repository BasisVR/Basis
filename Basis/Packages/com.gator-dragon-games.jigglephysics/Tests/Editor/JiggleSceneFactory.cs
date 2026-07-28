using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Spawns throwaway bone hierarchies for the tests that exercise the managed half of the package
/// (tree building, rig data, the memory bus), and destroys every GameObject it created on Dispose.
/// Consumed as a field with a [TearDown] that disposes it, matching JiggleJobTransformTests.
/// </summary>
internal sealed class JiggleBoneScene : IDisposable {
    private readonly List<GameObject> spawned = new List<GameObject>();

    public Transform Spawn(string name, Transform parent = null, Vector3 localPosition = default) {
        var gameObject = new GameObject(name);
        spawned.Add(gameObject);
        var transform = gameObject.transform;
        if (parent != null) {
            transform.SetParent(parent, false);
        }
        transform.localPosition = localPosition;
        return transform;
    }

    /// <summary>
    /// A straight parent-to-child chain of `boneCount` bones, each one `spacing` metres below the
    /// last. This is the shape almost every real rig starts from: a tail, a hair strand, a rope.
    /// </summary>
    public Transform Chain(int boneCount, float spacing = 0.25f, string prefix = "bone", Vector3 direction = default) {
        if (boneCount < 1) {
            throw new ArgumentOutOfRangeException(nameof(boneCount));
        }
        var step = direction == default ? new Vector3(0f, -spacing, 0f) : direction.normalized * spacing;
        Transform root = null;
        Transform parent = null;
        for (int i = 0; i < boneCount; i++) {
            var offset = i == 0 ? Vector3.zero : step;
            var bone = Spawn($"{prefix}{i}", parent, offset);
            root ??= bone;
            parent = bone;
        }
        return root;
    }

    /// <summary>Walks a chain built by <see cref="Chain"/> and returns its bones root-first.</summary>
    public static Transform[] Descend(Transform root, int boneCount) {
        var bones = new Transform[boneCount];
        var current = root;
        for (int i = 0; i < boneCount; i++) {
            bones[i] = current;
            if (i + 1 < boneCount) {
                current = current.GetChild(0);
            }
        }
        return bones;
    }

    public void Dispose() {
        // Destroying a parent already destroyed its children, so the Unity null check is load
        // bearing rather than defensive.
        for (int i = 0; i < spawned.Count; i++) {
            if (spawned[i]) {
                Object.DestroyImmediate(spawned[i]);
            }
        }
        spawned.Clear();
    }
}

internal static class JiggleSceneFactory {
    /// <summary>
    /// Rig data pointing at `root`, validated the way the inspector validates it, so the distance
    /// cache the tree builder depends on is already populated.
    /// </summary>
    public static JiggleRigData Rig(Transform root, params Transform[] excluded) {
        var data = JiggleRigData.Default();
        data.rootBone = root;
        data.excludedTransforms = excluded ?? Array.Empty<Transform>();
        data.OnValidate();
        return data;
    }

    public static JiggleColliderSerializable SphereCollider(Transform transform, float radius = 0.1f) {
        return new JiggleColliderSerializable {
            transform = transform,
            collider = new JiggleCollider {
                type = JiggleCollider.JiggleColliderType.Sphere,
                radius = radius,
            },
        };
    }

    /// <summary>
    /// Releases the unmanaged point/parameter buffers a JiggleTree hands to the job side.
    /// JiggleTree.Dispose() routes through JigglePhysics.FreeOnComplete, which dereferences a job
    /// pipeline that only exists once the package has actually started simulating, so tests free
    /// the buffers directly — the same trick JiggleSimHarness uses.
    /// </summary>
    public static unsafe void FreeStruct(JiggleTree tree) {
        if (tree == null) {
            return;
        }
        var data = tree.GetStruct();
        if (data.points != null) {
            UnsafeUtility.Free(data.points, Allocator.Persistent);
        }
        if (data.parameters != null) {
            UnsafeUtility.Free(data.parameters, Allocator.Persistent);
        }
    }
}

/// <summary>
/// Unity runs [RuntimeInitializeOnLoadMethod] statics on entering play mode; edit mode tests never
/// do, so the dummy transform pool, the job pipeline and the segment registry are all null here and
/// the first call into them throws. These helpers stand that state up and tear it back down.
/// The initialisers are found by attribute rather than by name so renaming one cannot silently skip
/// it and leave a fixture testing against half-initialised statics.
/// </summary>
internal static class JiggleRuntimeStatics {
    // Removing a rig swaps a pooled dummy into every slot it vacated, so the pool has to be at least
    // as large as the highest transform or collider index any test frees.
    private const int DummyTransformPoolSize = 512;

    private static bool poolsReady;

    public static void Boot() {
        // Process wide state, latched: the pools never need resetting between tests, and rerunning
        // JiggleMemoryBus's own initialiser would Destroy() the pooled objects, which is illegal
        // outside play mode.
        if (!poolsReady) {
            poolsReady = true;
            RunInitializers(typeof(JiggleTreeSegment));
            FillDummyTransformPool();
        }
        RunInitializers(typeof(JigglePhysics));
    }

    /// <summary>
    /// JiggleMemoryBus grows its dummy pool with DontDestroyOnLoad, which throws outright in edit
    /// mode, so every rig removal would die inside PreRemoveTree. Pre-filling the pool means
    /// GetDummyTransform only ever reads from it. The pool is a play mode implementation detail
    /// rather than something the tests assert on, so standing it up by hand is safe.
    /// </summary>
    private static void FillDummyTransformPool() {
        const string fieldName = "dummyTransforms";
        var field = typeof(JiggleMemoryBus).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        if (field == null) {
            throw new InvalidOperationException(
                $"JiggleMemoryBus.{fieldName} was renamed; the edit mode dummy transform pool needs updating.");
        }
        if (field.GetValue(null) is not List<Transform> pool) {
            pool = new List<Transform>();
            field.SetValue(null, pool);
        }
        while (pool.Count < DummyTransformPoolSize) {
            var dummy = new GameObject($"JiggleTestDummyTransform{pool.Count}") {
                hideFlags = HideFlags.HideAndDontSave,
            };
            pool.Add(dummy.transform);
        }
    }

    public static void Shutdown() {
        JigglePhysics.Dispose();
    }

    private static void RunInitializers(Type type) {
        var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++) {
            var method = methods[i];
            if (method.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>() == null) {
                continue;
            }
            if (method.GetParameters().Length != 0) {
                continue;
            }
            method.Invoke(null, null);
        }
    }
}


/// <summary>
/// Hands a fixed <see cref="JiggleRigData"/> to a <see cref="JiggleTreeSegment"/>. The runtime
/// provider is a MonoBehaviour, which an edit mode test cannot stand up on its own.
/// </summary>
internal sealed class JiggleTestRigProvider : IJiggleParameterProvider {
    private JiggleRigData rigData;

    public JiggleTestRigProvider(JiggleRigData rigData) {
        this.rigData = rigData;
    }

    public JiggleRigData GetJiggleRigData() => rigData;

    public bool HasAnimatedParameters => false;
}

}
