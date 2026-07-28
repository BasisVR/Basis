using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics {

/// <summary>
/// Tunables for the jiggle broad phase and collider culling. Settings come in two kinds. Startup
/// settings are captured into the job structs the first time the system simulates and cannot change
/// after that, so set them from Awake, from a bootstrap script, or any other code that runs before
/// the first jiggle rig simulates; assigning one later logs an error and leaves the value alone.
/// Live settings are re-read every frame and can be driven from a debug slider at any time.
/// </summary>
public static class JiggleSettings {
    private static bool booted;

    private static float broadPhaseCellSize = 0.25f;
    private static float inverseBroadPhaseCellSize = 1f / 0.25f;
    private static int maxColliderCellSpan = 400;
    private static int maxTreeCellSpan = 16384;
    private static int cellStalenessFrames = 3;
    private static bool cullingEnabled = true;
    private static float cullNearKeepRadius = 2.5f;
    private static float cullFrustumMargin = 0.5f;
    private static float cullFrustumExpansion = 1f;
    private static int colliderCullMinBatch = 64;
    private static int maxSubsteps = 2;

    internal static void ResetBootLatch() => booted = false;

    internal static void MarkBooted() => booted = true;

    private static bool RejectLateChange(string settingName) {
        if (!booted) {
            return false;
        }
        Debug.LogError($"JiggleSettings.{settingName} is a startup setting and was changed after the jiggle system began simulating. The change was ignored; set it before the first jiggle rig simulates.");
        return true;
    }

    /// <summary>
    /// Startup setting. Edge length in meters of one broad phase grid cell. Smaller cells mean fewer
    /// colliders tested per point, but more cells built and walked per collider and per jiggle tree.
    /// The insert side is the cheap side: over a 60 avatar scene, going from 0.5 to 0.25 cost 0.010ms
    /// more inserting and saved 0.150ms simulating, and 0.125 was cheaper again at roughly seven
    /// times the live cells. 0.25 takes most of the win without the memory. Sparse scenes with a few
    /// large colliders want the opposite, so this is worth sweeping per project.
    /// </summary>
    public static float BroadPhaseCellSize {
        get => broadPhaseCellSize;
        set {
            if (RejectLateChange(nameof(BroadPhaseCellSize))) {
                return;
            }
            broadPhaseCellSize = math.max(value, 0.01f);
            inverseBroadPhaseCellSize = 1f / broadPhaseCellSize;
        }
    }

    public static float InverseBroadPhaseCellSize => inverseBroadPhaseCellSize;

    /// <summary>
    /// Startup setting. A collider whose bounds cover more grid cells than this is placed in the
    /// global cell and tested against every jiggle point instead of being inserted per cell. Scales
    /// with the square of <see cref="BroadPhaseCellSize"/>, so halving cell size quadruples the span
    /// a given collider reports.
    ///
    /// Because of that scaling this is really a world area threshold wearing cell units: the collider
    /// footprint it admits is span * cellSize^2, so 400 at a cell size of 0.25 is the same 25 square
    /// meters that 100 was at 0.5. Rescale it by the same factor whenever cell size changes, or the
    /// threshold moves without anyone asking it to.
    ///
    /// Err high rather than low. The global cell is tested against every point of every tree, so
    /// pushing colliders into it is the expensive direction: forcing 127 colliders global measured
    /// 3.10ms against 0.60ms for the same scene gridded. Values from 4 up to 1024 were otherwise
    /// indistinguishable on a 60 avatar scene, since ordinary avatar colliders never approach it.
    /// </summary>
    public static int MaxColliderCellSpan {
        get => maxColliderCellSpan;
        set {
            if (RejectLateChange(nameof(MaxColliderCellSpan))) {
                return;
            }
            maxColliderCellSpan = math.max(value, 1);
        }
    }

    /// <summary>
    /// Startup setting. Upper bound on how many grid cells a single jiggle tree may walk while
    /// looking for colliders. Trees reporting a larger extent skip the grid walk entirely, which
    /// keeps corrupt or runaway point positions from stalling the simulation for seconds. Counted in
    /// cells, not metres, so it scales with the square of <see cref="BroadPhaseCellSize"/>: halving
    /// cell size quarters the physical area this covers. Raise it alongside any cell size decrease,
    /// or long legitimate trees will quietly stop seeing grid colliders.
    /// </summary>
    public static int MaxTreeCellSpan {
        get => maxTreeCellSpan;
        set {
            if (RejectLateChange(nameof(MaxTreeCellSpan))) {
                return;
            }
            maxTreeCellSpan = math.max(value, 1);
        }
    }

    /// <summary>
    /// Startup setting. How many simulation steps an empty grid cell is kept alive before its
    /// collider buffer is freed. Higher values trade memory for fewer allocations when colliders
    /// move back and forth across a cell boundary.
    /// </summary>
    public static int CellStalenessFrames {
        get => cellStalenessFrames;
        set {
            if (RejectLateChange(nameof(CellStalenessFrames))) {
                return;
            }
            cellStalenessFrames = math.max(value, 1);
        }
    }

    // All replaced by ColliderCullMinBatch: a fixed batch size and a fixed spread threshold are only
    // ever right for the core count they were measured on, and the cull now derives both from
    // JobsUtility.JobWorkerCount every frame instead. Sizing batches as a share of the work needed a
    // per-worker count too, but measured worse than holding the batch constant, so that went with it.
    //public static int ColliderCullBatchSize { ... }
    //public static int ColliderCullParallelThreshold { ... }
    //public static int ColliderCullBatchesPerWorker { ... }

    /// <summary>
    /// Startup setting. Smallest number of colliders worth handing to a worker as its own batch.
    /// Also sets the point where the cull stops spreading at all: below one full batch per worker
    /// the whole range goes to a single batch, because waking the pool costs more than the split
    /// saves. Measured on a 31 worker machine the spread schedule loses by 16x at 128 colliders and
    /// only overtakes a single batch at roughly 1500, which is what 64 x 31 predicts.
    /// </summary>
    public static int ColliderCullMinBatch {
        get => colliderCullMinBatch;
        set {
            if (RejectLateChange(nameof(ColliderCullMinBatch))) {
                return;
            }
            colliderCullMinBatch = math.max(value, 1);
        }
    }

    /// <summary>
    /// Live setting. Most fixed steps the simulation will run in a single frame when it has fallen
    /// behind. Each substep is a whole fixed step, so two of them advance the sim by exactly what two
    /// frames would: without this, a frame that owes two steps still only ever ran one, and jiggle
    /// quietly slowed down as the frame rate dropped.
    ///
    /// Costs a full simulate pass per substep, and the frames that need them are the ones already
    /// struggling, so this is capped rather than unbounded — past the cap the clock still advances
    /// and the sim simply accepts the drift instead of spiralling. 2 covers a 50Hz step down to 25fps.
    ///
    /// Setting it to 1 restores the previous behaviour exactly.
    /// </summary>
    public static int MaxSubsteps {
        get => maxSubsteps;
        set => maxSubsteps = math.max(value, 1);
    }

    /// <summary>
    /// Live setting. Master switch for collider culling. When false every collider is kept, whatever
    /// was passed to <see cref="JigglePhysics.SetCollisionCulling"/>, so culling can be turned off
    /// and back on without having to restore the individual frustum and distance flags.
    /// </summary>
    public static bool CullingEnabled {
        get => cullingEnabled;
        set => cullingEnabled = value;
    }

    /// <summary>
    /// Live setting. Colliders within this distance of a culling camera are never culled, regardless
    /// of the frustum and distance settings passed to
    /// <see cref="JigglePhysics.SetCollisionCulling"/>.
    /// </summary>
    public static float CullNearKeepRadius {
        get => cullNearKeepRadius;
        set => cullNearKeepRadius = math.max(value, 0f);
    }

    /// <summary>
    /// Live setting. Extra radius in meters added to a collider when testing it against the culling
    /// frustum. A fixed slack, so it matters most for colliders close to the camera.
    /// </summary>
    public static float CullFrustumMargin {
        get => cullFrustumMargin;
        set => cullFrustumMargin = math.max(value, 0f);
    }

    /// <summary>
    /// Live setting. Widens each culling camera's projection matrix by this factor before the
    /// frustum planes are extracted, so the kept area grows with distance instead of by a fixed
    /// amount. 1 uses the camera's own matrix, 1.2 keeps colliders 20% outside the view. Read when
    /// <see cref="JigglePhysics.SetCullingCameras"/> builds its cameras, so a change only takes
    /// effect on the next call. Works with off center and oblique projections, including VR eye
    /// matrices.
    /// </summary>
    public static float CullFrustumExpansion {
        get => cullFrustumExpansion;
        set => cullFrustumExpansion = math.max(value, 1f);
    }
}

}
