using System;
using Basis.EventDriver;
using UnityEngine;

namespace Basis.Shims
{
    /// <summary>
    /// Bulk skinned-mesh blendshape reads, writes and copies on behalf of a Cilbox-sandboxed
    /// script — a whole mesh of weights per call instead of one shape at a time. This is what
    /// carries face tracking, visemes and expressions onto a copied or mirrored avatar.
    ///
    /// WHY THIS EXISTS: inside the interpreter every call out to Unity is a MethodBase.Invoke plus
    /// two heap allocations — Cilbox builds a fresh object[] and StackElement[] per call, and Mono
    /// then re-validates the same immutable MethodInfo on every invoke. Blendshapes have no batch
    /// weight API, so a sandboxed script must call GetBlendShapeWeight once PER SHAPE PER FRAME:
    /// on an ARKit-style face that is hundreds of reflection invokes a frame for one mesh. Running
    /// the loop natively removes all of it and leaves one invoke.
    ///
    /// GRANTS NO NEW AUTHORITY. A sandboxed script can already read and write blendshape weights
    /// on any SkinnedMeshRenderer it holds a reference to — that is exactly what the slow version
    /// did, one reflection call at a time. Only the speed changes.
    ///
    /// THREE OPERATIONS, deliberately kept distinct rather than folded into one "sync" call:
    /// <list type="bullet">
    /// <item><see cref="GetWeights"/> — read a mesh's weights into your own float[].</item>
    /// <item><see cref="CopyWeights"/> — read one mesh and write another, with no intermediate
    /// storage. The fast path when you are only mirroring.</item>
    /// <item><see cref="SetWeights"/> — write your own float[] onto a mesh.</item>
    /// </list>
    /// Get and Set are what let a script do real work: pull 52 ARKit weights out with one invoke,
    /// blend or remap them in interpreted code where it is cheap because it never leaves the
    /// sandbox, then push the result back with one more. Copy exists because a plain mirror should
    /// not pay for the round trip through a managed array.
    ///
    /// All three are static and stateless — they work on any renderer you have, bound or not. The
    /// instance side on top of them (<see cref="BindMeshes"/> + the automatic tick) is for the
    /// common "mirror these meshes every frame" case, and adds a per-shape cache so a still face
    /// costs reads and no writes at all.
    ///
    /// WHEN IT TICKS: on <see cref="BasisEventDriver"/>, defaulting to
    /// <see cref="PhaseFrameSync"/>, which fires after face tracking, lipsync and expression
    /// drivers have set weights for the frame and before the render — so the copy is never a frame
    /// stale.
    ///
    /// SEPARATE FROM <see cref="BasisTransformSyncShim"/> ON PURPOSE. The two have different
    /// threading futures: paired transform copying is jobifiable and Basis already drives
    /// transforms off the main thread through TransformAccessArray, whereas SetBlendShapeWeight is
    /// main-thread only and always will be. Splitting them is what allows scheduled transform work
    /// to overlap this main-thread work rather than serialising behind it. Nothing in this class
    /// can move off the main thread.
    ///
    /// Typical use from a sandboxed script:
    /// <code>
    ///   faceSync = new Basis.Shims.BasisBlendShapeSyncShim();
    ///   faceSync.BindMeshes(sourceMeshes, targetMeshes, this);
    /// </code>
    /// Pass the FULL paired mesh arrays: the shim keeps only the meshes that actually carry
    /// blendshapes and allocates its own weight caches, so the caller precomputes nothing.
    ///
    /// Phase selectors are const int rather than an enum on purpose: a const inlines to a plain
    /// ldc.i4 in the caller's IL, so sandboxed callers need no enum type resolution, no boxing
    /// through Cilbox's enum machinery, and no extra link.xml preserve entry for a nested type.
    /// </summary>
    public sealed class BasisBlendShapeSyncShim : IBasisFrameSync
    {
        // ---- Tick phase --------------------------------------------------------------

        /// <summary>
        /// Run inside BasisEventDriver's LateUpdate, after the avatar network transmit and before
        /// JigglePhysics dispatches. The default: everything that drives a face for the frame has
        /// run, and the render has not happened yet.
        /// </summary>
        public const int PhaseFrameSync = 0;
        /// <summary>
        /// Run on BasisEventDriver.OnUpdate — the tail of Update, ahead of the LateUpdate blink,
        /// viseme-apply and expression work. Copies what the PREVIOUS frame's drivers left.
        /// </summary>
        public const int PhaseUpdate = 1;
        /// <summary>Run on BasisEventDriver.OnLateUpdate — the very end of the driver's LateUpdate.</summary>
        public const int PhaseLateUpdate = 2;

        // ---- Limits ------------------------------------------------------------------
        //
        // WORTH KNOWING, PLATFORM SIDE: native work is NOT covered by Cilbox's time budget — its
        // accounting measures interpreted instructions only. So moving a loop behind a shim also
        // moves it outside the ceiling that kept a runaway sandboxed loop merely slow. This
        // per-instance cap bounds one binding; the number of simultaneously ticking shims is
        // bounded separately by BasisFrameSyncRegistry.MaxEntries, which the driver resets on
        // teardown.

        /// <summary>Largest number of blendshape-bearing mesh pairs a single shim will copy.</summary>
        public const int MaxMeshes = 256;

        /// <summary>Default minimum weight change worth writing.</summary>
        public const float DefaultEpsilon = 0.01f;

        // ---- The three operations ----------------------------------------------------

        /// <summary>
        /// How many blendshapes <paramref name="mesh"/> has, or 0 if it has none or has been
        /// destroyed. Size your weight arrays with this — <see cref="GetWeights"/> and
        /// <see cref="SetWeights"/> clamp to whatever you pass, so a short array silently covers
        /// only its own length.
        /// </summary>
        public static int ShapeCount(SkinnedMeshRenderer mesh)
        {
            if (mesh == null)
            {
                return 0;
            }
            Mesh shared = mesh.sharedMesh;
            return shared == null ? 0 : shared.blendShapeCount;
        }

        /// <summary>
        /// Read <paramref name="mesh"/>'s blendshape weights into <paramref name="weights"/>, in
        /// shape-index order. One invoke for the whole face instead of one per shape.
        ///
        /// The count read is the lesser of the mesh's shape count and the array length, so neither
        /// side can be run off the end. A null or destroyed mesh reads nothing and returns 0.
        /// </summary>
        /// <returns>How many weights were written — use it to bound your own loop.</returns>
        public static int GetWeights(SkinnedMeshRenderer mesh, float[] weights)
        {
            if (mesh == null || weights == null)
            {
                return 0;
            }
            Mesh shared = mesh.sharedMesh;
            if (shared == null)
            {
                return 0;
            }

            int shapes = shared.blendShapeCount;
            if (weights.Length < shapes)
            {
                shapes = weights.Length;
            }
            if (shapes <= 0)
            {
                return 0;
            }

            for (int Index = 0; Index < shapes; Index++)
            {
                weights[Index] = mesh.GetBlendShapeWeight(Index);
            }
            return shapes;
        }

        /// <summary>
        /// Write <paramref name="weights"/> onto <paramref name="mesh"/>, in shape-index order.
        /// The exact mirror of <see cref="GetWeights"/> and clamped the same way.
        ///
        /// <paramref name="epsilon"/> at or below 0 writes every shape unconditionally, which is
        /// the cheapest thing to do when you know the values changed. Above 0, each shape's
        /// current weight is read back first and the write is skipped when it would move the
        /// weight by less than that — worth it when most shapes are still, since SetBlendShapeWeight
        /// dirties the skinned mesh and a read does not. If you are mirroring rather than
        /// computing, prefer the bound instance path, which caches the last value written and gets
        /// the same skip without the read-back.
        /// </summary>
        /// <returns>How many shapes were considered.</returns>
        public static int SetWeights(SkinnedMeshRenderer mesh, float[] weights, float epsilon)
        {
            if (mesh == null || weights == null)
            {
                return 0;
            }
            Mesh shared = mesh.sharedMesh;
            if (shared == null)
            {
                return 0;
            }

            int shapes = shared.blendShapeCount;
            if (weights.Length < shapes)
            {
                shapes = weights.Length;
            }
            if (shapes <= 0)
            {
                return 0;
            }

            if (epsilon <= 0f)
            {
                for (int Index = 0; Index < shapes; Index++)
                {
                    mesh.SetBlendShapeWeight(Index, weights[Index]);
                }
                return shapes;
            }

            for (int Index = 0; Index < shapes; Index++)
            {
                float weight = weights[Index];
                float delta = weight - mesh.GetBlendShapeWeight(Index);
                if (delta > epsilon || delta < -epsilon)
                {
                    mesh.SetBlendShapeWeight(Index, weight);
                }
            }
            return shapes;
        }

        /// <summary>
        /// Get from one mesh and set on another in a single pass. Shapes are matched by index,
        /// which is exact when the target is a clone of the source (shared Mesh); if the two
        /// disagree on shape count the lower count wins.
        ///
        /// Prefer this over <see cref="GetWeights"/> followed by <see cref="SetWeights"/> whenever
        /// you are not modifying the weights in between — it skips the intermediate array
        /// entirely. <paramref name="epsilon"/> works as it does on <see cref="SetWeights"/>: at
        /// or below 0 every shape is written, above 0 the destination is read back and unchanged
        /// shapes are left alone.
        /// </summary>
        /// <returns>How many shapes were considered.</returns>
        public static int CopyWeights(SkinnedMeshRenderer from, SkinnedMeshRenderer to, float epsilon)
        {
            if (from == null || to == null)
            {
                return 0;
            }
            Mesh sourceMesh = from.sharedMesh;
            Mesh targetMesh = to.sharedMesh;
            if (sourceMesh == null || targetMesh == null)
            {
                return 0;
            }

            int sourceShapes = sourceMesh.blendShapeCount;
            int targetShapes = targetMesh.blendShapeCount;
            int shapes = sourceShapes < targetShapes ? sourceShapes : targetShapes;
            if (shapes <= 0)
            {
                return 0;
            }

            if (epsilon <= 0f)
            {
                for (int Index = 0; Index < shapes; Index++)
                {
                    to.SetBlendShapeWeight(Index, from.GetBlendShapeWeight(Index));
                }
                return shapes;
            }

            for (int Index = 0; Index < shapes; Index++)
            {
                float weight = from.GetBlendShapeWeight(Index);
                float delta = weight - to.GetBlendShapeWeight(Index);
                if (delta > epsilon || delta < -epsilon)
                {
                    to.SetBlendShapeWeight(Index, weight);
                }
            }
            return shapes;
        }

        // ---- State -------------------------------------------------------------------

        // Only meshes that actually carry shapes are kept, and cache holds the last weight
        // written per shape so a still face costs reads and nothing else.
        private SkinnedMeshRenderer[] source = Array.Empty<SkinnedMeshRenderer>();
        private SkinnedMeshRenderer[] target = Array.Empty<SkinnedMeshRenderer>();
        private float[][] cache = Array.Empty<float[]>();
        private int count;

        // Optional renderers to gate on: no copy on frames where none is on screen.
        private Renderer[] gate = Array.Empty<Renderer>();
        private int gateCount;

        // Destroyed alongside this object, the shim unhooks itself. Without it a caller that
        // forgets to Dispose would leave a registration alive forever, pinning every bound
        // renderer in memory.
        private UnityEngine.Object owner;

        private int phase = PhaseFrameSync;
        private int hookedPhase = -1;
        private bool disposed;

        // ---- Configuration -----------------------------------------------------------

        /// <summary>
        /// Minimum weight change worth writing on the automatic tick. Compared against the shim's
        /// own cache of what it last wrote, so 0 writes whenever a weight moves at all; the
        /// default ~0.01 skips imperceptible jitter. Negative values are treated as 0. Safe to
        /// change while bound.
        /// </summary>
        public float Epsilon = DefaultEpsilon;

        /// <summary>Set false to pause the automatic tick without unbinding.</summary>
        public bool Enabled = true;

        /// <summary>
        /// PhaseFrameSync (default), PhaseUpdate or PhaseLateUpdate. Changing this re-subscribes
        /// immediately.
        /// </summary>
        public int Phase
        {
            get { return phase; }
            set
            {
                if (phase == value)
                {
                    return;
                }
                phase = value;
                UpdateHook();
            }
        }

        /// <summary>Number of blendshape-bearing mesh pairs currently bound.</summary>
        public int MeshCount { get { return count; } }

        // ---- Binding -----------------------------------------------------------------

        /// <summary>
        /// Bind (or re-bind) the mesh pairs the automatic tick mirrors. Pass the FULL paired
        /// arrays — meshes with no blendshapes are dropped here, once, rather than being walked
        /// every frame. Pairing is by index and the shorter length wins; shapes are matched by
        /// index too, which is exact when the target is a clone of the source (shared Mesh), and
        /// if the two meshes disagree on shape count the lower count wins.
        ///
        /// Weights are synced in full once here, so the target starts correct rather than waiting
        /// for the first shape to move.
        ///
        /// You do not need to bind to use <see cref="GetWeights"/>, <see cref="SetWeights"/> or
        /// <see cref="CopyWeights"/> — those are static and take whatever renderer you hand them.
        /// Binding buys you the automatic per-frame tick, the change cache and the lifetime
        /// cleanup.
        /// </summary>
        /// <param name="lifetimeOwner">
        /// Object whose destruction ends the subscription — normally the calling behaviour itself
        /// (<c>this</c>). Pass null to opt out, in which case calling Dispose is mandatory.
        /// </param>
        public void BindMeshes(SkinnedMeshRenderer[] sourceMeshes, SkinnedMeshRenderer[] targetMeshes, UnityEngine.Object lifetimeOwner)
        {
            if (disposed)
            {
                return;
            }

            owner = lifetimeOwner;
            Clear();

            if (sourceMeshes == null || targetMeshes == null)
            {
                UpdateHook();
                return;
            }

            int pairs = sourceMeshes.Length < targetMeshes.Length
                ? sourceMeshes.Length
                : targetMeshes.Length;
            if (pairs <= 0)
            {
                UpdateHook();
                return;
            }

            SkinnedMeshRenderer[] src = new SkinnedMeshRenderer[pairs];
            SkinnedMeshRenderer[] dst = new SkinnedMeshRenderer[pairs];
            float[][] caches = new float[pairs][];
            int kept = 0;
            bool capped = false;

            for (int Index = 0; Index < pairs; Index++)
            {
                if (kept >= MaxMeshes)
                {
                    capped = true;
                    break;
                }

                SkinnedMeshRenderer s = sourceMeshes[Index];
                SkinnedMeshRenderer d = targetMeshes[Index];
                if (s == null || d == null)
                {
                    continue;
                }

                Mesh sourceMesh = s.sharedMesh;
                Mesh targetMesh = d.sharedMesh;
                if (sourceMesh == null || targetMesh == null)
                {
                    continue;
                }

                int sourceShapes = sourceMesh.blendShapeCount;
                int targetShapes = targetMesh.blendShapeCount;
                int shapes = sourceShapes < targetShapes ? sourceShapes : targetShapes;
                if (shapes == 0)
                {
                    continue;
                }

                // Initial full sync doubles as cache seeding: no sentinel value needed, and the
                // target is correct from the frame it is bound.
                float[] weights = new float[shapes];
                for (int b = 0; b < shapes; b++)
                {
                    float weight = s.GetBlendShapeWeight(b);
                    d.SetBlendShapeWeight(b, weight);
                    weights[b] = weight;
                }

                src[kept] = s;
                dst[kept] = d;
                caches[kept] = weights;
                kept++;
            }

            if (capped)
            {
                BasisDebug.LogWarning($"[BasisBlendShapeSyncShim] More than {MaxMeshes} blendshape meshes bound; copying the first {MaxMeshes} only.", BasisDebug.LogTag.Shims);
            }

            if (kept > 0)
            {
                if (kept != pairs)
                {
                    Array.Resize(ref src, kept);
                    Array.Resize(ref dst, kept);
                    Array.Resize(ref caches, kept);
                }
                source = src;
                target = dst;
                cache = caches;
                count = kept;
            }

            UpdateHook();
        }

        /// <summary>
        /// Renderers to test before the automatic tick does any work — the copy is skipped
        /// entirely on frames where none of them is being rendered by any camera. Pass null or an
        /// empty array to always copy. Worth setting for anything that can be off screen, since a
        /// face nobody is looking at still costs a read per shape.
        /// </summary>
        public void SetVisibilityGate(Renderer[] renderers)
        {
            if (disposed)
            {
                return;
            }
            if (renderers == null || renderers.Length == 0)
            {
                gate = Array.Empty<Renderer>();
                gateCount = 0;
                return;
            }
            Renderer[] g = new Renderer[renderers.Length];
            Array.Copy(renderers, g, renderers.Length);
            gate = g;
            gateCount = g.Length;
        }

        /// <summary>Stop the automatic tick and drop everything bound, keeping the shim reusable.</summary>
        public void Unbind()
        {
            Clear();
            gate = Array.Empty<Renderer>();
            gateCount = 0;
            UpdateHook();
        }

        /// <summary>Unbind and refuse further binds. Idempotent.</summary>
        public void Dispose()
        {
            disposed = true;
            Unbind();
            owner = null;
        }

        private void Clear()
        {
            source = Array.Empty<SkinnedMeshRenderer>();
            target = Array.Empty<SkinnedMeshRenderer>();
            cache = Array.Empty<float[]>();
            count = 0;
        }

        // ---- Bound-mesh conveniences -------------------------------------------------

        /// <summary>
        /// <see cref="GetWeights"/> over one of the bound SOURCE meshes. <paramref name="meshIndex"/>
        /// runs 0..<see cref="MeshCount"/>-1 and indexes the KEPT pairs, not the arrays you passed
        /// to <see cref="BindMeshes"/> — shapeless meshes were dropped at bind time. Out of range
        /// reads nothing and returns 0.
        /// </summary>
        public int GetSourceWeights(int meshIndex, float[] weights)
        {
            if (meshIndex < 0 || meshIndex >= count)
            {
                return 0;
            }
            return GetWeights(source[meshIndex], weights);
        }

        /// <summary>
        /// <see cref="SetWeights"/> over one of the bound TARGET meshes, using <see cref="Epsilon"/>.
        /// Pairs with <see cref="GetSourceWeights"/> for read-modify-write. Note this writes past
        /// the shim's cache, so the next automatic tick will still push whatever the source says —
        /// set <see cref="Enabled"/> false if you want to own the target's weights outright.
        /// </summary>
        public int SetTargetWeights(int meshIndex, float[] weights)
        {
            if (meshIndex < 0 || meshIndex >= count)
            {
                return 0;
            }
            return SetWeights(target[meshIndex], weights, Epsilon);
        }

        /// <summary>
        /// Run one copy of the bound meshes right now, ignoring <see cref="Enabled"/> and the tick
        /// phase but still honouring the visibility gate. This is the seam for driving ordering by
        /// hand: set <see cref="Enabled"/> false so the automatic tick does nothing, then call this
        /// exactly where you want the work to land — for instance after scheduling transform work,
        /// so the two overlap. Main thread only.
        /// </summary>
        public int Sync()
        {
            if (disposed || count == 0 || !AnyGateVisible())
            {
                return 0;
            }
            return CopyCachedWeights();
        }

        // ---- Ticking -----------------------------------------------------------------

        /// <summary>
        /// Subscribe only while there is something to copy, so an idle or unbound shim costs the
        /// event driver nothing. Drops the previous phase before taking the new one, so this is
        /// correct regardless of which path got us here.
        /// </summary>
        private void UpdateHook()
        {
            int wanted = disposed || count == 0 ? -1 : phase;
            if (wanted == hookedPhase)
            {
                return;
            }

            switch (hookedPhase)
            {
                case PhaseFrameSync: BasisFrameSyncRegistry.Unregister(this); break;
                case PhaseUpdate: BasisEventDriver.OnUpdate -= HandleTick; break;
                case PhaseLateUpdate: BasisEventDriver.OnLateUpdate -= HandleTick; break;
            }
            hookedPhase = -1;

            switch (wanted)
            {
                case PhaseFrameSync:
                    if (!BasisFrameSyncRegistry.Register(this))
                    {
                        return;
                    }
                    break;
                case PhaseUpdate: BasisEventDriver.OnUpdate += HandleTick; break;
                case PhaseLateUpdate: BasisEventDriver.OnLateUpdate += HandleTick; break;
                default: return;
            }
            hookedPhase = wanted;
        }

        /// <summary>Driver entry point for <see cref="PhaseFrameSync"/>.</summary>
        public void FrameSync()
        {
            HandleTick();
        }

        private void HandleTick()
        {
            // Owner destroyed and nobody called Dispose: clean up after them. The ReferenceEquals
            // guard distinguishes "no owner was given" (opted out) from "owner was given and has
            // since been destroyed", which Unity's == null reports identically.
            if (!ReferenceEquals(owner, null) && owner == null)
            {
                Dispose();
                return;
            }

            if (disposed || !Enabled || count == 0)
            {
                return;
            }

            if (!AnyGateVisible())
            {
                return;
            }

            CopyCachedWeights();
        }

        /// <summary>
        /// The bound copy. Only shapes that actually moved are written: the read is unavoidable
        /// (there is no batch weight API) but a still face then costs nothing but reads.
        /// </summary>
        private int CopyCachedWeights()
        {
            int meshes = count;
            if (meshes <= 0)
            {
                return 0;
            }

            SkinnedMeshRenderer[] src = source;
            SkinnedMeshRenderer[] dst = target;
            float[][] caches = cache;
            float epsilon = Epsilon;
            if (epsilon < 0f)
            {
                epsilon = 0f;
            }

            for (int m = 0; m < meshes; m++)
            {
                SkinnedMeshRenderer s = src[m];
                SkinnedMeshRenderer d = dst[m];
                if (s == null || d == null)
                {
                    continue;
                }

                // Re-clamp against the live meshes every frame. Either renderer can have its
                // sharedMesh swapped for one with fewer shapes after binding, and a stale index
                // into GetBlendShapeWeight throws — one exception per frame, forever.
                Mesh sourceMesh = s.sharedMesh;
                Mesh targetMesh = d.sharedMesh;
                if (sourceMesh == null || targetMesh == null)
                {
                    continue;
                }

                float[] weights = caches[m];
                int shapes = weights.Length;
                int sourceShapes = sourceMesh.blendShapeCount;
                int targetShapes = targetMesh.blendShapeCount;
                if (sourceShapes < shapes)
                {
                    shapes = sourceShapes;
                }
                if (targetShapes < shapes)
                {
                    shapes = targetShapes;
                }
                if (shapes <= 0)
                {
                    continue;
                }

                for (int b = 0; b < shapes; b++)
                {
                    float weight = s.GetBlendShapeWeight(b);
                    float delta = weight - weights[b];
                    if (delta > epsilon || delta < -epsilon)
                    {
                        d.SetBlendShapeWeight(b, weight);
                        weights[b] = weight;
                    }
                }
            }
            return meshes;
        }

        /// <summary>
        /// True when there is no gate, or when at least one gated renderer is being rendered by
        /// some camera this frame.
        /// </summary>
        private bool AnyGateVisible()
        {
            int gates = gateCount;
            if (gates == 0)
            {
                return true;
            }
            Renderer[] renderers = gate;
            for (int Index = 0; Index < gates; Index++)
            {
                Renderer renderer = renderers[Index];
                if (renderer != null && renderer.isVisible)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
