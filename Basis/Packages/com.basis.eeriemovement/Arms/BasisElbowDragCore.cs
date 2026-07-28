using Unity.Burst;
using Unity.Mathematics;

namespace Basis.IK
{
    /// <summary>
    /// Temporal DRAG on the no-elbow-tracker arm pole: the elbow trails the field instead of snapping to it.
    ///
    /// ================================================================================================
    /// WHY THE ARM HAD NOTHING LIKE THIS, AND WHY THE TWO THINGS THAT LOOK LIKE IT ARE NOT.
    ///
    /// User, in a headset: "when they dont have a hint tracker we should add some smoothing (drag) to help
    /// mask us waving our hands and the elbow going crazy."
    ///
    ///   * BasisElbowSwingCapCore is a GAIN cap -- bend rotation per unit HAND rotation, deliberately with
    ///     no dt at all. Its own header says it "SELF-SCALES: a fast hand earns a proportionally fast elbow,
    ///     so there is NO lag on ordinary reaching". That is exactly the right contract for killing a core
    ///     flip and exactly the wrong one for a wave: wave twice as fast and it licenses twice the elbow
    ///     speed. It bounds the elbow against the HAND, and a wave moves the hand.
    ///
    ///   * The old SmoothElbowSwivel One-Euro (BasisSwivelFilterCore) would be worse than nothing here.
    ///     Its cutoff is `minCutoff + beta * |velocity|` -- it OPENS UP as the signal speeds up, on the
    ///     premise that fast motion is intent worth tracking. BasisElbowFlareCore already wrote the
    ///     epitaph: "that One-Euro deliberately OPENS its cutoff on sustained swivel velocity so a real
    ///     reach does not lag, and this noise arrives precisely when the arm is moving." A speed-adaptive
    ///     filter stops filtering exactly when the user is waving, which is the one moment this is for.
    ///
    /// So neither a gain cap nor a One-Euro can do it, and the arm shipped with no temporal filter at all.
    /// What is missing is plain DRAG: a lag with a FIXED time constant, which is what physical inertia is.
    /// A real forearm has mass, so it trails a thrashing hand and settles; slow deliberate motion outruns
    /// the lag almost entirely, because the error a first-order lag holds is proportional to SPEED.
    ///
    /// ================================================================================================
    /// IT IS THE SAME OPERATION AS THE CAP, WITH A MULTIPLY WHERE THE CAP HAS A CLAMP.
    ///
    /// Both work in the tangent frame perpendicular to the shoulder->hand axis, where the single angle
    /// `ang` is "how far the bend must swivel THIS FRAME to sit on the field". The cap clamps that angle;
    /// this scales it. Scaling by alpha in (0,1] is a first-order low-pass on the pole, and because the
    /// frame is rebuilt from the CURRENT axis every frame, bulk motion -- a body turn, the hand crossing
    /// the room -- is carried for free and is never what gets lagged. Only the swivel is.
    ///
    /// Applied to the POLE (the hint handed to the arm solve), not to the solved elbow, so the HAND is
    /// untouched by construction: the two-bone solve puts the hand on target whatever the pole says.
    ///
    /// ================================================================================================
    /// A BODY TURN COSTS NOTHING, BECAUSE THE CARRIED POLE IS ROTATED BY THE BODY FIRST.
    ///
    /// The transport here is a PROJECTION onto the current axis, which does not rotate the carried pole
    /// about the vertical. On its own that reproduces the defect BasisSwivelSmootherCore's header
    /// describes -- "a swivel measured against a world axis is not invariant under a body turn ... the
    /// smoother then drags the limb toward a stale pole" -- because the field IS body-framed, so a pure
    /// yaw moves the target while a world-space pole stays put, and the whole turn reads as error.
    ///
    /// Measured with the world-frame version, hand held still relative to a spinning body:
    ///
    ///      90 deg/s turn:  0.86 cm of elbow lag at 2.5 Hz, 1.81 cm at 1.25 Hz
    ///     180 deg/s turn:  1.71 cm             at 2.5 Hz, 3.57 cm at 1.25 Hz
    ///
    /// It scales with the time constant, so doubling the damping doubled it, and it would have doubled
    /// again by ~0.6 Hz. So the pole is now carried through `bodyDelta` before the projection, which
    /// cancels a rigid turn EXACTLY rather than damping it: measured 0.00 cm at every rate and every drag
    /// setting. The drag now only ever lags what the arm does RELATIVE TO THE BODY, which is the only
    /// thing it was ever meant to lag.
    /// </summary>
    [BurstCompile]
    public static class BasisElbowDragCore
    {
        /// <summary>
        /// Per-frame blend for a first-order lag of corner frequency <paramref name="hz"/>, framerate
        /// independent. The exact exponential rather than the One-Euro's 1/(1+tau/dt) approximation --
        /// there is no reason to approximate when the closed form is one math.exp, and it keeps the drag
        /// identical across 72/90/120 Hz headsets instead of drifting with frame time.
        ///
        /// alpha -> 1 (no drag) as hz or dt grows; the time constant is tau = 1/(2*pi*hz).
        /// </summary>
        public static float Alpha(float hz, float dt)
        {
            if (!(hz > 0f) || !(dt > 0f))
            {
                return 1f;   // no drag configured, or a degenerate frame -> pass the field straight through
            }
            return 1f - math.exp(-2f * math.PI * hz * dt);
        }

        /// <summary>
        /// Lag <paramref name="targetBend"/> toward itself from last frame's bend, about
        /// <paramref name="curAxis"/>. All vectors WORLD space and unit; the result is perpendicular to
        /// curAxis. alpha >= 1 returns targetBend bit-for-bit, so a disabled drag is a true no-op.
        ///
        /// prevBend is last frame's OUTPUT pole (the caller's per-arm state).
        ///
        /// <paramref name="bodyDelta"/> is the rotation the BODY made since that pole was stored (this
        /// frame's hips rotation times the inverse of last frame's). Carrying the pole through it first is
        /// what keeps a turn from being read as swivel: the field is body-framed, so during a pure yaw the
        /// target rotates with the player, and a pole left behind in world space would register the whole
        /// turn as an error to be damped. Rotate it along and that error is identically zero, so a turn
        /// costs nothing at any drag setting. Pass identity to fall back to the world-frame behaviour.
        /// </summary>
        public static float3 Apply(float3 prevBend, quaternion bodyDelta, float3 curAxis, float3 targetBend, float alpha)
        {
            if (alpha >= 1f)
            {
                return targetBend;
            }
            if (alpha <= 0f)
            {
                alpha = 0f;
            }

            prevBend = math.rotate(bodyDelta, prevBend);

            float3 tp = prevBend - curAxis * math.dot(prevBend, curAxis);
            float tpLen = math.length(tp);
            if (tpLen < 1e-4f)
            {
                return targetBend;   // degenerate transport (axis flipped ~180) -> take the field
            }
            tp /= tpLen;

            float3 cross = math.cross(curAxis, tp);   // completes the tangent frame
            float ang = math.atan2(math.dot(targetBend, cross), math.dot(targetBend, tp));

            // atan2, not acos -- same reason as the cap: a barely-moved hand has dot ~ 1, where float32
            // acos has lost most of its digits, and a drag that jitters on slow motion is worse than none.
            ang *= alpha;

            float3 outb = tp * math.cos(ang) + cross * math.sin(ang);
            outb = outb - curAxis * math.dot(outb, curAxis);
            return math.normalizesafe(outb, targetBend);
        }
    }
}
