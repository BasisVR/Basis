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
    
    public void Execute() {
        float3 min = Vector3.one * 10000f;
        float3 max = Vector3.one * -10000f;
        int currentCount = 0;

        for (int i = 0; i < personalColliderCount; i++) {
            var collider = personalColliders[i];
            if (!collider.enabled) continue;
            currentCount = AppendColliderChunks(collider, currentCount, new float4(1f, 0.5490196f, 0f, 1f), ref min, ref max);
        }

        for (int i = 0; i < sceneColliderCount; i++) {
            var collider = sceneColliders[i];
            if (!collider.enabled) continue;
            currentCount = AppendColliderChunks(collider, currentCount, new float4(0.5450981f, 0f, 0f, 1f), ref min, ref max);
        }
        for (var i = 0; i < treeCount; i++) {
            var tree = trees[i];
            for(var o=0;o<tree.pointCount;o++) {
                unsafe {
                    var point = tree.points[o];
                    var pose = outputPoses[o + (int)tree.transformIndexOffset];
                    if (pose.isVirtual) {
                        continue;
                    }
                    var radius = point.worldRadius;
                    JiggleRenderInstancer.GPUChunk chunk = new JiggleRenderInstancer.GPUChunk() {
                        matrix = float4x4.TRS(pose.position, pose.rotation, new float3(1f*radius*2f)),
                        color = new float4(0.5294118f, 0.8078432f, 0.9803922f, 1f),
                    };
                    min = math.min(min, pose.position - new float3(1f)*radius);
                    max = math.max(max, pose.position + new float3(1f)*radius);
                    sphereChunks[currentCount] = chunk;
                    currentCount++;
                }
            }
        }

        sphereCount.Value = currentCount;
        sphereBounds.Value = new Bounds(Vector3.zero, math.max(math.abs(max), math.abs(min))*2f);
    }

    private int AppendColliderChunks(JiggleCollider collider, int index, float4 color, ref float3 min, ref float3 max) {
        var position = collider.localToWorldMatrix.c3.xyz;
        switch (collider.type) {
            case JiggleCollider.JiggleColliderType.Sphere: {
                var r = collider.worldRadius;
                min = math.min(min, position - new float3(r));
                max = math.max(max, position + new float3(r));
                var scaleAdjust = float4x4.Scale(collider.radius * 2f);
                sphereChunks[index] = new JiggleRenderInstancer.GPUChunk {
                    matrix = math.mul(collider.localToWorldMatrix, scaleAdjust),
                    color = color
                };
                return index + 1;
            }
            case JiggleCollider.JiggleColliderType.Capsule: {
                var axisDir = collider.GetWorldAxis();
                var halfHeight = collider.worldHeight * 0.5f;
                var top = position + axisDir * halfHeight;
                var bottom = position - axisDir * halfHeight;
                var r = collider.worldRadius;
                min = math.min(min, math.min(top, bottom) - new float3(r));
                max = math.max(max, math.max(top, bottom) + new float3(r));
                var scaleAdjust = float4x4.Scale(collider.radius * 2f);
                // Top cap sphere.
                var topMatrix = collider.localToWorldMatrix;
                topMatrix.c3 = new float4(top, 1f);
                sphereChunks[index] = new JiggleRenderInstancer.GPUChunk {
                    matrix = math.mul(topMatrix, scaleAdjust),
                    color = color
                };
                // Bottom cap sphere.
                var bottomMatrix = collider.localToWorldMatrix;
                bottomMatrix.c3 = new float4(bottom, 1f);
                sphereChunks[index + 1] = new JiggleRenderInstancer.GPUChunk {
                    matrix = math.mul(bottomMatrix, scaleAdjust),
                    color = color
                };
                return index + 2;
            }
            default:
                return index;
        }
    }

    public void Dispose() {
        if (sphereCount.IsCreated) {
            sphereCount.Dispose();
        }

        if (sphereBounds.IsCreated) {
            sphereBounds.Dispose();
        }
    }
}
}
