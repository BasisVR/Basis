using System;
using Basis.EventDriver;
using UnityEngine;

namespace Basis.Shims
{
    /// <summary>
    /// Copies transform state between paired transforms natively, once per frame, on
    /// behalf of a Cilbox-sandboxed script.
    ///
    /// WHY THIS EXISTS: inside the interpreter every call out to Unity is a
    /// MethodBase.Invoke plus two heap allocations — Cilbox builds a fresh object[] and
    /// StackElement[] per call, and Mono then re-validates the same immutable MethodInfo
    /// on every invoke. That makes per-frame transform work brutally expensive from a
    /// sandboxed script: reading and writing local position + rotation costs five
    /// reflection invokes and roughly a dozen allocations PER TRANSFORM PER FRAME.
    /// Measured on a 223-transform avatar skeleton: ~1116 invokes and ~146 KB of garbage
    /// every frame, ~9 ms, of which only ~1 ms was the interpreter's own opcode loop and
    /// ~1 ms the actual Unity work. Everything else was reflection overhead.
    ///
    /// Hand the paired arrays across the boundary ONCE and run the loop natively and that
    /// cost goes to zero; the copy itself is tens of microseconds.
    ///
    /// GRANTS NO NEW AUTHORITY. A sandboxed script can already write position, rotation
    /// and scale on any Transform it holds a reference to — that is exactly what the slow
    /// version did, one reflection call at a time. This shim only changes the speed, and
    /// touches nothing but the transform channels you select.
    ///
    /// Ticks on <see cref="BasisEventDriver"/>, defaulting to OnLateUpdate, which fires
    /// after Basis's IK and bone drivers have posed avatars for the frame — so a follower
    /// lands on THIS frame's pose. A plain MonoBehaviour LateUpdate races those drivers
    /// and can be a frame stale depending on script order.
    ///
    /// Typical use from a sandboxed script:
    /// <code>
    ///   sync = new Basis.Shims.BasisTransformSyncShim();
    ///   sync.Channels = Basis.Shims.BasisTransformSyncShim.ChannelPose;
    ///   sync.BindTransforms(sourceTransforms, targetTransforms, this);
    /// </code>
    /// Blendshape weights are deliberately NOT handled here — see
    /// <see cref="BasisBlendShapeSyncShim"/>. They are split because the two have
    /// different threading futures: transform copying is jobifiable (Basis already drives
    /// paired transforms off the main thread via TransformAccessArray), while
    /// SetBlendShapeWeight is main-thread only. Keeping them separate is what lets
    /// scheduled transform work overlap main-thread blendshape work instead of serialising
    /// behind it. Set <see cref="Enabled"/> false and drive <see cref="Sync"/> yourself if
    /// you need to control that interleaving by hand today.
    ///
    /// Channel/space/phase selectors are const int masks rather than enums on purpose:
    /// a const inlines to a plain ldc.i4 in the caller's IL, so sandboxed callers need
    /// no enum type resolution, no boxing through Cilbox's enum machinery, and no extra
    /// link.xml preserve entry for a nested type.
    /// </summary>
    public sealed class BasisTransformSyncShim
    {
        // ---- Channels: which transform state to copy (bit mask) ----------------------

        /// <summary>Copy position.</summary>
        public const int ChannelPosition = 1;
        /// <summary>Copy rotation.</summary>
        public const int ChannelRotation = 2;
        /// <summary>Copy scale. Always LOCAL scale — Unity has no world-scale setter.</summary>
        public const int ChannelScale = 4;
        /// <summary>Position + rotation. The usual choice for pose following.</summary>
        public const int ChannelPose = ChannelPosition | ChannelRotation;
        /// <summary>Position + rotation + scale.</summary>
        public const int ChannelAll = ChannelPosition | ChannelRotation | ChannelScale;

        // ---- Space -------------------------------------------------------------------

        /// <summary>Copy parent-relative state. Correct when the target mirrors the source hierarchy.</summary>
        public const int SpaceLocal = 0;
        /// <summary>Copy world state. Use when source and target live in unrelated hierarchies.</summary>
        public const int SpaceWorld = 1;

        // ---- Tick phase --------------------------------------------------------------

        /// <summary>Run on BasisEventDriver.OnLateUpdate, after animation and IK. The default.</summary>
        public const int PhaseLateUpdate = 0;
        /// <summary>Run on BasisEventDriver.OnUpdate, before animation and IK.</summary>
        public const int PhaseUpdate = 1;

        // ---- Limits ------------------------------------------------------------------
        //
        // WORTH KNOWING, PLATFORM SIDE: native work is NOT covered by Cilbox's time
        // budget — its accounting measures interpreted instructions only. So moving a
        // loop behind a shim also moves it outside the ceiling that kept a runaway
        // sandboxed loop merely slow. These per-instance caps bound one binding; they do
        // NOT bound a script that constructs many shims. A real ceiling for that belongs
        // to the platform (a budget Basis owns and can attribute per prop), not to this
        // class, and is deliberately left as a policy decision rather than half-solved
        // here. An earlier draft kept a static cross-instance total, which was worse:
        // BasisEventDriver.ResetEventCallbacks is private, so there is no reliable hook to
        // reset it on scene teardown, and a leaked count would eventually refuse bindings
        // for every other prop in the session.

        /// <summary>Largest number of transform pairs a single shim will copy.</summary>
        public const int MaxPairs = 8192;

        // ---- State -------------------------------------------------------------------

        private Transform[] source = Array.Empty<Transform>();
        private Transform[] target = Array.Empty<Transform>();
        private int count;

        // Optional renderers to gate on: no copy on frames where none is on screen.
        private Renderer[] gate = Array.Empty<Renderer>();
        private int gateCount;

        // Destroyed alongside this object, the shim unhooks itself. Without it a caller
        // that forgets to Dispose would leave a subscription on a STATIC event forever,
        // pinning every bound transform in memory.
        private UnityEngine.Object owner;

        private int phase = PhaseLateUpdate;
        private bool hookedLate;
        private bool hookedUpdate;
        private bool disposed;

        // ---- Configuration -----------------------------------------------------------

        /// <summary>
        /// Which transform channels to copy — any combination of ChannelPosition,
        /// ChannelRotation and ChannelScale. Defaults to ChannelPose. Safe to change
        /// while bound; takes effect on the next frame.
        /// </summary>
        public int Channels { get; set; } = ChannelPose;

        /// <summary>
        /// SpaceLocal (default) or SpaceWorld. Applies to position and rotation; scale is
        /// always local because Unity exposes no world-scale setter.
        /// </summary>
        public int Space { get; set; } = SpaceLocal;

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

        /// <summary>Number of transform pairs currently bound.</summary>
        public int PairCount { get { return count; } }

        // ---- Binding -----------------------------------------------------------------

        /// <summary>
        /// Bind (or re-bind) the transform pairs to copy: target[i] takes source[i]'s
        /// state each frame, per <see cref="Channels"/> and <see cref="Space"/>. Arrays
        /// are COPIED, so the caller may rebuild or drop its own freely. Pairing is by
        /// index and the shorter length wins. Passing empty or null arrays unbinds the
        /// binding.
        /// </summary>
        /// <param name="lifetimeOwner">
        /// Object whose destruction ends the subscription — normally the calling
        /// behaviour itself (<c>this</c>). Pass null to opt out, in which case calling
        /// Dispose is mandatory.
        /// </param>
        public void BindTransforms(Transform[] sourceTransforms, Transform[] targetTransforms, UnityEngine.Object lifetimeOwner)
        {
            if (disposed)
            {
                return;
            }

            owner = lifetimeOwner;

            int pairs = 0;
            if (sourceTransforms != null && targetTransforms != null)
            {
                pairs = sourceTransforms.Length < targetTransforms.Length
                    ? sourceTransforms.Length
                    : targetTransforms.Length;
            }

            if (pairs > MaxPairs)
            {
                Debug.LogWarning("[BasisTransformSyncShim] " + pairs + " transform pairs requested; copying the first "
                    + MaxPairs + " only.");
                pairs = MaxPairs;
            }

            ClearPairs();

            if (pairs > 0)
            {
                // Private copies: the caller's arrays may be sandbox-owned and replaced
                // on its next rebuild while we are mid-iteration on a later frame.
                Transform[] src = new Transform[pairs];
                Transform[] dst = new Transform[pairs];
                Array.Copy(sourceTransforms, src, pairs);
                Array.Copy(targetTransforms, dst, pairs);
                source = src;
                target = dst;
                count = pairs;
            }

            UpdateHook();
        }


        /// <summary>
        /// Renderers to test before doing any work — the copy is skipped entirely on
        /// frames where none of them is being rendered by any camera. Pass null or an
        /// empty array to always copy. Cheap and worth setting for anything that can be
        /// off screen.
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
            ClearPairs();
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

        private void ClearPairs()
        {
            source = Array.Empty<Transform>();
            target = Array.Empty<Transform>();
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

            // Drop the wrong-phase subscription as well as adding the right one, so this
            // is correct regardless of which path got us here.
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
            // "owner was given and has since been destroyed", which Unity's == null
            // reports identically.
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

            CopyTransforms();
        }

        /// <summary>
        /// Run one copy right now, ignoring <see cref="Enabled"/> and the tick phase but
        /// still honouring the visibility gate. This is the seam for driving ordering by
        /// hand: set <see cref="Enabled"/> false so the automatic tick does nothing, then
        /// call this exactly where you want the work to land relative to other systems.
        /// Only valid on the main thread.
        /// </summary>
        public void Sync()
        {
            if (disposed || count == 0 || !AnyGateVisible())
            {
                return;
            }
            CopyTransforms();
        }

        private void CopyTransforms()
        {
            int pairs = count;
            if (pairs == 0)
            {
                return;
            }

            // Decide the shape of the work ONCE, outside the loop.
            int channels = Channels;
            bool doPosition = (channels & ChannelPosition) != 0;
            bool doRotation = (channels & ChannelRotation) != 0;
            bool doScale = (channels & ChannelScale) != 0;
            if (!doPosition && !doRotation && !doScale)
            {
                return;
            }
            bool world = Space == SpaceWorld;
            bool combined = doPosition && doRotation;

            Transform[] src = source;
            Transform[] dst = target;

            for (int i = 0; i < pairs; i++)
            {
                Transform s = src[i];
                Transform d = dst[i];
                if (s == null || d == null)
                {
                    continue;
                }

                if (combined)
                {
                    // One paired get/set beats two property round trips.
                    if (world)
                    {
                        s.GetPositionAndRotation(out Vector3 worldPosition, out Quaternion worldRotation);
                        d.SetPositionAndRotation(worldPosition, worldRotation);
                    }
                    else
                    {
                        s.GetLocalPositionAndRotation(out Vector3 localPosition, out Quaternion localRotation);
                        d.SetLocalPositionAndRotation(localPosition, localRotation);
                    }
                }
                else if (doPosition)
                {
                    if (world)
                    {
                        d.position = s.position;
                    }
                    else
                    {
                        d.localPosition = s.localPosition;
                    }
                }
                else if (doRotation)
                {
                    if (world)
                    {
                        d.rotation = s.rotation;
                    }
                    else
                    {
                        d.localRotation = s.localRotation;
                    }
                }

                if (doScale)
                {
                    // Local only: Transform exposes no world-scale setter (lossyScale is
                    // read-only), so there is nothing sane to do for SpaceWorld here.
                    d.localScale = s.localScale;
                }
            }
        }


        /// <summary>
        /// True when there is no gate, or when at least one gated renderer is being
        /// rendered by some camera this frame.
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
