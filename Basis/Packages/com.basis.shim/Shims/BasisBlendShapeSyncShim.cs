using System;
using Basis.EventDriver;
using UnityEngine;

namespace Basis.Shims
{
    /// <summary>
    /// Copies skinned-mesh blendshape weights between paired renderers natively, once per
    /// frame, on behalf of a Cilbox-sandboxed script. This is what carries face tracking,
    /// visemes and expressions onto a copied or mirrored avatar.
    ///
    /// WHY THIS EXISTS: inside the interpreter every call out to Unity is a
    /// MethodBase.Invoke plus two heap allocations — Cilbox builds a fresh object[] and
    /// StackElement[] per call, and Mono then re-validates the same immutable MethodInfo
    /// on every invoke. Blendshapes have no batch weight API, so a sandboxed script must
    /// call GetBlendShapeWeight once PER SHAPE PER FRAME: on an ARKit-style face that is
    /// hundreds of reflection invokes a frame for one mesh. Handing the paired renderers
    /// across the boundary once and running the loop natively removes all of it.
    ///
    /// GRANTS NO NEW AUTHORITY. A sandboxed script can already read and write blendshape
    /// weights on any SkinnedMeshRenderer it holds a reference to — that is exactly what
    /// the slow version did, one reflection call at a time. Only the speed changes.
    ///
    /// SEPARATE FROM <see cref="BasisTransformSyncShim"/> ON PURPOSE. The two have
    /// different threading futures: paired transform copying is jobifiable and Basis
    /// already drives transforms off the main thread through TransformAccessArray, whereas
    /// SetBlendShapeWeight is main-thread only and always will be. Splitting them is what
    /// allows scheduled transform work to overlap this main-thread work rather than
    /// serialising behind it. Nothing in this class can move off the main thread.
    ///
    /// Ticks on <see cref="BasisEventDriver"/>, defaulting to OnLateUpdate, which fires
    /// after face tracking, lipsync and expression drivers have set weights for the frame,
    /// so the copy is never a frame stale.
    ///
    /// Typical use from a sandboxed script:
    /// <code>
    ///   faceSync = new Basis.Shims.BasisBlendShapeSyncShim();
    ///   faceSync.BindMeshes(sourceMeshes, targetMeshes, this);
    /// </code>
    /// Pass the FULL paired mesh arrays: the shim keeps only the meshes that actually carry
    /// blendshapes and allocates its own weight caches, so the caller precomputes nothing.
    ///
    /// Phase selectors are const int rather than an enum on purpose: a const inlines to a
    /// plain ldc.i4 in the caller's IL, so sandboxed callers need no enum type resolution,
    /// no boxing through Cilbox's enum machinery, and no extra link.xml preserve entry for
    /// a nested type.
    /// </summary>
    public sealed class BasisBlendShapeSyncShim
    {
        // ---- Tick phase --------------------------------------------------------------

        /// <summary>Run on BasisEventDriver.OnLateUpdate, after face and viseme drivers. The default.</summary>
        public const int PhaseLateUpdate = 0;
        /// <summary>Run on BasisEventDriver.OnUpdate, before them.</summary>
        public const int PhaseUpdate = 1;

        // ---- Limits ------------------------------------------------------------------
        //
        // WORTH KNOWING, PLATFORM SIDE: native work is NOT covered by Cilbox's time
        // budget — its accounting measures interpreted instructions only. So moving a loop
        // behind a shim also moves it outside the ceiling that kept a runaway sandboxed
        // loop merely slow. This per-instance cap bounds one binding; it does NOT bound a
        // script that constructs many shims. A real ceiling for that belongs to the
        // platform, where it can be attributed per prop.

        /// <summary>Largest number of blendshape-bearing mesh pairs a single shim will copy.</summary>
        public const int MaxMeshes = 256;

        /// <summary>Default minimum weight change worth writing.</summary>
        public const float DefaultEpsilon = 0.01f;

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

        // Destroyed alongside this object, the shim unhooks itself. Without it a caller
        // that forgets to Dispose would leave a subscription on a STATIC event forever,
        // pinning every bound renderer in memory.
        private UnityEngine.Object owner;

        private int phase = PhaseLateUpdate;
        private bool hookedLate;
        private bool hookedUpdate;
        private bool disposed;

        // ---- Configuration -----------------------------------------------------------

        /// <summary>
        /// Minimum weight change worth writing. 0 writes on any change; the default ~0.01
        /// skips imperceptible jitter. Negative values are treated as 0. Safe to change
        /// while bound.
        /// </summary>
        public float Epsilon { get; set; } = DefaultEpsilon;

        /// <summary>
        /// PhaseLateUpdate (default) or PhaseUpdate. Changing this re-subscribes
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
                Unhook();
                UpdateHook();
            }
        }

        /// <summary>Set false to pause copying without unbinding.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Number of blendshape-bearing mesh pairs currently bound.</summary>
        public int MeshCount { get { return count; } }

        // ---- Binding -----------------------------------------------------------------

        /// <summary>
        /// Bind (or re-bind) the mesh pairs to mirror. Pass the FULL paired arrays — meshes
        /// with no blendshapes are dropped here, once, rather than being walked every
        /// frame. Pairing is by index and the shorter length wins; shapes are matched by
        /// index too, which is exact when the target is a clone of the source (shared
        /// Mesh), and if the two meshes disagree on shape count the lower count wins.
        ///
        /// Weights are synced in full once here, so the target starts correct rather than
        /// waiting for the first shape to move.
        /// </summary>
        /// <param name="lifetimeOwner">
        /// Object whose destruction ends the subscription — normally the calling behaviour
        /// itself (<c>this</c>). Pass null to opt out, in which case calling Dispose is
        /// mandatory.
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

            SkinnedMeshRenderer[] src = new SkinnedMeshRenderer[pairs];
            SkinnedMeshRenderer[] dst = new SkinnedMeshRenderer[pairs];
            float[][] caches = new float[pairs][];
            int kept = 0;
            bool capped = false;

            for (int i = 0; i < pairs; i++)
            {
                if (kept >= MaxMeshes)
                {
                    capped = true;
                    break;
                }

                SkinnedMeshRenderer s = sourceMeshes[i];
                SkinnedMeshRenderer d = targetMeshes[i];
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

                // Initial full sync doubles as cache seeding: no sentinel value needed, and
                // the target is correct from the frame it is bound.
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
                Debug.LogWarning("[BasisBlendShapeSyncShim] More than " + MaxMeshes
                    + " blendshape meshes bound; copying the first " + MaxMeshes + " only.");
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
        /// Renderers to test before doing any work — the copy is skipped entirely on frames
        /// where none of them is being rendered by any camera. Pass null or an empty array
        /// to always copy. Worth setting for anything that can be off screen, since a face
        /// nobody is looking at still costs a read per shape.
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

        /// <summary>Stop copying and drop everything bound, keeping the shim reusable.</summary>
        public void Unbind()
        {
            Clear();
            gate = Array.Empty<Renderer>();
            gateCount = 0;
            Unhook();
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

        // ---- Ticking -----------------------------------------------------------------

        /// <summary>
        /// Subscribe only while there is something to copy, so an idle or unbound shim
        /// costs the event driver nothing.
        /// </summary>
        private void UpdateHook()
        {
            if (disposed || count == 0)
            {
                Unhook();
                return;
            }

            // Drop the wrong-phase subscription as well as adding the right one, so this is
            // correct regardless of which path got us here.
            if (phase == PhaseUpdate)
            {
                if (hookedLate)
                {
                    BasisEventDriver.OnLateUpdate -= HandleTick;
                    hookedLate = false;
                }
                if (!hookedUpdate)
                {
                    BasisEventDriver.OnUpdate += HandleTick;
                    hookedUpdate = true;
                }
            }
            else
            {
                if (hookedUpdate)
                {
                    BasisEventDriver.OnUpdate -= HandleTick;
                    hookedUpdate = false;
                }
                if (!hookedLate)
                {
                    BasisEventDriver.OnLateUpdate += HandleTick;
                    hookedLate = true;
                }
            }
        }

        private void Unhook()
        {
            if (hookedLate)
            {
                BasisEventDriver.OnLateUpdate -= HandleTick;
                hookedLate = false;
            }
            if (hookedUpdate)
            {
                BasisEventDriver.OnUpdate -= HandleTick;
                hookedUpdate = false;
            }
        }

        private void HandleTick()
        {
            // Owner destroyed and nobody called Dispose: clean up after them. The
            // ReferenceEquals guard distinguishes "no owner was given" (opted out) from
            // "owner was given and has since been destroyed", which Unity's == null reports
            // identically.
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

            CopyWeights();
        }

        /// <summary>
        /// Run one copy right now, ignoring <see cref="Enabled"/> and the tick phase but
        /// still honouring the visibility gate. This is the seam for driving ordering by
        /// hand: set <see cref="Enabled"/> false so the automatic tick does nothing, then
        /// call this exactly where you want the work to land — for instance after
        /// scheduling transform jobs, so the two overlap. Main thread only.
        /// </summary>
        public void Sync()
        {
            if (disposed || count == 0 || !AnyGateVisible())
            {
                return;
            }
            CopyWeights();
        }

        /// <summary>
        /// Only shapes that actually moved are written: the read is unavoidable (there is
        /// no batch weight API) but a still face then costs nothing but reads.
        /// </summary>
        private void CopyWeights()
        {
            int meshes = count;
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

                float[] weights = caches[m];
                int shapes = weights.Length;
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
        }

        /// <summary>
        /// True when there is no gate, or when at least one gated renderer is being rendered
        /// by some camera this frame.
        /// </summary>
        private bool AnyGateVisible()
        {
            int gates = gateCount;
            if (gates == 0)
            {
                return true;
            }
            Renderer[] renderers = gate;
            for (int i = 0; i < gates; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer != null && renderer.isVisible)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
