using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using Object = UnityEngine.Object;

namespace GatorDragonGames.JigglePhysics.Tests {

[TestFixture]
internal class JiggleJobTransformTests {
    private readonly List<GameObject> spawned = new List<GameObject>();
    private TransformAccessArray access;

    [TearDown]
    public void TearDown() {
        if (access.isCreated) access.Dispose();
        foreach (var go in spawned) {
            if (go != null) Object.DestroyImmediate(go);
        }
        spawned.Clear();
    }

    private Transform Spawn(string name, Vector3 position = default, Quaternion rotation = default, Transform parent = null) {
        var go = new GameObject(name);
        spawned.Add(go);
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, rotation == default ? Quaternion.identity : rotation);
        return go.transform;
    }

    private void BuildAccess(params Transform[] transforms) {
        access = new TransformAccessArray(transforms);
    }

    private static JiggleTransform Local(Vector3 position, Quaternion rotation, bool isVirtual = false) {
        return new JiggleTransform {
            isVirtual = isVirtual, position = position, rotation = rotation, scale = new float3(1f),
        };
    }

    [Test]
    public void BulkTransformRead_CapturesWorldPositionRotationAndScale() {
        var parent = Spawn("parent", new Vector3(1f, 0f, 0f));
        parent.localScale = new Vector3(2f, 2f, 2f);
        var child = Spawn("child", parent: parent);
        child.localPosition = new Vector3(1f, 0f, 0f);
        BuildAccess(child);

        var poses = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        poses[0] = Local(Vector3.zero, Quaternion.identity);
        var job = new JiggleJobBulkTransformRead { simulateInputPoses = poses };
        job.Schedule(access).Complete();

        JiggleAssert.AreEqual(new float3(3f, 0f, 0f), poses[0].position, 1e-3f);
        JiggleAssert.AreEqual(new float3(2f), poses[0].scale, 1e-3f);
        poses.Dispose();
    }

    [Test]
    public void BulkTransformRead_SkipsVirtualSlots() {
        var t = Spawn("bone", new Vector3(5f, 5f, 5f));
        BuildAccess(t);

        var poses = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        poses[0] = Local(Vector3.zero, Quaternion.identity, isVirtual: true);
        var job = new JiggleJobBulkTransformRead { simulateInputPoses = poses };
        job.Schedule(access).Complete();

        JiggleAssert.AreEqual(float3.zero, poses[0].position, 1e-3f);
        poses.Dispose();
    }

    [Test]
    public void BulkReadRoots_WritesWorldPositions() {
        var a = Spawn("a", new Vector3(1f, 2f, 3f));
        var b = Spawn("b", new Vector3(-4f, 0f, 6f));
        BuildAccess(a, b);

        var roots = new NativeArray<float3>(2, Allocator.Persistent);
        var job = new JiggleJobBulkReadRoots { rootOutputPositions = roots };
        job.Schedule(access).Complete();

        JiggleAssert.AreEqual(new float3(1f, 2f, 3f), roots[0], 1e-3f);
        JiggleAssert.AreEqual(new float3(-4f, 0f, 6f), roots[1], 1e-3f);
        roots.Dispose();
    }

    [Test]
    public void TransformWrite_AppliesThePoseAndRecordsTheResultingLocalPose() {
        var parent = Spawn("parent", new Vector3(10f, 0f, 0f));
        var child = Spawn("child", parent: parent);
        BuildAccess(child);

        var interpolated = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var previousLocal = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        interpolated[0] = Local(new Vector3(12f, 3f, 0f), Quaternion.identity);

        var job = new JiggleJobTransformWrite {
            inputInterpolatedPoses = interpolated, previousLocalPoses = previousLocal,
        };
        job.Schedule(access).Complete();

        JiggleAssert.AreEqual(new float3(12f, 3f, 0f), (float3)(Vector3)child.position, 1e-3f);
        JiggleAssert.AreEqual(new float3(2f, 3f, 0f), previousLocal[0].position, 1e-3f);

        interpolated.Dispose();
        previousLocal.Dispose();
    }

    [Test]
    public void TransformWrite_SkipsVirtualPoses() {
        var t = Spawn("bone", new Vector3(1f, 1f, 1f));
        BuildAccess(t);

        var interpolated = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var previousLocal = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        interpolated[0] = Local(new Vector3(99f, 99f, 99f), Quaternion.identity, isVirtual: true);

        var job = new JiggleJobTransformWrite {
            inputInterpolatedPoses = interpolated, previousLocalPoses = previousLocal,
        };
        job.Schedule(access).Complete();

        JiggleAssert.AreEqual(new float3(1f, 1f, 1f), (float3)(Vector3)t.position, 1e-3f);

        interpolated.Dispose();
        previousLocal.Dispose();
    }

    [Test]
    public void ColliderRead_PullsTheTransformMatrixIntoTheCollider() {
        var t = Spawn("collider", new Vector3(2f, 0f, 0f));
        t.localScale = new Vector3(3f, 3f, 3f);
        BuildAccess(t);

        var colliders = new NativeArray<JiggleCollider>(1, Allocator.Persistent);
        colliders[0] = new JiggleCollider {
            enabled = true, type = JiggleCollider.JiggleColliderType.Sphere, radius = 0.5f,
        };
        var job = new JiggleJobBulkColliderTransformRead(colliders);
        job.Schedule(access).Complete();

        JiggleAssert.AreEqual(new float3(2f, 0f, 0f), colliders[0].localToWorldMatrix.c3.xyz, 1e-3f);
        Assert.AreEqual(1.5f, colliders[0].worldRadius, 1e-3f);
        colliders.Dispose();
    }

    [Test]
    public void ColliderRead_LeavesDisabledCollidersAlone() {
        var t = Spawn("collider", new Vector3(7f, 0f, 0f));
        BuildAccess(t);

        var colliders = new NativeArray<JiggleCollider>(1, Allocator.Persistent);
        colliders[0] = new JiggleCollider {
            enabled = false, type = JiggleCollider.JiggleColliderType.Sphere, radius = 0.5f,
        };
        var job = new JiggleJobBulkColliderTransformRead(colliders);
        job.Schedule(access).Complete();

        Assert.AreEqual(0f, colliders[0].worldRadius, 1e-6f);
        colliders.Dispose();
    }

    /// <summary>
    /// A destroyed transform is dropped by TransformAccessArray rather than visited with an invalid
    /// handle, so its collider keeps whatever state it last had; what matters is that the surviving
    /// entries are still sampled correctly and the job does not fault.
    /// </summary>
    [Test]
    public void ColliderRead_SurvivesADestroyedTransform() {
        var alive = Spawn("alive", new Vector3(1f, 0f, 0f));
        var doomed = Spawn("doomed", new Vector3(2f, 0f, 0f));
        BuildAccess(alive, doomed);
        Object.DestroyImmediate(doomed.gameObject);

        var colliders = new NativeArray<JiggleCollider>(2, Allocator.Persistent);
        for (int i = 0; i < 2; i++) {
            colliders[i] = new JiggleCollider {
                enabled = true, type = JiggleCollider.JiggleColliderType.Sphere, radius = 0.5f,
            };
        }
        var job = new JiggleJobBulkColliderTransformRead(colliders);

        Assert.DoesNotThrow(() => job.Schedule(access).Complete());
        Assert.IsTrue(colliders[0].enabled);
        Assert.AreEqual(0.5f, colliders[0].worldRadius, 1e-3f);
        colliders.Dispose();
    }

    [Test]
    public void TransformReset_RestoresTheRestPoseWhenNothingChanged() {
        var t = Spawn("bone");
        t.localPosition = new Vector3(4f, 4f, 4f);
        BuildAccess(t);

        var rest = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var previousLocal = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        rest[0] = Local(new Vector3(1f, 2f, 3f), Quaternion.identity);
        previousLocal[0] = Local(new Vector3(4f, 4f, 4f), Quaternion.identity);

        var job = new JiggleJobBulkTransformReset { restPoseTransforms = rest, previousLocalTransforms = previousLocal };
        job.Schedule(access).Complete();

        JiggleAssert.AreEqual(new float3(1f, 2f, 3f), (float3)(Vector3)t.localPosition, 1e-3f);
        rest.Dispose();
        previousLocal.Dispose();
    }

    [Test]
    public void TransformReset_AdoptsAnAnimatedPositionAsTheNewRestPose() {
        var t = Spawn("bone");
        t.localPosition = new Vector3(9f, 0f, 0f);
        BuildAccess(t);

        var rest = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var previousLocal = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        rest[0] = Local(new Vector3(1f, 0f, 0f), Quaternion.identity);
        previousLocal[0] = Local(new Vector3(4f, 4f, 4f), Quaternion.identity);

        var job = new JiggleJobBulkTransformReset { restPoseTransforms = rest, previousLocalTransforms = previousLocal };
        job.Schedule(access).Complete();

        JiggleAssert.AreEqual(new float3(9f, 0f, 0f), rest[0].position, 1e-3f);
        rest.Dispose();
        previousLocal.Dispose();
    }

    [Test]
    public void TransformReset_AdoptsAnAnimatedRotationAndRestoresThePosition() {
        var t = Spawn("bone");
        var animated = Quaternion.Euler(0f, 45f, 0f);
        t.localPosition = new Vector3(4f, 4f, 4f);
        t.localRotation = animated;
        BuildAccess(t);

        var rest = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var previousLocal = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        rest[0] = Local(new Vector3(1f, 2f, 3f), Quaternion.identity);
        previousLocal[0] = Local(new Vector3(4f, 4f, 4f), Quaternion.identity);

        var job = new JiggleJobBulkTransformReset { restPoseTransforms = rest, previousLocalTransforms = previousLocal };
        job.Schedule(access).Complete();

        JiggleAssert.AreEqual(new float3(1f, 2f, 3f), (float3)(Vector3)t.localPosition, 1e-3f);
        Assert.AreEqual(1f, Mathf.Abs(Quaternion.Dot(animated, rest[0].rotation)), 1e-3f);
        rest.Dispose();
        previousLocal.Dispose();
    }

    [Test]
    public void TransformReset_AdoptsBothChannelsWhenBothMoved() {
        var t = Spawn("bone");
        var animated = Quaternion.Euler(0f, 30f, 0f);
        t.localPosition = new Vector3(7f, 8f, 9f);
        t.localRotation = animated;
        BuildAccess(t);

        var rest = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var previousLocal = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        rest[0] = Local(new Vector3(1f, 2f, 3f), Quaternion.identity);
        previousLocal[0] = Local(new Vector3(4f, 4f, 4f), Quaternion.identity);

        var job = new JiggleJobBulkTransformReset { restPoseTransforms = rest, previousLocalTransforms = previousLocal };
        job.Schedule(access).Complete();

        JiggleAssert.AreEqual(new float3(7f, 8f, 9f), rest[0].position, 1e-3f);
        Assert.AreEqual(1f, Mathf.Abs(Quaternion.Dot(animated, rest[0].rotation)), 1e-3f);
        JiggleAssert.AreEqual(new float3(7f, 8f, 9f), (float3)(Vector3)t.localPosition, 1e-3f);
        rest.Dispose();
        previousLocal.Dispose();
    }

    [Test]
    public void TransformReset_SkipsVirtualSlots() {
        var t = Spawn("bone");
        t.localPosition = new Vector3(4f, 4f, 4f);
        BuildAccess(t);

        var rest = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var previousLocal = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        rest[0] = Local(new Vector3(1f, 2f, 3f), Quaternion.identity);
        previousLocal[0] = Local(new Vector3(4f, 4f, 4f), Quaternion.identity, isVirtual: true);

        var job = new JiggleJobBulkTransformReset { restPoseTransforms = rest, previousLocalTransforms = previousLocal };
        job.Schedule(access).Complete();

        JiggleAssert.AreEqual(new float3(4f, 4f, 4f), (float3)(Vector3)t.localPosition, 1e-3f);
        rest.Dispose();
        previousLocal.Dispose();
    }

    /// <summary>
    /// The merged read+reset job replaced a reset pass followed by a read pass; it must leave the
    /// transform, the rest pose and the sampled input identical to running the two separately.
    /// </summary>
    [Test]
    public void MergedReadReset_MatchesResetThenReadForAnUnchangedTransform() {
        RunMergedComparison(new Vector3(4f, 4f, 4f), Quaternion.identity, new Vector3(4f, 4f, 4f), Quaternion.identity);
    }

    [Test]
    public void MergedReadReset_MatchesResetThenReadForAMovedTransform() {
        RunMergedComparison(new Vector3(9f, 1f, 2f), Quaternion.identity, new Vector3(4f, 4f, 4f), Quaternion.identity);
    }

    [Test]
    public void MergedReadReset_MatchesResetThenReadForARotatedTransform() {
        RunMergedComparison(new Vector3(4f, 4f, 4f), Quaternion.Euler(0f, 45f, 0f), new Vector3(4f, 4f, 4f), Quaternion.identity);
    }

    [Test]
    public void MergedReadReset_MatchesResetThenReadWhenBothChannelsMoved() {
        RunMergedComparison(new Vector3(5f, 6f, 7f), Quaternion.Euler(10f, 20f, 30f), new Vector3(4f, 4f, 4f), Quaternion.identity);
    }

    private void RunMergedComparison(Vector3 currentLocalPosition, Quaternion currentLocalRotation,
        Vector3 previousLocalPosition, Quaternion previousLocalRotation) {
        var restPosition = new Vector3(1f, 2f, 3f);
        var restRotation = Quaternion.Euler(0f, 90f, 0f);

        var separateTransform = Spawn("separate");
        separateTransform.SetLocalPositionAndRotation(currentLocalPosition, currentLocalRotation);
        var separateAccess = new TransformAccessArray(new[] { separateTransform });

        var separateRest = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var separatePrevious = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var separateInput = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        separateRest[0] = Local(restPosition, restRotation);
        separatePrevious[0] = Local(previousLocalPosition, previousLocalRotation);
        separateInput[0] = Local(Vector3.zero, Quaternion.identity);

        new JiggleJobBulkTransformReset { restPoseTransforms = separateRest, previousLocalTransforms = separatePrevious }
            .Schedule(separateAccess).Complete();
        new JiggleJobBulkTransformRead { simulateInputPoses = separateInput }
            .Schedule(separateAccess).Complete();

        var mergedTransform = Spawn("merged");
        mergedTransform.SetLocalPositionAndRotation(currentLocalPosition, currentLocalRotation);
        var mergedAccess = new TransformAccessArray(new[] { mergedTransform });

        var mergedRest = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var mergedPrevious = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var mergedInput = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        mergedRest[0] = Local(restPosition, restRotation);
        mergedPrevious[0] = Local(previousLocalPosition, previousLocalRotation);
        mergedInput[0] = Local(Vector3.zero, Quaternion.identity);

        new JiggleJobBulkTransformReadReset {
            restPoseTransforms = mergedRest, previousLocalTransforms = mergedPrevious, simulateInputPoses = mergedInput,
        }.Schedule(mergedAccess).Complete();

        JiggleAssert.AreEqual(separateRest[0].position, mergedRest[0].position, 1e-4f, "rest position");
        Assert.AreEqual(1f, Mathf.Abs(Quaternion.Dot(separateRest[0].rotation, mergedRest[0].rotation)), 1e-4f, "rest rotation");
        JiggleAssert.AreEqual(separateInput[0].position, mergedInput[0].position, 1e-4f, "sampled input position");
        Assert.AreEqual(1f, Mathf.Abs(Quaternion.Dot(separateInput[0].rotation, mergedInput[0].rotation)), 1e-4f, "sampled input rotation");
        JiggleAssert.AreEqual((float3)(Vector3)separateTransform.localPosition, (float3)(Vector3)mergedTransform.localPosition, 1e-4f, "resulting local position");

        separateAccess.Dispose();
        mergedAccess.Dispose();
        separateRest.Dispose();
        separatePrevious.Dispose();
        separateInput.Dispose();
        mergedRest.Dispose();
        mergedPrevious.Dispose();
        mergedInput.Dispose();
    }

    [Test]
    public void MergedReadReset_StillSamplesInputForVirtualRestSlots() {
        var t = Spawn("bone", new Vector3(3f, 4f, 5f));
        BuildAccess(t);

        var rest = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var previousLocal = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var input = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        rest[0] = Local(Vector3.zero, Quaternion.identity);
        previousLocal[0] = Local(Vector3.zero, Quaternion.identity, isVirtual: true);
        input[0] = Local(Vector3.zero, Quaternion.identity);

        new JiggleJobBulkTransformReadReset {
            restPoseTransforms = rest, previousLocalTransforms = previousLocal, simulateInputPoses = input,
        }.Schedule(access).Complete();

        JiggleAssert.AreEqual(new float3(3f, 4f, 5f), input[0].position, 1e-3f);

        rest.Dispose();
        previousLocal.Dispose();
        input.Dispose();
    }

    [Test]
    public void MergedReadReset_SkipsSamplingForVirtualInputSlots() {
        var t = Spawn("bone", new Vector3(3f, 4f, 5f));
        BuildAccess(t);

        var rest = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var previousLocal = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        var input = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        rest[0] = Local(Vector3.zero, Quaternion.identity);
        previousLocal[0] = Local(Vector3.zero, Quaternion.identity, isVirtual: true);
        input[0] = Local(Vector3.zero, Quaternion.identity, isVirtual: true);

        new JiggleJobBulkTransformReadReset {
            restPoseTransforms = rest, previousLocalTransforms = previousLocal, simulateInputPoses = input,
        }.Schedule(access).Complete();

        JiggleAssert.AreEqual(float3.zero, input[0].position, 1e-3f);

        rest.Dispose();
        previousLocal.Dispose();
        input.Dispose();
    }
}

}
