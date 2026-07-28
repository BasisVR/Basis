using Unity.Mathematics;

namespace Basis.Scripts.Drivers
{
    public enum BasisLocoState : byte
    {
        Walking = 0,
        Cawling = 1,
        Jump = 2,
        Falling = 3,
        Landing = 4,
    }

    public enum BasisLocoCondition : byte
    {
        IsJumpingTrue = 0,
        CrouchedTrue = 1,
        CrouchedFalse = 2,
        IsFallingTrue = 3,
        IsFallingFalse = 4,
        LandingTrigger = 5,
    }

    public struct BasisLocoParams
    {
        public float VelocityX;
        public float VelocityZ;
        public float CurrentSpeed;
        public bool CrouchedState;
        public bool IsFalling;
        public bool IsJumping;
        public bool LandingTrigger;
    }

    public struct BasisLocoContribution
    {
        public int Clip;
        public float Weight;
        public float Time;
    }

    public struct BasisLocoSimState
    {
        public BasisLocoState Current;
        public float CurrentNorm;
        public bool InTransition;
        public BasisLocoState To;
        public float ToNorm;
        public float TransitionProgress01;
        public float TransitionInvDuration;
    }

    public struct BasisLocoTransition
    {
        public BasisLocoCondition Condition;
        public BasisLocoState To;
        public float DurationSeconds;
        public bool HasExitTime;
        public float ExitTime;
    }

    /// <summary>
    /// Faithful data model + stepper for BasisLocomotion.controller (com.basis.sdk). The tables mirror
    /// the asset exactly — state set, transition order, blend-tree child positions/time scales, exit
    /// times — and BasisLocomotionGraphParityTests re-derives them from the asset in the editor, so a
    /// controller edit fails the test instead of silently diverging from this stepper.
    /// Scope is deliberately the stock controller only: avatars that ship their own controller keep the
    /// engine Animator path (see BasisLocomotionPoseSystem).
    /// </summary>
    public static class BasisLocomotionGraph
    {
        public const int ClipCount = 17;
        public const int MaxContributions = 16;

        public const int WalkingChildCount = 10;
        public const int CawlingChildCount = 4;
        public const int WalkingClipStart = 0;
        public const int CawlingClipStart = 10;
        public const int JumpClip = 14;
        public const int FallingClip = 15;
        public const int LandingClip = 16;

        public static readonly string[] ClipNames =
        {
            "Armature.001|Walking",
            "Armature.001|WalkingRight",
            "Armature.001|StrafeRight",
            "Armature.001|WalkingBackRight",
            "Run_S",
            "Armature.001|WalkingBackLeft",
            "Armature.001|Walking.001",
            "Armature.001|WalkingLeft",
            "HumanoidRun",
            "Idle",
            "HumanoidCrouchWalk",
            "HumanoidCrouchIdle",
            "HumanoidCrouchWalkLeft",
            "HumanoidCrouchWalkRight",
            "JumpStart",
            "HumanoidFall",
            "JumpLand",
        };

        public static readonly float2[] WalkingChildPositions =
        {
            new float2(0f, 1.2f),
            new float2(1.3388321f, 1.3386241f),
            new float2(1.3944283f, 0.000011066161f),
            new float2(1.3341995f, -1.3040041f),
            new float2(-0.011385167f, -1.8538179f),
            new float2(-1.3471601f, -1.3232877f),
            new float2(-1.3944328f, -0.000010990724f),
            new float2(-1.3405663f, 1.3408747f),
            new float2(0.03366417f, 3.6030414f),
            new float2(0.000011402694f, 0.000046161364f),
        };

        public static readonly float[] WalkingChildTimeScales =
        {
            1.5f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f,
        };

        public static readonly float2[] CawlingChildPositions =
        {
            new float2(-0.035f, 1.524f),
            new float2(0f, 0f),
            new float2(-1.394433f, -0.00001099072f),
            new float2(1.3342f, 0.00001106616f),
        };

        public static readonly float[] CawlingChildTimeScales =
        {
            1f, 1f, 1f, 1f,
        };

        // Per state, in the exact order the controller asset lists them — first satisfied wins.
        public static readonly BasisLocoTransition[][] Transitions =
        {
            // Walking
            new[]
            {
                new BasisLocoTransition { Condition = BasisLocoCondition.IsJumpingTrue, To = BasisLocoState.Jump, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.CrouchedTrue, To = BasisLocoState.Cawling, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingTrue, To = BasisLocoState.Falling, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.LandingTrigger, To = BasisLocoState.Landing, DurationSeconds = 0.25f, HasExitTime = true, ExitTime = 0.79395604f },
            },
            // Cawling
            new[]
            {
                new BasisLocoTransition { Condition = BasisLocoCondition.CrouchedFalse, To = BasisLocoState.Walking, DurationSeconds = 0.25f },
            },
            // Jump
            new[]
            {
                new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingTrue, To = BasisLocoState.Falling, DurationSeconds = 0.25f },
            },
            // Falling
            new[]
            {
                new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingFalse, To = BasisLocoState.Walking, DurationSeconds = 0.25f, HasExitTime = true, ExitTime = 0.75f },
                new BasisLocoTransition { Condition = BasisLocoCondition.LandingTrigger, To = BasisLocoState.Landing, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.IsJumpingTrue, To = BasisLocoState.Jump, DurationSeconds = 0.25f },
            },
            // Landing
            new[]
            {
                new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingTrue, To = BasisLocoState.Falling, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.IsJumpingTrue, To = BasisLocoState.Jump, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingFalse, To = BasisLocoState.Walking, DurationSeconds = 0.25f },
            },
        };

        static readonly float[] sWalkingWeights = new float[WalkingChildCount];
        static readonly float[] sCawlingWeights = new float[CawlingChildCount];

        public static BasisLocoSimState DefaultSimState => new BasisLocoSimState
        {
            Current = BasisLocoState.Walking,
        };

        static bool EvaluateCondition(BasisLocoCondition condition, in BasisLocoParams p)
        {
            switch (condition)
            {
                case BasisLocoCondition.IsJumpingTrue: return p.IsJumping;
                case BasisLocoCondition.CrouchedTrue: return p.CrouchedState;
                case BasisLocoCondition.CrouchedFalse: return !p.CrouchedState;
                case BasisLocoCondition.IsFallingTrue: return p.IsFalling;
                case BasisLocoCondition.IsFallingFalse: return !p.IsFalling;
                case BasisLocoCondition.LandingTrigger: return p.LandingTrigger;
                default: return false;
            }
        }

        /// <summary>
        /// Gradient-band interpolation in cartesian space — the algorithm behind Unity's
        /// 2D Freeform Cartesian blend type. weights must have one slot per position.
        /// </summary>
        public static void FreeformCartesianWeights(float2 point, float2[] positions, float[] weights)
        {
            int count = positions.Length;
            float total = 0f;
            for (int i = 0; i < count; i++)
            {
                float2 pi = positions[i];
                float weight = 1f;
                for (int j = 0; j < count; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }
                    float2 edge = positions[j] - pi;
                    float lenSq = math.lengthsq(edge);
                    float h = lenSq > 1e-12f ? 1f - math.dot(point - pi, edge) / lenSq : 0f;
                    h = math.clamp(h, 0f, 1f);
                    if (h < weight)
                    {
                        weight = h;
                    }
                }
                weights[i] = weight;
                total += weight;
            }

            if (total <= 1e-6f)
            {
                int nearest = 0;
                float best = float.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    float d = math.lengthsq(point - positions[i]);
                    weights[i] = 0f;
                    if (d < best)
                    {
                        best = d;
                        nearest = i;
                    }
                }
                weights[nearest] = 1f;
                return;
            }

            float inv = 1f / total;
            for (int i = 0; i < count; i++)
            {
                weights[i] *= inv;
            }
        }

        static float StateNormDelta(BasisLocoState state, in BasisLocoParams p, float dt, float[] clipLengths, out float[] treeWeights)
        {
            switch (state)
            {
                case BasisLocoState.Walking:
                {
                    FreeformCartesianWeights(new float2(p.VelocityX, p.VelocityZ), WalkingChildPositions, sWalkingWeights);
                    treeWeights = sWalkingWeights;
                    float duration = 0f;
                    for (int i = 0; i < WalkingChildCount; i++)
                    {
                        duration += sWalkingWeights[i] * (clipLengths[WalkingClipStart + i] / WalkingChildTimeScales[i]);
                    }
                    return duration > 1e-4f ? dt * p.CurrentSpeed / duration : 0f;
                }
                case BasisLocoState.Cawling:
                {
                    FreeformCartesianWeights(new float2(p.VelocityX, p.VelocityZ), CawlingChildPositions, sCawlingWeights);
                    treeWeights = sCawlingWeights;
                    float duration = 0f;
                    for (int i = 0; i < CawlingChildCount; i++)
                    {
                        duration += sCawlingWeights[i] * (clipLengths[CawlingClipStart + i] / CawlingChildTimeScales[i]);
                    }
                    return duration > 1e-4f ? dt * p.CurrentSpeed / duration : 0f;
                }
                case BasisLocoState.Jump:
                    treeWeights = null;
                    return clipLengths[JumpClip] > 1e-4f ? dt / clipLengths[JumpClip] : 0f;
                case BasisLocoState.Falling:
                    treeWeights = null;
                    return clipLengths[FallingClip] > 1e-4f ? dt / clipLengths[FallingClip] : 0f;
                default:
                    treeWeights = null;
                    return clipLengths[LandingClip] > 1e-4f ? dt / clipLengths[LandingClip] : 0f;
            }
        }

        static int EmitState(BasisLocoState state, float norm, float stateWeight, float[] treeWeights, float[] clipLengths, bool[] clipLooping, BasisLocoContribution[] contributions, int count)
        {
            switch (state)
            {
                case BasisLocoState.Walking:
                {
                    float phase = norm - math.floor(norm);
                    for (int i = 0; i < WalkingChildCount; i++)
                    {
                        float w = stateWeight * treeWeights[i];
                        if (w <= 1e-5f)
                        {
                            continue;
                        }
                        int clip = WalkingClipStart + i;
                        contributions[count++] = new BasisLocoContribution { Clip = clip, Weight = w, Time = phase * clipLengths[clip] };
                    }
                    return count;
                }
                case BasisLocoState.Cawling:
                {
                    float phase = norm - math.floor(norm);
                    for (int i = 0; i < CawlingChildCount; i++)
                    {
                        float w = stateWeight * treeWeights[i];
                        if (w <= 1e-5f)
                        {
                            continue;
                        }
                        int clip = CawlingClipStart + i;
                        contributions[count++] = new BasisLocoContribution { Clip = clip, Weight = w, Time = phase * clipLengths[clip] };
                    }
                    return count;
                }
                default:
                {
                    int clip = state == BasisLocoState.Jump ? JumpClip : state == BasisLocoState.Falling ? FallingClip : LandingClip;
                    float phase = clipLooping[clip] ? norm - math.floor(norm) : math.min(norm, 1f);
                    contributions[count++] = new BasisLocoContribution { Clip = clip, Weight = stateWeight, Time = phase * clipLengths[clip] };
                    return count;
                }
            }
        }

        /// <summary>
        /// Advances the state machine by dt and emits the weighted clip samples that reproduce what the
        /// Animator would have blended this frame. Consumes p.LandingTrigger when a transition fires on it.
        /// Returns the contribution count.
        /// </summary>
        public static int Step(ref BasisLocoSimState s, ref BasisLocoParams p, float dt, float[] clipLengths, bool[] clipLooping, BasisLocoContribution[] contributions)
        {
            float previousNorm = s.CurrentNorm;
            float currentDelta = StateNormDelta(s.Current, in p, dt, clipLengths, out float[] currentWeights);
            s.CurrentNorm += currentDelta;

            float[] toWeights = null;
            if (s.InTransition)
            {
                s.ToNorm += StateNormDelta(s.To, in p, dt, clipLengths, out toWeights);
                s.TransitionProgress01 += dt * s.TransitionInvDuration;
                if (s.TransitionProgress01 >= 1f)
                {
                    s.Current = s.To;
                    s.CurrentNorm = s.ToNorm;
                    s.InTransition = false;
                    s.TransitionProgress01 = 0f;
                    currentWeights = toWeights;
                    toWeights = null;
                }
            }
            else
            {
                BasisLocoTransition[] candidates = Transitions[(int)s.Current];
                for (int i = 0; i < candidates.Length; i++)
                {
                    ref readonly BasisLocoTransition t = ref candidates[i];
                    if (t.HasExitTime)
                    {
                        // Looping states re-arm the exit each cycle: fires on crossing k + ExitTime.
                        bool crossed = math.floor(s.CurrentNorm - t.ExitTime) > math.floor(previousNorm - t.ExitTime);
                        if (!crossed)
                        {
                            continue;
                        }
                    }
                    if (!EvaluateCondition(t.Condition, in p))
                    {
                        continue;
                    }
                    if (t.Condition == BasisLocoCondition.LandingTrigger)
                    {
                        p.LandingTrigger = false;
                    }
                    s.InTransition = true;
                    s.To = t.To;
                    s.ToNorm = 0f;
                    s.TransitionProgress01 = 0f;
                    s.TransitionInvDuration = 1f / math.max(t.DurationSeconds, 1e-4f);
                    StateNormDelta(s.To, in p, 0f, clipLengths, out toWeights);
                    break;
                }
            }

            int count = 0;
            float fromWeight = s.InTransition ? 1f - s.TransitionProgress01 : 1f;
            count = EmitState(s.Current, s.CurrentNorm, fromWeight, currentWeights, clipLengths, clipLooping, contributions, count);
            if (s.InTransition)
            {
                count = EmitState(s.To, s.ToNorm, s.TransitionProgress01, toWeights, clipLengths, clipLooping, contributions, count);
            }
            return count;
        }
    }
}
