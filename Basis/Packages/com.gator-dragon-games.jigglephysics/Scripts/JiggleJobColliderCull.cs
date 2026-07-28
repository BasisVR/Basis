using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace GatorDragonGames.JigglePhysics {

public struct JiggleColliderBroadPhaseEntry {
    public const byte StateSkip = 0;
    public const byte StateGlobal = 1;
    public const byte StateGrid = 2;

    public int2 minCell;
    public int2 maxCell;
    public byte state;
}

[BurstCompile]
public struct JiggleJobColliderCull : IJobParallelFor {
    [ReadOnly] public NativeArray<JiggleCollider> jiggleColliders;
    [ReadOnly] public NativeArray<JiggleCullingCamera> cullingCameras;
    [WriteOnly] public NativeArray<JiggleColliderBroadPhaseEntry> broadPhaseEntries;

    public int cullingCameraCount;
    public byte frustumCull;
    public byte distanceCull;
    public float maxCollisionDistance;
    public float nearKeepRadius;
    public float frustumMargin;
    public float inverseCellSize;
    public int maxColliderCellSpan;

    public JiggleJobColliderCull(JiggleMemoryBus bus) {
        jiggleColliders = bus.sceneColliders;
        broadPhaseEntries = bus.broadPhaseEntries;
        cullingCameras = default;
        cullingCameraCount = 0;
        frustumCull = 0;
        distanceCull = 0;
        maxCollisionDistance = 0f;
        nearKeepRadius = 0f;
        frustumMargin = 0f;
        inverseCellSize = JiggleSettings.InverseBroadPhaseCellSize;
        maxColliderCellSpan = JiggleSettings.MaxColliderCellSpan;
    }

    public void UpdateArrays(JiggleMemoryBus bus) {
        jiggleColliders = bus.sceneColliders;
        broadPhaseEntries = bus.broadPhaseEntries;
    }

    /// <summary>
    /// Derives the inner loop batch count from the work available and the pool that would run it,
    /// rather than a size tuned for one machine. Below one full batch per worker the whole range
    /// becomes a single batch, so one worker takes it instead of the pool splitting a few
    /// microseconds of work between dozens of threads. The job carries its dependencies either way,
    /// so this only decides how many workers wake, never whether the main thread waits.
    /// </summary>
    public static int GetBatchSize(int colliderCount, int workerCount, int minBatch) {
        if (colliderCount <= 0) {
            return 1;
        }
        var workers = math.max(workerCount, 1);
        var floor = math.max(minBatch, 1);
        if (colliderCount < floor * (long)workers) {
            return colliderCount;
        }
        // Deliberately constant rather than a share of the work. Cost per collider barely varies, so
        // the ideal batch is just the smallest one that amortises its own scheduling, and holding it
        // there lets the batch count grow with the workload: at 8192 colliders that lands on the
        // measured optimum, and it keeps handing the pool stealing headroom as the scene grows.
        // Sizing batches as a share of the count instead measured 25% slower at 32768.
        return floor;
    }

    public void Execute(int index) {
        var collider = jiggleColliders[index];
        if (!collider.enabled) {
            broadPhaseEntries[index] = default;
            return;
        }
        float3 position = collider.localToWorldMatrix.c3.xyz;
        if (IsColliderCulled(collider, position)) {
            broadPhaseEntries[index] = default;
            return;
        }
        // Planes are infinite, always go into the global cell
        if (collider.type == JiggleCollider.JiggleColliderType.Plane) {
            broadPhaseEntries[index] = new JiggleColliderBroadPhaseEntry() {
                state = JiggleColliderBroadPhaseEntry.StateGlobal,
            };
            return;
        }
        float3 aabbExtent;
        switch (collider.type) {
            case JiggleCollider.JiggleColliderType.Capsule: {
                var up = math.abs(collider.GetWorldAxis());
                aabbExtent = up * collider.worldHeight * 0.5f + new float3(collider.worldRadius);
                break;
            }
            default: // Sphere
                aabbExtent = new float3(collider.worldRadius);
                break;
        }
        int2 min = JiggleGridCell.GetKeyForPosition(position - aabbExtent, inverseCellSize);
        int2 max = JiggleGridCell.GetKeyForPosition(position + aabbExtent, inverseCellSize);
        long cellSpanX = (long)max.x - min.x + 1;
        long cellSpanY = (long)max.y - min.y + 1;
        var useGlobalCell = cellSpanX <= 0 || cellSpanY <= 0
            || cellSpanX > maxColliderCellSpan || cellSpanY > maxColliderCellSpan
            || cellSpanX * cellSpanY > maxColliderCellSpan;
        broadPhaseEntries[index] = new JiggleColliderBroadPhaseEntry() {
            minCell = min,
            maxCell = max,
            state = useGlobalCell ? JiggleColliderBroadPhaseEntry.StateGlobal : JiggleColliderBroadPhaseEntry.StateGrid,
        };
    }

    private bool IsColliderCulled(in JiggleCollider collider, float3 position) {
        if (cullingCameraCount == 0 || (frustumCull == 0 && distanceCull == 0)) {
            return false;
        }
        if (collider.type == JiggleCollider.JiggleColliderType.Plane) {
            return false;
        }
        var boundingRadius = collider.worldRadius;
        if (collider.type == JiggleCollider.JiggleColliderType.Capsule) {
            boundingRadius += collider.worldHeight * 0.5f;
        }
        var nearSq = nearKeepRadius * nearKeepRadius;
        var maxRange = maxCollisionDistance + boundingRadius;
        var maxSq = maxRange * maxRange;
        for (int c = 0; c < cullingCameraCount; c++) {
            var cam = cullingCameras[c];
            var distSq = math.distancesq(cam.position, position);
            if (distSq <= nearSq) {
                return false;
            }
            if (distanceCull != 0 && distSq > maxSq) {
                continue;
            }
            if (frustumCull == 0 || SphereInFrustum(cam, position, boundingRadius + frustumMargin)) {
                return false;
            }
        }
        return true;
    }

    private static bool SphereInFrustum(in JiggleCullingCamera cam, float3 center, float radius) {
        if (math.dot(cam.plane0.xyz, center) + cam.plane0.w < -radius) return false;
        if (math.dot(cam.plane1.xyz, center) + cam.plane1.w < -radius) return false;
        if (math.dot(cam.plane2.xyz, center) + cam.plane2.w < -radius) return false;
        if (math.dot(cam.plane3.xyz, center) + cam.plane3.w < -radius) return false;
        if (math.dot(cam.plane4.xyz, center) + cam.plane4.w < -radius) return false;
        if (math.dot(cam.plane5.xyz, center) + cam.plane5.w < -radius) return false;
        return true;
    }
}

}
