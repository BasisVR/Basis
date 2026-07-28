using System;
using Basis.EventDriver;
using UnityEngine;

namespace Basis.Shims
{
    /// <summary>
    /// Bulk transform reads, writes and copies on behalf of a Cilbox-sandboxed script — a whole
    /// array of transforms per call instead of one property access at a time.
    ///
    /// WHY THIS EXISTS: inside the interpreter every call out to Unity is a MethodBase.Invoke
    /// plus two heap allocations — Cilbox builds a fresh object[] and StackElement[] per call, and
    /// Mono then re-validates the same immutable MethodInfo on every invoke. That makes per-frame
    /// transform work brutally expensive from a sandboxed script: reading and writing local
    /// position + rotation costs five reflection invokes and roughly a dozen allocations PER
    /// TRANSFORM PER FRAME. Measured on a 223-transform avatar skeleton: ~1116 invokes and
    /// ~146 KB of garbage every frame, ~9 ms, of which only ~1 ms was the interpreter's own opcode
    /// loop and ~1 ms the actual Unity work. Everything else was reflection overhead.
    ///
    /// Hand the arrays across the boundary ONCE and run the loop natively and that cost goes to
    /// one invoke; the loop itself is tens of microseconds.
    ///
    /// GRANTS NO NEW AUTHORITY. A sandboxed script can already read and write position, rotation
    /// and scale on any Transform it holds a reference to — that is exactly what the slow version
    /// did, one reflection call at a time. This shim only changes the speed, and touches nothing
    /// but the channels you select on the transforms you hand it.
    ///
    /// THREE OPERATIONS, deliberately kept distinct rather than folded into one "sync" call:
    /// <list type="bullet">
    /// <item><see cref="GetValues"/> — read an array of transforms into your own arrays.</item>
    /// <item><see cref="CopyValues"/> — read one array of transforms and write another, with no
    /// intermediate storage. The fast path when you are only mirroring.</item>
    /// <item><see cref="SetValues"/> — write your own arrays onto an array of transforms.</item>
    /// </list>
    /// Get and Set are what let a script do real work: pull 223 bone poses into a Vector3[] and a
    /// Quaternion[] with one invoke, do the maths in interpreted code where it is cheap because it
    /// never leaves the sandbox, then push the result back with one more invoke. Copy exists
    /// because a plain mirror should not pay for the round trip through managed arrays.
    ///
    /// All three are static and stateless — they work on any arrays you have, bound or not. The
    /// instance side on top of them (<see cref="BindTransforms"/> + the automatic tick) is only
    /// for the common "mirror these pairs every frame" case, so it does not need driving by hand.
    ///
    /// WHEN IT TICKS: on <see cref="BasisEventDriver"/>, defaulting to
    /// <see cref="PhaseFrameSync"/> — the point in LateUpdate after the avatar transmit and
    /// before JigglePhysics dispatches, which is the one window where an arbitrary Transform is
    /// free of in-flight jobs on the main thread. A follower therefore lands on THIS frame's IK
    /// pose and is picked up by jiggle this frame. A plain MonoBehaviour LateUpdate races those
    /// drivers and can be a frame stale depending on script execution order.
    ///
    /// Typical use from a sandboxed script:
    /// <code>
    ///   sync = new Basis.Shims.BasisTransformSyncShim();
    ///   sync.Channels = Basis.Shims.BasisTransformSyncShim.ChannelPose;
    ///   sync.BindTransforms(sourceTransforms, targetTransforms, this);
    /// </code>
    ///
    /// Blendshape weights are deliberately NOT handled here — see
    /// <see cref="BasisBlendShapeSyncShim"/>. They are split because the two have different
    /// threading futures: transform copying is jobifiable (Basis already drives paired transforms
    /// off the main thread via TransformAccessArray), while SetBlendShapeWeight is main-thread only
    /// and always will be. Keeping them separate is what lets scheduled transform work overlap
    /// main-thread blendshape work instead of serialising behind it.
    ///
    /// MAIN THREAD ONLY, all of it.
    ///
    /// Channel/space/phase selectors are const int masks rather than enums on purpose: a const
    /// inlines to a plain ldc.i4 in the caller's IL, so sandboxed callers need no enum type
    /// resolution, no boxing through Cilbox's enum machinery, and no extra link.xml preserve entry
    /// for a nested type.
    /// </summary>
    public sealed class BasisTransformSyncShim : IBasisFrameSync
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

        /// <summary>Parent-relative state. Correct when the target mirrors the source hierarchy.</summary>
        public const int SpaceLocal = 0;
        /// <summary>World state. Use when source and target live in unrelated hierarchies.</summary>
        public const int SpaceWorld = 1;

        // ---- Tick phase --------------------------------------------------------------

        /// <summary>
        /// Run inside BasisEventDriver's LateUpdate, after the avatar network transmit and before
        /// JigglePhysics dispatches its transform jobs. The default, and the only phase where an
        /// arbitrary Transform is guaranteed job-free on the main thread.
        /// </summary>
        public const int PhaseFrameSync = 0;
        /// <summary>Run on BasisEventDriver.OnUpdate, ahead of animation and IK for the frame.</summary>
        public const int PhaseUpdate = 1;
        /// <summary>
        /// Run on BasisEventDriver.OnLateUpdate — the very end of the driver's LateUpdate, after
        /// jiggle has dispatched. Later than <see cref="PhaseFrameSync"/> and can stall the main
        /// thread on in-flight transform jobs; only worth it if you must observe something that
        /// the tail of LateUpdate produces.
        /// </summary>
        public const int PhaseLateUpdate = 2;

        // ---- Limits ------------------------------------------------------------------
        //
        // WORTH KNOWING, PLATFORM SIDE: native work is NOT covered by Cilbox's time budget — its
        // accounting measures interpreted instructions only. So moving a loop behind a shim also
        // moves it outside the ceiling that kept a runaway sandboxed loop merely slow. This
        // per-call cap bounds one call; the number of simultaneously ticking shims is bounded
        // separately by BasisFrameSyncRegistry.MaxEntries, which the driver resets on teardown.

        /// <summary>Largest number of transforms one call will touch.</summary>
        public const int MaxPairs = 8192;

        // ---- The three operations ----------------------------------------------------

        /// <summary>
        /// Read <paramref name="transforms"/> into the arrays you pass. One invoke for the whole
        /// array instead of one per channel per transform.
        ///
        /// The arrays you pass ARE the channel selection: pass null for anything you do not want.
        /// <c>GetValues(bones, SpaceLocal, positions, rotations, null)</c> reads pose only and
        /// never touches scale.
        ///
        /// Entry i of each array corresponds to <c>transforms[i]</c>. The count processed is the
        /// shortest of the transform array and every non-null value array, capped at
        /// <see cref="MaxPairs"/> — nothing is ever read or written out of range. A destroyed or
        /// null transform is skipped and its slots are left as you had them, rather than being
        /// zeroed out from under you.
        ///
        /// Scale is always local: Unity's world scale (lossyScale) has no setter, so reading it
        /// would hand back something <see cref="SetValues"/> could not put back.
        /// </summary>
        /// <returns>How many entries were considered — use it to bound your own loop.</returns>
        public static int GetValues(Transform[] transforms, int space, Vector3[] positions, Quaternion[] rotations, Vector3[] scales)
        {
            bool doPosition = positions != null;
            bool doRotation = rotations != null;
            bool doScale = scales != null;
            if (transforms == null || (!doPosition && !doRotation && !doScale))
            {
                return 0;
            }

            int entries = transforms.Length;
            if (doPosition && positions.Length < entries)
            {
                entries = positions.Length;
            }
            if (doRotation && rotations.Length < entries)
            {
                entries = rotations.Length;
            }
            if (doScale && scales.Length < entries)
            {
                entries = scales.Length;
            }
            if (entries > MaxPairs)
            {
                entries = MaxPairs;
            }
            if (entries <= 0)
            {
                return 0;
            }

            bool world = space == SpaceWorld;
            bool combined = doPosition && doRotation;

            for (int Index = 0; Index < entries; Index++)
            {
                Transform transform = transforms[Index];
                if (transform == null)
                {
                    continue;
                }

                if (combined)
                {
                    // One paired get beats two property round trips: the combined call does a
                    // single local-to-world matrix traversal instead of two.
                    if (world)
                    {
                        transform.GetPositionAndRotation(out Vector3 worldPosition, out Quaternion worldRotation);
                        positions[Index] = worldPosition;
                        rotations[Index] = worldRotation;
                    }
                    else
                    {
                        transform.GetLocalPositionAndRotation(out Vector3 localPosition, out Quaternion localRotation);
                        positions[Index] = localPosition;
                        rotations[Index] = localRotation;
                    }
                }
                else if (doPosition)
                {
                    positions[Index] = world ? transform.position : transform.localPosition;
                }
                else if (doRotation)
                {
                    rotations[Index] = world ? transform.rotation : transform.localRotation;
                }

                if (doScale)
                {
                    scales[Index] = transform.localScale;
                }
            }
            return entries;
        }

        /// <summary>
        /// Write your arrays onto <paramref name="transforms"/>. The exact mirror of
        /// <see cref="GetValues"/>, with the same rules: the arrays you pass are the channel
        /// selection, pairing is by index, the shortest length wins, and null or destroyed
        /// transforms are skipped.
        ///
        /// Round-tripping is lossless in local space — Get then Set with the same arrays and space
        /// leaves the hierarchy exactly as it was. In world space it is lossless for position and
        /// rotation but NOT for scale, because scale is always applied locally.
        /// </summary>
        /// <returns>How many entries were considered.</returns>
        public static int SetValues(Transform[] transforms, int space, Vector3[] positions, Quaternion[] rotations, Vector3[] scales)
        {
            bool doPosition = positions != null;
            bool doRotation = rotations != null;
            bool doScale = scales != null;
            if (transforms == null || (!doPosition && !doRotation && !doScale))
            {
                return 0;
            }

            int entries = transforms.Length;
            if (doPosition && positions.Length < entries)
            {
                entries = positions.Length;
            }
            if (doRotation && rotations.Length < entries)
            {
                entries = rotations.Length;
            }
            if (doScale && scales.Length < entries)
            {
                entries = scales.Length;
            }
            if (entries > MaxPairs)
            {
                entries = MaxPairs;
            }
            if (entries <= 0)
            {
                return 0;
            }

            bool world = space == SpaceWorld;
            bool combined = doPosition && doRotation;

            for (int Index = 0; Index < entries; Index++)
            {
                Transform transform = transforms[Index];
                if (transform == null)
                {
                    continue;
                }

                if (combined)
                {
                    if (world)
                    {
                        transform.SetPositionAndRotation(positions[Index], rotations[Index]);
                    }
                    else
                    {
                        transform.SetLocalPositionAndRotation(positions[Index], rotations[Index]);
                    }
                }
                else if (doPosition)
                {
                    if (world)
                    {
                        transform.position = positions[Index];
                    }
                    else
                    {
                        transform.localPosition = positions[Index];
                    }
                }
                else if (doRotation)
                {
                    if (world)
                    {
                        transform.rotation = rotations[Index];
                    }
                    else
                    {
                        transform.localRotation = rotations[Index];
                    }
                }

                if (doScale)
                {
                    // Local only: Transform exposes no world-scale setter (lossyScale is
                    // read-only), so there is nothing sane to do for SpaceWorld here.
                    transform.localScale = scales[Index];
                }
            }
            return entries;
        }

        /// <summary>
        /// Get from one array and set on another in a single pass: <c>to[i]</c> takes
        /// <c>from[i]</c>'s state, per <paramref name="channels"/> and <paramref name="space"/>.
        ///
        /// Prefer this over <see cref="GetValues"/> followed by <see cref="SetValues"/> whenever
        /// you are not modifying the values in between — it skips the intermediate arrays
        /// entirely, so there is nothing to allocate and nothing to keep in step.
        ///
        /// Pairing is by index, the shorter array wins, the count is capped at
        /// <see cref="MaxPairs"/>, and a pair with either side null or destroyed is skipped.
        /// </summary>
        /// <returns>How many pairs were considered.</returns>
        public static int CopyValues(Transform[] from, Transform[] to, int channels, int space)
        {
            bool doPosition = (channels & ChannelPosition) != 0;
            bool doRotation = (channels & ChannelRotation) != 0;
            bool doScale = (channels & ChannelScale) != 0;
            if (from == null || to == null || (!doPosition && !doRotation && !doScale))
            {
                return 0;
            }

            int pairs = from.Length < to.Length ? from.Length : to.Length;
            if (pairs > MaxPairs)
            {
                pairs = MaxPairs;
            }
            if (pairs <= 0)
            {
                return 0;
            }

            // Decide the shape of the work ONCE, outside the loop.
            bool world = space == SpaceWorld;
            bool combined = doPosition && doRotation;

            for (int Index = 0; Index < pairs; Index++)
            {
                Transform source = from[Index];
                Transform target = to[Index];
                if (source == null || target == null)
                {
                    continue;
                }

                if (combined)
                {
                    if (world)
                    {
                        source.GetPositionAndRotation(out Vector3 worldPosition, out Quaternion worldRotation);
                        target.SetPositionAndRotation(worldPosition, worldRotation);
                    }
                    else
                    {
                        source.GetLocalPositionAndRotation(out Vector3 localPosition, out Quaternion localRotation);
                        target.SetLocalPositionAndRotation(localPosition, localRotation);
                    }
                }
                else if (doPosition)
                {
                    if (world)
                    {
                        target.position = source.position;
                    }
                    else
                    {
                        target.localPosition = source.localPosition;
                    }
                }
                else if (doRotation)
                {
                    if (world)
                    {
                        target.rotation = source.rotation;
                    }
                    else
                    {
                        target.localRotation = source.localRotation;
                    }
                }

                if (doScale)
                {
                    target.localScale = source.localScale;
                }
            }
            return pairs;
        }

        // ---- State -------------------------------------------------------------------

        private Transform[] source = Array.Empty<Transform>();
        private Transform[] target = Array.Empty<Transform>();
        private int count;

        // Optional renderers to gate on: no copy on frames where none is on screen.
        private Renderer[] gate = Array.Empty<Renderer>();
        private int gateCount;

        // Destroyed alongside this object, the shim unhooks itself. Without it a caller that
        // forgets to Dispose would leave a registration alive forever, pinning every bound
        // transform in memory.
        private UnityEngine.Object owner;

        private int phase = PhaseFrameSync;
        private int hookedPhase = -1;
        private bool disposed;

        // ---- Configuration -----------------------------------------------------------

        /// <summary>
        /// Which transform channels the automatic tick copies — any combination of
        /// ChannelPosition, ChannelRotation and ChannelScale. Defaults to ChannelPose. Safe to
        /// change while bound; takes effect on the next tick.
        /// </summary>
        public int Channels = ChannelPose;

        /// <summary>
        /// SpaceLocal (default) or SpaceWorld, for the automatic tick. Applies to position and
        /// rotation; scale is always local because Unity exposes no world-scale setter.
        /// </summary>
        public int Space = SpaceLocal;

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

        /// <summary>Number of transform pairs currently bound.</summary>
        public int PairCount { get { return count; } }

        // ---- Binding -----------------------------------------------------------------

        /// <summary>
        /// Bind (or re-bind) the transform pairs the automatic tick mirrors: target[i] takes
        /// source[i]'s state each frame, per <see cref="Channels"/> and <see cref="Space"/>.
        /// Arrays are COPIED, so the caller may rebuild or drop its own freely. Pairing is by
        /// index and the shorter length wins. Passing empty or null arrays unbinds.
        ///
        /// You do not need to bind to use <see cref="GetValues"/>, <see cref="SetValues"/> or
        /// <see cref="CopyValues"/> — those are static and take whatever arrays you hand them.
        /// Binding buys you the automatic per-frame tick and the lifetime cleanup, nothing else.
        /// </summary>
        /// <param name="lifetimeOwner">
        /// Object whose destruction ends the subscription — normally the calling behaviour itself
        /// (<c>this</c>). Pass null to opt out, in which case calling Dispose is mandatory.
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
                BasisDebug.LogWarning($"[BasisTransformSyncShim] {pairs} transform pairs requested; copying the first {MaxPairs} only.", BasisDebug.LogTag.Shims);
                pairs = MaxPairs;
            }

            ClearPairs();

            if (pairs > 0)
            {
                // Private copies: the caller's arrays may be sandbox-owned and replaced on its
                // next rebuild while we are mid-iteration on a later frame.
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
        /// Renderers to test before the automatic tick does any work — the copy is skipped
        /// entirely on frames where none of them is being rendered by any camera. Pass null or an
        /// empty array to always copy. Cheap and worth setting for anything that can be off screen.
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
            ClearPairs();
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

        private void ClearPairs()
        {
            source = Array.Empty<Transform>();
            target = Array.Empty<Transform>();
            count = 0;
        }

        // ---- Bound-array conveniences ------------------------------------------------

        /// <summary>
        /// <see cref="GetValues"/> over the bound SOURCE transforms, in the bound
        /// <see cref="Space"/>. Size your arrays to <see cref="PairCount"/>.
        /// </summary>
        public int GetSourceValues(Vector3[] positions, Quaternion[] rotations, Vector3[] scales)
        {
            return GetValues(source, Space, positions, rotations, scales);
        }

        /// <summary>
        /// <see cref="SetValues"/> over the bound TARGET transforms, in the bound
        /// <see cref="Space"/>. Pairs with <see cref="GetSourceValues"/> for read-modify-write:
        /// pull the source pose, adjust it, push it to the target.
        /// </summary>
        public int SetTargetValues(Vector3[] positions, Quaternion[] rotations, Vector3[] scales)
        {
            return SetValues(target, Space, positions, rotations, scales);
        }

        /// <summary>
        /// Run one copy of the bound pairs right now, ignoring <see cref="Enabled"/> and the tick
        /// phase but still honouring the visibility gate. This is the seam for driving ordering by
        /// hand: set <see cref="Enabled"/> false so the automatic tick does nothing, then call this
        /// exactly where you want the work to land relative to other systems. Main thread only.
        /// </summary>
        public int Sync()
        {
            if (disposed || count == 0 || !AnyGateVisible())
            {
                return 0;
            }
            return CopyValues(source, target, Channels, Space);
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

            CopyValues(source, target, Channels, Space);
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
