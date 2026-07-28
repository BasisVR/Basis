using GatorDragonGames.JigglePhysics;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics {
[BurstCompile]
public struct JiggleJobPrepareRender : IJob {
    [NativeDisableParallelForRestriction,ReadOnly]
    public NativeArray<JiggleCollider> personalColliders;
    [NativeDisableParallelForRestriction,ReadOnly]
    public NativeArray<JiggleCollider> sceneColliders;
    [NativeDisableParallelForRestriction,ReadOnly]
    public NativeArray<JiggleTransform> outputPoses;
    [NativeDisableParallelForRestriction,ReadOnly]
    public NativeArray<JiggleTreeJobData> trees;

    public int sceneColliderCount;
    public int personalColliderCount;
    public int transformCount;
    public int treeCount;

    public NativeArray<JiggleRenderInstancer.GPUChunk> sphereChunks;
    public NativeReference<Bounds> sphereBounds;
    public NativeReference<int> sphereCount;

    public NativeArray<JiggleRenderInstancer.GPUChunk> capsuleChunks;
    public NativeReference<Bounds> capsuleBounds;
    public NativeReference<int> capsuleCount;

    public void Execute() {
        float3 sphereMin = Vector3.one * 10000f;
        float3 sphereMax = Vector3.one * -10000f;
        float3 capsuleMin = Vector3.one * 10000f;
        float3 capsuleMax = Vector3.one * -10000f;
        int sphereIdx = 0;
        int capsuleIdx = 0;
        int sphereCapacity = sphereChunks.Length;
        int capsuleCapacity = capsuleChunks.Length;

        for (int i = 0; i < personalColliderCount; i++) {
            var collider = personalColliders[i];
            // Retired slots keep their last matrix, and freshly added ones are counted before the
            // collider TransformAccessArray flips — so until the read job has visited them their
            // matrix is whatever it was built with (or all zeros). Drawing either one pins a gizmo
            // wherever the avatar was when it spawned. worldRadius is only ever set by Read, so it
            // doubles as "this slot has been sampled at least once".
            if (!collider.enabled || collider.worldRadius <= 0f) {
                continue;
            }
            var position = collider.localToWorldMatrix.c3.xyz;
            switch (collider.type) {
                case JiggleCollider.JiggleColliderType.Sphere: {
                    sphereMin = math.min(sphereMin, position - new float3(collider.worldRadius));
                    sphereMax = math.max(sphereMax, position + new float3(collider.worldRadius));
                    if (sphereIdx < sphereCapacity) {
                        var scaleAdjust = float4x4.Scale(collider.radius * 2f);
                        sphereChunks[sphereIdx++] = new JiggleRenderInstancer.GPUChunk() {
                            matrix = math.mul(collider.localToWorldMatrix, scaleAdjust),
                            color = new float4(1f, 0.5490196f, 0f, 1f),
                        };
                    }
                    break;
                }
                case JiggleCollider.JiggleColliderType.Capsule: {
                    var extent = collider.worldRadius + collider.worldHeight * 0.5f;
                    capsuleMin = math.min(capsuleMin, position - new float3(extent));
                    capsuleMax = math.max(capsuleMax, position + new float3(extent));
                    if (capsuleIdx < capsuleCapacity) {
                        capsuleChunks[capsuleIdx++] = new JiggleRenderInstancer.GPUChunk() {
                            matrix = BuildCapsuleMatrix(collider),
                            color = new float4(1f, 0.5490196f, 0f, 1f),
                        };
                    }
                    break;
                }
            }
        }

        for (int i = 0; i < sceneColliderCount; i++) {
            var collider = sceneColliders[i];
            if (!collider.enabled || collider.worldRadius <= 0f) {
                continue;
            }
            var position = collider.localToWorldMatrix.c3.xyz;
            switch (collider.type) {
                case JiggleCollider.JiggleColliderType.Sphere: {
                    sphereMin = math.min(sphereMin, position - new float3(collider.worldRadius));
                    sphereMax = math.max(sphereMax, position + new float3(collider.worldRadius));
                    if (sphereIdx < sphereCapacity) {
                        var scaleAdjust = float4x4.Scale(2f * collider.radius);
                        sphereChunks[sphereIdx++] = new JiggleRenderInstancer.GPUChunk() {
                            matrix = math.mul(collider.localToWorldMatrix, scaleAdjust),
                            color = new float4(0.5450981f, 0f, 0f, 1f),
                        };
                    }
                    break;
                }
                case JiggleCollider.JiggleColliderType.Capsule: {
                    var extent = collider.worldRadius + collider.worldHeight * 0.5f;
                    capsuleMin = math.min(capsuleMin, position - new float3(extent));
                    capsuleMax = math.max(capsuleMax, position + new float3(extent));
                    if (capsuleIdx < capsuleCapacity) {
                        capsuleChunks[capsuleIdx++] = new JiggleRenderInstancer.GPUChunk() {
                            matrix = BuildCapsuleMatrix(collider),
                            color = new float4(0.5450981f, 0f, 0f, 1f),
                        };
                    }
                    break;
                }
            }
        }

        for (var i = 0; i < treeCount; i++) {
            var tree = trees[i];
            for (var o = 0; o < tree.pointCount; o++) {
                unsafe {
                    var point = tree.points[o];
                    var pose = outputPoses[o + (int)tree.transformIndexOffset];
                    if (pose.isVirtual) {
                        continue;
                    }
                    var radius = point.worldRadius;
                    if (sphereIdx < sphereCapacity) {
                        sphereChunks[sphereIdx++] = new JiggleRenderInstancer.GPUChunk() {
                            matrix = float4x4.TRS(pose.position, pose.rotation, new float3(1f * radius * 2f)),
                            color = new float4(0.5294118f, 0.8078432f, 0.9803922f, 1f),
                        };
                    }
                    sphereMin = math.min(sphereMin, pose.position - new float3(1f) * radius);
                    sphereMax = math.max(sphereMax, pose.position + new float3(1f) * radius);
                }
            }
        }

        sphereCount.Value = sphereIdx;
        sphereBounds.Value = new Bounds(Vector3.zero, math.max(math.abs(sphereMax), math.abs(sphereMin)) * 2f);
        capsuleCount.Value = capsuleIdx;
        capsuleBounds.Value = new Bounds(Vector3.zero, math.max(math.abs(capsuleMax), math.abs(capsuleMin)) * 2f);
    }

    private static float4x4 BuildCapsuleMatrix(JiggleCollider collider) {
        float4x4 permute = collider.capsuleAxis switch {
            JiggleCollider.CapsuleAxis.X => new float4x4(
                new float4(0f, -1f, 0f, 0f),
                new float4(1f, 0f, 0f, 0f),
                new float4(0f, 0f, 1f, 0f),
                new float4(0f, 0f, 0f, 1f)),
            JiggleCollider.CapsuleAxis.Z => new float4x4(
                new float4(1f, 0f, 0f, 0f),
                new float4(0f, 0f, 1f, 0f),
                new float4(0f, -1f, 0f, 0f),
                new float4(0f, 0f, 0f, 1f)),
            _ => float4x4.identity,
        };
        float diameter = collider.radius * 2f;
        float lengthScale = collider.height * 0.5f + collider.radius;
        float4x4 scale = float4x4.Scale(diameter, lengthScale, diameter);
        return math.mul(collider.localToWorldMatrix, math.mul(permute, scale));
    }

    public void Dispose() {
        if (sphereCount.IsCreated) {
            sphereCount.Dispose();
        }
        if (sphereBounds.IsCreated) {
            sphereBounds.Dispose();
        }
        if (capsuleCount.IsCreated) {
            capsuleCount.Dispose();
        }
        if (capsuleBounds.IsCreated) {
            capsuleBounds.Dispose();
        }
    }
}
}
