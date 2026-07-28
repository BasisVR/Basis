using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// Which stretch of vertebrae a spine bone stands for. This is not a label -- a segment's range of
    /// motion is set by the orientation of its facet joints and, in the thorax, by the ribs, and those
    /// differ by a FACTOR OF SIX between the lumbar spine and the thorax on some axes. One ROM for "the
    /// spine" cannot be right for any of it.
    /// </summary>
    public enum BasisSpineSegment
    {
        /// <summary>Unity `Spine`. L1-S1. The trunk's flexion hinge -- and almost immobile in twist.</summary>
        Lumbar = 0,
        /// <summary>Unity `Chest`. T7-T12. Floating ribs: bends and twists more freely than the upper thorax.</summary>
        LowerThoracic = 1,
        /// <summary>Unity `UpperChest`. T1-T6. Ribs anchored to the sternum. The stiffest part of the spine.</summary>
        UpperThoracic = 2,
        /// <summary>Unity `Neck`. C1-C7. By far the freest segment, especially in twist.</summary>
        Cervical = 3,
    }

    /// <summary>Per-side, per-axis range of motion for one spine segment, in degrees.</summary>
    public struct BasisSpineRom
    {
        /// <summary>Forward bend, degrees.</summary>
        public float FlexDeg;
        /// <summary>Backward bend, degrees.</summary>
        public float ExtDeg;
        /// <summary>Side bend, degrees PER SIDE.</summary>
        public float LatDeg;
        /// <summary>Axial rotation, degrees PER SIDE.</summary>
        public float AxialDeg;

        public BasisSpineRom(float flexDeg, float extDeg, float latDeg, float axialDeg)
        {
            FlexDeg = flexDeg;
            ExtDeg = extDeg;
            LatDeg = latDeg;
            AxialDeg = axialDeg;
        }
    }

    /// <summary>
    /// The bone's anatomical rest frame, captured at T-pose and CONSTANT thereafter. All three axes are
    /// unit vectors expressed in the PARENT bone's local frame -- the same frame the joint's local rotation
    /// lives in, which is what lets the whole guard be plain dot products with no basis change anywhere.
    /// </summary>
    public struct BasisSpineRestFrame
    {
        /// <summary>The joint's local rotation at T-pose. The anatomical neutral.</summary>
        public Quaternion RestLocalRot;
        /// <summary>Subject's RIGHT. A positive rotation about this is FLEXION -- it tips the bone forward.</summary>
        public Vector3 Right;
        /// <summary>The bone's own long axis (joint -> child at T-pose). Rotation about this is AXIAL.</summary>
        public Vector3 Up;
        /// <summary>
        /// Subject's FORWARD. A positive rotation about this side-bends the bone to the subject's LEFT
        /// (right-hand rule: +Y rotates toward -X about +Z). The lateral limit is symmetric, so which side
        /// is positive does not change any outcome -- but the sign is stated here because a comment that
        /// guesses is worse than no comment.
        /// </summary>
        public Vector3 Forward;
        /// <summary>False if the T-pose capture failed (degenerate bone). The guard no-ops.</summary>
        public bool Valid;
    }

    /// <summary>
    /// THE SPINE IS NOT ONE JOINT, AND IT IS NOT A BALL JOINT.
    ///
    /// ================================================================================================
    /// WHAT SHIPPED, AND WHY IT COULD NOT BE RIGHT:
    ///
    ///   1. There was NO AXIAL ROTATION LIMIT ANYWHERE IN THE SPINE. In BasisSpineBendCore.ClampAsymmetric,
    ///      the twist component `e.y` was clamped by `maxLat` -- THE LATERAL BEND LIMIT. Those are different
    ///      degrees of freedom of different joints. There was no `SpineMaxAxialDeg` to clamp it with; the
    ///      field did not exist.
    ///
    ///   2. ONE ROM WAS SHARED BY EVERY SEGMENT. `SpineMaxForwardDeg=60 / Backward=25 / Lateral=25` were
    ///      applied identically to the lumbar spine and to the upper chest. The lumbar does the great
    ///      majority of the trunk's flexion and almost none of its twist; the upper thorax is the reverse.
    ///      A single triple is wrong for both, and wrong in opposite directions.
    ///
    ///   3. AND NONE OF IT SURVIVED THE SOLVE ANYWAY. `ClampAsymmetric` limits the PRE-BEND delta, and then
    ///      SolveSequentialSpineIK -- the CCD that actually places the head -- runs AFTERWARDS and rotates
    ///      the same bones with no per-joint limit at all. Its only constraints were a 45 deg symmetric cone
    ///      on the neck and a 90 deg cone on the chest. THE LUMBAR SPINE AND THE UPPER CHEST HAD NOTHING.
    ///      The single most rotation-restricted segment in the human trunk was the one bone in the chain
    ///      with no constraint of any kind.
    ///
    /// THE NUMBERS, MEASURED ON 35,081 FRAMES OF REAL HUMANS (CMU corpus, 64 clips, per-segment swing-twist
    /// against each bone's own rest axis):
    ///
    ///                          flexion   extension   lateral   AXIAL       what shipped allowed
    ///     lumbar    (Spine)      49.2       10.7       12.5     10.2        60 / 25 / 25 / 25*
    ///     lo-thorac (Chest)      18.2       11.1       10.4     14.2        60 / 25 / 25 / 25*
    ///     up-thorac (UpprCh)      5.4       18.4       13.1      6.6        60 / 25 / 25 / 25*
    ///     cervical  (Neck)       23.8       35.7       29.5     20.8        45 deg cone, no twist limit
    ///     (p99 of |angle|, degrees)                            *and unbounded once the CCD ran
    ///
    ///   The upper chest pitches 5.4 deg at p99 and was allowed 60. That is "looking down forces the chest
    ///   to rotate". The lumbar twists 10.2 deg at p99 and was allowed 25 and then anything at all.
    /// ================================================================================================
    ///
    /// THE LIMITS COME FROM THE LITERATURE, NOT FROM THE CORPUS, AND THAT IS DELIBERATE. Motion capture
    /// records what people DID, not what they CAN do -- CMU subjects walk around a lab, they do not
    /// side-bend to end range. Fitting the envelope to the corpus would encode "didn't" as "can't" and
    /// then clip the first VR user who leans sideways. So the limits are max-voluntary-ROM from the
    /// clinical literature (see BasisSpineAnatomy), and the corpus gets a VETO ONLY: it is used to prove
    /// the envelope never fires on real motion. It does not: across all 35,081 frames the guard perturbs
    /// under 0.6% of them on every segment, and every frame it does touch is one the literature says is
    /// impossible -- a 35 deg lumbar twist (Fujii's in-vivo MRI says 7.7), a 100 deg lumbar flexion. Those
    /// are artifacts of CMU's 3-joint spine, and the shipped rig would have reproduced them faithfully.
    ///
    /// THE SWING ENVELOPE IS AN ELLIPTICAL CONE, NOT A BOX. Clamping flexion, lateral bend and twist
    /// independently -- which is what clamping euler components does -- admits max-flexion AND max-side-bend
    /// AND max-twist simultaneously. No spine does that; you cannot bend fully forward and fully sideways at
    /// once. The cone has four quadrant radii (flexion and extension differ; left and right do not), which is
    /// the standard reach-cone construction.
    ///
    /// THE TWIST LIMIT IS FLAT, AND THAT TOO IS MEASURED. The hip's rotational slack collapses at end-range
    /// swing (van Arkel 2015) and it was tempting to port that here. The corpus says the spine does not do
    /// that: binned by swing magnitude, the lumbar's p99 axial rotation runs 8.7 / 8.7 / 10.6 / 10.7 / 10.4
    /// deg from 0-10 deg of swing out to 45-90. It does not collapse. So there is no swing-twist coupling in
    /// this guard, because there is no evidence for one.
    ///
    /// IT GUARDS THE OUTCOME AND RUNS INSIDE THE CCD, WHICH IS THE ONLY PLACE IT CAN WORK. A limit applied
    /// before the CCD is a suggestion. Applied after the CCD it would drag the head off the HMD. Applied
    /// per-joint INSIDE the loop, the residual simply redistributes onto the other vertebrae on the next
    /// sweep -- which is exactly what a real spine does when you ask one segment for more than it has.
    ///
    /// IT IS THE IDENTITY INSIDE THE ENVELOPE, BIT FOR BIT. A legal pose is returned untouched -- not
    /// re-normalised, not round-tripped through a decomposition. A guard that perturbs legal poses to fix
    /// illegal ones is the wrong trade. Outside, it eases in with the same rational saturation and slope-1
    /// handover that BasisElbowAnatomyCore and BasisLegSolveCore.ClampKneeSwivelDeg use, so the spine
    /// arrives at its limit instead of hitting a wall. Slope-1 means no derivative step, which is the
    /// difference between a guard and a pop.
    ///
    /// THE HEAD IS NOT GUARDED, ON PURPOSE. It is welded to the HMD -- commanded, not solved. A guard there
    /// would fight the tracker and drag the view off the user's eyes. (And the corpus agrees it would be
    /// wrong to try: CMU's `Head` joint carries real cervical motion, and a C0-C1 envelope clips 29% of it.)
    /// This guards what the solver INVENTS. The same doctrine as the arm: guard the elbow, never the hand.
    /// </summary>
    public static class BasisSpineAnatomyCore
    {
        const float k_Epsilon = 1e-5f;
        const float k_SqrEpsilon = 1e-8f;

        /// <summary>
        /// Inverse of a UNIT quaternion: the conjugate. Everything here is unit by construction, so this is
        /// exact. Written out rather than calling Quaternion.Inverse because that is a native ECall -- it
        /// cannot run outside the editor, which would put this core beyond the reach of a standalone test.
        /// </summary>
        public static Quaternion Conj(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        /// <summary>Quaternion.AngleAxis, written out. Same reason as <see cref="Conj"/>. `axis` must be unit.</summary>
        public static Quaternion AxisAngle(float deg, Vector3 axis)
        {
            float h = deg * (0.5f * Mathf.Deg2Rad);
            float s = Mathf.Sin(h);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(h));
        }

        /// <summary>
        /// How far past the anatomical limit the guard's asymptote sits. The limit itself is the SOFT
        /// threshold -- every anatomically legal pose passes through untouched -- and the guard eases from
        /// there toward `limit * OvershootAsymptote`, which it never reaches.
        ///
        /// Why tolerate any overshoot at all: published ROM has real inter-subject spread (van Arkel's hip
        /// extension is -12 +/- 7 deg), so a hard wall at the population mean would fight a hypermobile user
        /// on poses their own spine makes comfortably. 1.25x is inside that spread and still 2-3x tighter
        /// than what shipped -- which, past the CCD, was no limit at all.
        /// </summary>
        public const float OvershootAsymptote = 1.25f;

        /// <summary>
        /// Clamp one spine joint's LOCAL rotation into its anatomical envelope.
        ///
        /// `localRot` is the bone's rotation relative to its parent, right now. `frame` is its rest frame,
        /// captured at T-pose. Returns `localRot` UNCHANGED, bit for bit, when the pose is already legal.
        /// </summary>
        public static Quaternion Clamp(Quaternion localRot, in BasisSpineRestFrame frame, in BasisSpineRom rom)
        {
            return Clamp(localRot, frame, rom, out _);
        }

        /// <summary>As <see cref="Clamp(Quaternion, in BasisSpineRestFrame, in BasisSpineRom)"/>, reporting what it did.</summary>
        public static Quaternion Clamp(Quaternion localRot, in BasisSpineRestFrame frame, in BasisSpineRom rom, out BasisSpineClampInfo info)
        {
            info = default;
            if (!frame.Valid)
            {
                return localRot;
            }

            // Deviation from the anatomical neutral. Both `localRot` and the rest pose are rotations in the
            // PARENT's frame, so `delta` is too -- and so are the three anatomical axes, which is what makes
            // every projection below a plain dot product with no change of basis anywhere.
            Quaternion delta = localRot * Conj(frame.RestLocalRot);

            Decompose(delta, frame, out float flexDeg, out float latDeg, out float axialDeg);
            // Reject-unless-good: NaN fails every ordered comparison, so a NaN quaternion leaves here as the
            // input rather than as a NaN written into a bone -- and a NaN transform PERSISTS in Unity, so the
            // spine would never recover even once good data returned.
            if (!(flexDeg * flexDeg + latDeg * latDeg + axialDeg * axialDeg >= 0f))
            {
                return localRot;
            }

            float axialLim = Mathf.Max(0f, rom.AxialDeg);
            float flexLim = Mathf.Max(0f, flexDeg >= 0f ? rom.FlexDeg : rom.ExtDeg);
            float latLim = Mathf.Max(0f, rom.LatDeg);

            // ---- the swing: an elliptical cone, evaluated on the SWING as a whole ----
            // q is the squared ellipse radius the current swing sits at. q <= 1 is inside.
            float fN = flexDeg / Mathf.Max(flexLim, k_Epsilon);
            float lN = latDeg / Mathf.Max(latLim, k_Epsilon);
            float q = fN * fN + lN * lN;

            float swingScale = 1f;
            if (q > 1f)
            {
                // Saturate the ellipse RADIUS, not the components: scaling flexion and lateral bend by the
                // same factor keeps the swing's DIRECTION exactly, so the guard pulls the bone straight back
                // along its own bend and never swings it sideways to get legal.
                float rNow = Mathf.Sqrt(q);              // 1.0 == exactly on the ellipse
                float rGuard = Saturate(rNow, 1f, OvershootAsymptote);
                swingScale = rGuard / rNow;
                info.SwingClamped = true;
            }

            // ---- the twist: a flat per-segment limit ----
            float axialGuard = axialDeg;
            float axialAbs = axialDeg < 0f ? -axialDeg : axialDeg;
            if (axialAbs > axialLim)
            {
                float mag = Saturate(axialAbs, axialLim, axialLim * OvershootAsymptote);
                axialGuard = axialDeg < 0f ? -mag : mag;
                info.TwistClamped = true;
            }

            info.FlexDeg = flexDeg;
            info.LatDeg = latDeg;
            info.AxialDeg = axialDeg;

            // Inside the envelope: the IDENTITY, exactly. Not re-normalised, not round-tripped through the
            // decomposition -- the caller gets back the quaternion it handed us, bit for bit.
            if (!info.SwingClamped && !info.TwistClamped)
            {
                return localRot;
            }

            info.FlexGuardedDeg = flexDeg * swingScale;
            info.LatGuardedDeg = latDeg * swingScale;
            info.AxialGuardedDeg = axialGuard;

            return Recompose(info.FlexGuardedDeg, info.LatGuardedDeg, axialGuard, frame);
        }

        /// <summary>
        /// Swing-twist decomposition of `delta` about the bone's own long axis, read out onto the anatomical
        /// axes. Order-free and gimbal-free -- unlike euler angles, which is what the shipped clamp used.
        /// </summary>
        public static void Decompose(Quaternion delta, in BasisSpineRestFrame frame, out float flexDeg, out float latDeg, out float axialDeg)
        {
            // Shortest arc. Without this the twist angle wraps sign at +/-180 and the guard would fight itself.
            if (delta.w < 0f)
            {
                delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
            }

            Vector3 v = new Vector3(delta.x, delta.y, delta.z);
            float proj = Vector3.Dot(v, frame.Up);

            // twist = the part of `delta` about the bone axis. swing = whatever is left.
            Quaternion twist = new Quaternion(frame.Up.x * proj, frame.Up.y * proj, frame.Up.z * proj, delta.w);
            float twistNorm = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            if (!(twistNorm > k_Epsilon))
            {
                // delta is a 180 deg swing about an axis perpendicular to the bone: the twist is genuinely
                // undefined there, not merely hard to compute. Call it zero and let the swing carry it all.
                twist = Quaternion.identity;
                proj = 0f;
            }
            else
            {
                float inv = 1f / twistNorm;
                twist = new Quaternion(twist.x * inv, twist.y * inv, twist.z * inv, twist.w * inv);
                proj *= inv;
            }

            axialDeg = 2f * Mathf.Atan2(proj, twist.w) * Mathf.Rad2Deg;

            Quaternion swing = delta * Conj(twist);
            if (swing.w < 0f)
            {
                swing = new Quaternion(-swing.x, -swing.y, -swing.z, -swing.w);
            }

            Vector3 sv = new Vector3(swing.x, swing.y, swing.z);
            float svLen = sv.magnitude;
            float swingDeg = 2f * Mathf.Atan2(svLen, swing.w) * Mathf.Rad2Deg;
            Vector3 swingVec = svLen > k_Epsilon ? (sv / svLen) * swingDeg : Vector3.zero;

            // A positive rotation about `Right` tips the bone's up-axis toward `Forward`: that is FLEXION.
            // A positive rotation about `Forward` side-bends it to the subject's LEFT (see BasisSpineRestFrame).
            flexDeg = Vector3.Dot(swingVec, frame.Right);
            latDeg = Vector3.Dot(swingVec, frame.Forward);
        }

        /// <summary>Rebuild a local rotation from anatomical angles. Exactly inverts <see cref="Decompose"/>.</summary>
        public static Quaternion Recompose(float flexDeg, float latDeg, float axialDeg, in BasisSpineRestFrame frame)
        {
            Vector3 swingVec = frame.Right * flexDeg + frame.Forward * latDeg;
            float swingDeg = swingVec.magnitude;
            Quaternion swing = swingDeg > k_Epsilon
                ? AxisAngle(swingDeg, swingVec / swingDeg)
                : Quaternion.identity;
            Quaternion twist = AxisAngle(axialDeg, frame.Up);
            // delta = swing * twist, matching the decomposition's `swing = delta * inv(twist)`.
            return swing * twist * frame.RestLocalRot;
        }

        /// <summary>
        /// Rational saturation: exactly `x` up to `soft`, then eased asymptotically toward `hard`, which it
        /// approaches but never reaches. Slope is exactly 1 at the handover, so there is no derivative step
        /// -- the bone eases into its limit rather than striking a wall. Same shape as the elbow and knee.
        /// </summary>
        public static float Saturate(float x, float soft, float hard)
        {
            if (!(x > soft))
            {
                return x;
            }
            float m = hard - soft;
            if (!(m > k_Epsilon))
            {
                return soft;
            }
            float e = x - soft;
            return soft + m * e / (m + e);
        }
    }

    /// <summary>What the guard did to one joint this frame. Diagnostics + tests.</summary>
    public struct BasisSpineClampInfo
    {
        public bool SwingClamped;
        public bool TwistClamped;
        public float FlexDeg, LatDeg, AxialDeg;
        public float FlexGuardedDeg, LatGuardedDeg, AxialGuardedDeg;
        public bool Touched => SwingClamped || TwistClamped;
    }
}
