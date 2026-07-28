using System;
using System.Runtime.InteropServices;
using GatorDragonGames.JigglePhysics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics {
public static class JiggleRenderer {
    private static NativeArray<JiggleRenderInstancer.GPUChunk> sphereChunks;
    private static NativeArray<JiggleRenderInstancer.GPUChunk> capsuleChunks;
    private static Bounds colliderBounds;
    private static int colliderCount;

    private static bool hasHandleRender;
    private static JobHandle handleRender;
    private static JiggleJobPrepareRender jobPrepareRender;
    private static JiggleRenderInstancer sphereInstancer;
    private static JiggleRenderInstancer capsuleInstancer;

    public static void OnEnable(JiggleJobs job) {
        sphereInstancer = new JiggleRenderInstancer();
        capsuleInstancer = new JiggleRenderInstancer();
        job.OnFinishSimulate += FlipData;
        jobPrepareRender = new JiggleJobPrepareRender() {
            personalColliders = job.GetPersonalColliders(out var _),
            sceneColliders = job.GetSceneColliders(out var _),
            outputPoses = job.GetInterpolatedOutputPoses(out var _),
            trees = job.GetTrees(out var _),
            sphereChunks = sphereChunks,
            sphereBounds = new NativeReference<Bounds>(Allocator.Persistent),
            sphereCount = new NativeReference<int>(Allocator.Persistent),
            capsuleChunks = capsuleChunks,
            capsuleBounds = new NativeReference<Bounds>(Allocator.Persistent),
            capsuleCount = new NativeReference<int>(Allocator.Persistent),
        };
    }

    private static float4 ColorToFloat4(Color color) {
        return new float4(color.r, color.g, color.b, color.a);
    }

    private static void FlipData(JiggleJobs job, double simulatedTime) {
        var sceneColliderCapacity = job.GetSceneColliderCapacity();
        var personalColliderCapacity = job.GetPersonalColliderCapacity();
        var transformCapacity = job.GetTransformCapcity();

        int desiredSphereCount = math.max(sceneColliderCapacity + personalColliderCapacity + transformCapacity, 1);
        int desiredCapsuleCount = math.max(sceneColliderCapacity + personalColliderCapacity, 1);

        if (!sphereChunks.IsCreated || sphereChunks.Length != desiredSphereCount) {
            var newSphereChunks = new NativeArray<JiggleRenderInstancer.GPUChunk>(desiredSphereCount, Allocator.Persistent);
            if (sphereChunks.IsCreated) {
                var oldLength = sphereChunks.Length;
                NativeArray<JiggleRenderInstancer.GPUChunk>.Copy(sphereChunks, newSphereChunks, oldLength);
                sphereChunks.Dispose();
            }
            sphereChunks = newSphereChunks;
        }

        if (!capsuleChunks.IsCreated || capsuleChunks.Length != desiredCapsuleCount) {
            var newCapsuleChunks = new NativeArray<JiggleRenderInstancer.GPUChunk>(desiredCapsuleCount, Allocator.Persistent);
            if (capsuleChunks.IsCreated) {
                var oldLength = capsuleChunks.Length;
                NativeArray<JiggleRenderInstancer.GPUChunk>.Copy(capsuleChunks, newCapsuleChunks, oldLength);
                capsuleChunks.Dispose();
            }
            capsuleChunks = newCapsuleChunks;
        }
    }

    public static void PrepareRender(JiggleJobs job) {
        if (!sphereChunks.IsCreated && !capsuleChunks.IsCreated) {
            return;
        }
        jobPrepareRender.sphereChunks = sphereChunks;
        jobPrepareRender.capsuleChunks = capsuleChunks;
        jobPrepareRender.personalColliders = job.GetPersonalColliders(out jobPrepareRender.personalColliderCount);
        jobPrepareRender.sceneColliders = job.GetSceneColliders(out jobPrepareRender.sceneColliderCount);
        jobPrepareRender.outputPoses = job.GetInterpolatedOutputPoses(out jobPrepareRender.transformCount);
        jobPrepareRender.trees = job.GetTrees(out jobPrepareRender.treeCount);
        // Latch only when the job actually got scheduled. Flagging unconditionally meant a frame
        // where the sim dependencies weren't ready still completed and re-rendered the previous
        // frame's chunk data, pinning gizmos at stale positions.
        if (job.TryGetRenderDependencies(out var handle)) {
            handleRender = jobPrepareRender.Schedule(handle);
            hasHandleRender = true;
        }
    }

    public static void FinishRender(Material gpuInstanceMaterial, Mesh sphere, Mesh capsule) {
        if (!hasHandleRender) {
            return;
        }
        handleRender.Complete();
        hasHandleRender = false;
        if (sphereChunks.IsCreated && sphere != null) {
            sphereInstancer.Render(jobPrepareRender.sphereBounds.Value, sphere, gpuInstanceMaterial, sphereChunks, jobPrepareRender.sphereCount.Value);
        }
        if (capsuleChunks.IsCreated && capsule != null) {
            capsuleInstancer.Render(jobPrepareRender.capsuleBounds.Value, capsule, gpuInstanceMaterial, capsuleChunks, jobPrepareRender.capsuleCount.Value);
        }
    }

    public static void Dispose() {
        if (hasHandleRender) {
            handleRender.Complete();
            hasHandleRender = false;
        }
        sphereInstancer?.Dispose();
        sphereInstancer = null;
        capsuleInstancer?.Dispose();
        capsuleInstancer = null;
        jobPrepareRender.Dispose();
        if (sphereChunks.IsCreated) {
            sphereChunks.Dispose();
        }
        if (capsuleChunks.IsCreated) {
            capsuleChunks.Dispose();
        }
    }
}

}
