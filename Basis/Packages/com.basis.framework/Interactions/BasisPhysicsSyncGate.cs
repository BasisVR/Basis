using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// Coalesces the <see cref="Physics.SyncTransforms"/> calls owed by hand-moved colliders.
    /// <para>
    /// <c>Physics.autoSyncTransforms</c> is off project-wide, so anything that repositions a collider
    /// by writing its transform has to flush before the next scene query or that collider keeps
    /// answering from its old pose. The flush is global: it walks every dirty collider transform in
    /// the scene, which on a populated instance is dominated by avatar and jiggle churn that has
    /// nothing to do with the caller. Calling it eagerly from a mover therefore pays the whole
    /// scene's bill every frame, including frames where nothing a query cares about actually moved.
    /// </para>
    /// <para>
    /// Movers call <see cref="MarkColliderMoved"/> instead, and the query that needs fresh colliders
    /// calls <see cref="FlushIfDirty"/>. One flush per frame at most, and none at all while the
    /// movers are idle.
    /// </para>
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisPhysicsSyncGate
    {
        private static bool Dirty;

        /// <summary>
        /// Records that a collider was repositioned by a transform write and owes a flush before the
        /// next scene query reads it.
        /// </summary>
        public static void MarkColliderMoved()
        {
            Dirty = true;
        }

        /// <summary>
        /// Flushes pending collider moves into PhysX. A no-op, and free, when nothing was marked.
        /// </summary>
        public static void FlushIfDirty()
        {
            if (Dirty)
            {
                Dirty = false;
                Physics.SyncTransforms();
            }
        }
    }
}
