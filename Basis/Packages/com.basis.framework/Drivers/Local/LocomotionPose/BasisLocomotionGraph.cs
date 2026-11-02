using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
namespace Basis.Scripts.Drivers
{
    public struct BasisLocoParams
    {
        public float VelocityX, VelocityZ, CurrentSpeed;
        public bool CrouchedState, ProneState, IsFalling, IsJumping, LandingTrigger;
    }
    public struct BasisLocoContribution
    {
        public int Clip;
        public float Weight, Time;
    }
    public struct BasisLocoSimState
    {
        public BasisLocoState Current;
        public float CurrentNorm;
        public bool InTransition;
        public BasisLocoState To;
        public float ToNorm, TransitionProgress01, TransitionInvDuration;
    }
    public struct BasisLocoTransition
    {
        public BasisLocoCondition Condition;
        public BasisLocoState To;
        public float DurationSeconds;
        public bool HasExitTime;
        public float ExitTime;
    }
    public static class BasisLocomotionGraph
    {
        public const int ClipCount = 38;
        public const int MaxContributions = 28;
        public const int WalkingChildCount = 17;
        public const int CrouchingChildCount = 9;
        public const int ProneChildCount = 9;
        public const int WalkingClipStart = 0;
        public const int CrouchingClipStart = 17;
        public const int ProneClipStart = 26;
        public const int JumpClip = 35;
        public const int FallingClip = 36;
        public const int LandingClip = 37;
        public static readonly string[] ClipNames =
        {
            "Walking",
            "ForwardStrafeRight",
            "Rside",
            "BackwardsStrafeRight",
            "Backwards",
            "BackwardsStrafeLeft",
            "Lside",
            "ForwardStrafeLeft",
            "Run",
            "Idle",
            "RunStrafeRight",
            "RunRight",
            "RunBackStafeRight",
            "RunBackwards",
            "RunBackStafeLeft",
            "RunLeft",
            "RunStrafeLeft",
            "CrouchForward",
            "CrouchStrafeRight",
            "RCrouch",
            "CrouchStrafeBackRight",
            "CrouchBackwards",
            "CrouchStrafeBackLeft",
            "LCrouch",
            "CrouchStrafeLeft",
            "CrouchIdle",
            "ProneForward",
            "ProneStrafeRight",
            "ProneRight",
            "ProneBackWRight",
            "ProneBackwards",
            "ProneBackWLeft",
            "ProneLeft",
            "ProneStrafeLeft",
            "ProneIdle",
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
            new float2(0f, 3.6f),
            new float2(0.000011402694f, 0.000046161364f),
            new float2(2.55f, 2.55f),
            new float2(3.6f, 0f),
            new float2(2.55f, -2.55f),
            new float2(0f, -3.6f),
            new float2(-2.55f, -2.55f),
            new float2(-3.6f, 0f),
            new float2(-2.55f, 2.55f),
        };
        public static readonly float[] WalkingChildTimeScales =
        {
            1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1.5f, 1f, 1.5f, 1.5f, 1.5f, 1.5f, 1.5f, 1.5f, 1.5f,
        };
        public static readonly float2[] CrouchingChildPositions =
        {
            new float2(0f, 1.2f),
            new float2(1.35f, 1.35f),
            new float2(1.4f, 0f),
            new float2(1.35f, -1.35f),
            new float2(0f, -1.2f),
            new float2(-1.35f, -1.35f),
            new float2(-1.4f, 0f),
            new float2(-1.35f, 1.35f),
            new float2(0f, 0f),
        };
        public static readonly float[] CrouchingChildTimeScales =
        {
            1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f,
        };
        public static readonly float2[] ProneChildPositions =
        {
            new float2(0f, 0.4f),
            new float2(0.42f, 0.42f),
            new float2(0.45f, 0f),
            new float2(0.42f, -0.42f),
            new float2(0f, -0.4f),
            new float2(-0.42f, -0.42f),
            new float2(-0.45f, 0f),
            new float2(-0.42f, 0.42f),
            new float2(0f, 0f),
        };
        public static readonly float[] ProneChildTimeScales =
        {
            1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f,
        };
        public static readonly BasisLocoTransition[][] Transitions =
        {

            new[]
            {
                new BasisLocoTransition { Condition = BasisLocoCondition.IsJumpingTrue, To = BasisLocoState.Jump, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.ProneTrue, To = BasisLocoState.Prone, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.CrouchedTrue, To = BasisLocoState.Crouching, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingTrue, To = BasisLocoState.Falling, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.LandingTrigger, To = BasisLocoState.Landing, DurationSeconds = 0.25f, HasExitTime = true, ExitTime = 0.79395604f },
            },

            new[]
            {
                new BasisLocoTransition { Condition = BasisLocoCondition.ProneTrue, To = BasisLocoState.Prone, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.CrouchedFalse, To = BasisLocoState.Walking, DurationSeconds = 0.25f },
            },

            new[]
            {
                new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingTrue, To = BasisLocoState.Falling, DurationSeconds = 0.25f },
            },

            new[]
            {
                new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingFalse, To = BasisLocoState.Walking, DurationSeconds = 0.25f, HasExitTime = true, ExitTime = 0.75f },
                new BasisLocoTransition { Condition = BasisLocoCondition.LandingTrigger, To = BasisLocoState.Landing, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.IsJumpingTrue, To = BasisLocoState.Jump, DurationSeconds = 0.25f },
            },

            new[]
            {
                new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingTrue, To = BasisLocoState.Falling, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.IsJumpingTrue, To = BasisLocoState.Jump, DurationSeconds = 0.25f },
                new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingFalse, To = BasisLocoState.Walking, DurationSeconds = 0.25f },
            },

            new[]
            {
                new BasisLocoTransition { Condition = BasisLocoCondition.ProneFalse, To = BasisLocoState.Crouching, DurationSeconds = 0.25f },
            },
        };
        static readonly float[] sWalkingWeights = new float[WalkingChildCount];
        static readonly float[] sCrouchingWeights = new float[CrouchingChildCount];
        static readonly float[] sProneWeights = new float[ProneChildCount];
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
                case BasisLocoCondition.ProneTrue: return p.ProneState;
                case BasisLocoCondition.ProneFalse: return !p.ProneState;
                default: return false;
            }
        }
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
        static float TreeNormDelta(in BasisLocoParams p, float dt, float[] clipLengths, float2[] positions, float[] timeScales, float[] weights, int clipStart)
        {
            FreeformCartesianWeights(new float2(p.VelocityX, p.VelocityZ), positions, weights);
            int count = positions.Length;
            float duration = 0f;
            for (int i = 0; i < count; i++)
            {
                duration += weights[i] * (clipLengths[clipStart + i] / timeScales[i]);
            }
            return duration > 1e-4f ? dt * p.CurrentSpeed / duration : 0f;
        }
        static float StateNormDelta(BasisLocoState state, in BasisLocoParams p, float dt, float[] clipLengths, out float[] treeWeights)
        {
            switch (state)
            {
                case BasisLocoState.Walking: treeWeights = sWalkingWeights;
                    return TreeNormDelta(in p, dt, clipLengths, WalkingChildPositions, WalkingChildTimeScales, sWalkingWeights, WalkingClipStart);
                case BasisLocoState.Crouching: treeWeights = sCrouchingWeights;
                    return TreeNormDelta(in p, dt, clipLengths, CrouchingChildPositions, CrouchingChildTimeScales, sCrouchingWeights, CrouchingClipStart);
                case BasisLocoState.Prone: treeWeights = sProneWeights;
                    return TreeNormDelta(in p, dt, clipLengths, ProneChildPositions, ProneChildTimeScales, sProneWeights, ProneClipStart);
                case BasisLocoState.Jump: treeWeights = null;
                    return clipLengths[JumpClip] > 1e-4f ? dt / clipLengths[JumpClip] : 0f;
                case BasisLocoState.Falling: treeWeights = null;
                    return clipLengths[FallingClip] > 1e-4f ? dt / clipLengths[FallingClip] : 0f;
                default: treeWeights = null;
                    return clipLengths[LandingClip] > 1e-4f ? dt / clipLengths[LandingClip] : 0f;
            }
        }
        static int EmitTree(float norm, float stateWeight, float[] treeWeights, int clipStart, int childCount, float[] clipLengths, BasisLocoContribution[] contributions, int count)
        {
            float phase = norm - math.floor(norm);
            for (int i = 0; i < childCount; i++)
            {
                float w = stateWeight * treeWeights[i];
                if (w <= 1e-5f)
                {
                    continue;
                }
                int clip = clipStart + i;
                contributions[count++] = new BasisLocoContribution { Clip = clip, Weight = w, Time = phase * clipLengths[clip] };
            }
            return count;
        }
        static int EmitState(BasisLocoState state, float norm, float stateWeight, float[] treeWeights, float[] clipLengths, bool[] clipLooping, BasisLocoContribution[] contributions, int count)
        {
            switch (state)
            {
                case BasisLocoState.Walking: return EmitTree(norm, stateWeight, treeWeights, WalkingClipStart, WalkingChildCount, clipLengths, contributions, count);
                case BasisLocoState.Crouching: return EmitTree(norm, stateWeight, treeWeights, CrouchingClipStart, CrouchingChildCount, clipLengths, contributions, count);
                case BasisLocoState.Prone: return EmitTree(norm, stateWeight, treeWeights, ProneClipStart, ProneChildCount, clipLengths, contributions, count);
                default:
                {
                    int clip = state == BasisLocoState.Jump ? JumpClip : state == BasisLocoState.Falling ? FallingClip : LandingClip;
                    float phase = clipLooping[clip] ? norm - math.floor(norm) : math.min(norm, 1f);
                    contributions[count++] = new BasisLocoContribution { Clip = clip, Weight = stateWeight, Time = phase * clipLengths[clip] };
                    return count;
                }
            }
        }
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

        static void NativeWeights(float2 point, BasisLocoState state, in NativeArray<float2> positions, ref FixedList128Bytes<float> weights)
        {
            int start = state == BasisLocoState.Walking ? WalkingClipStart : state == BasisLocoState.Crouching ? CrouchingClipStart : ProneClipStart;
            int count = state == BasisLocoState.Walking ? WalkingChildCount : state == BasisLocoState.Crouching ? CrouchingChildCount : ProneChildCount;
            weights.Clear();
            float total = 0f;
            for (int i = 0; i < count; i++)
            {
                float2 pi = positions[start + i];
                float weight = 1f;
                for (int j = 0; j < count; j++)
                {
                    if (j == i) continue;
                    float2 edge = positions[start + j] - pi;
                    float lenSq = math.lengthsq(edge);
                    float h = lenSq > 1e-12f ? 1f - math.dot(point - pi, edge) / lenSq : 0f;
                    weight = math.min(weight, math.saturate(h));
                }
                weights.Add(weight);
                total += weight;
            }
            if (total <= 1e-6f)
            {
                int nearest = 0;
                float best = float.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    float d = math.lengthsq(point - positions[start + i]);
                    weights[i] = 0f;
                    if (d < best) { best = d; nearest = i; }
                }
                weights[nearest] = 1f;
                return;
            }
            float inv = math.rcp(total);
            for (int i = 0; i < count; i++) weights[i] *= inv;
        }
        static bool IsTree(BasisLocoState state) => state == BasisLocoState.Walking || state == BasisLocoState.Crouching || state == BasisLocoState.Prone;
        static int NativeClipStart(BasisLocoState state) => state == BasisLocoState.Walking ? WalkingClipStart : state == BasisLocoState.Crouching ? CrouchingClipStart : ProneClipStart;
        static int NativeChildCount(BasisLocoState state) => state == BasisLocoState.Walking ? WalkingChildCount : state == BasisLocoState.Crouching ? CrouchingChildCount : ProneChildCount;
        static float NativeStateDelta(BasisLocoState state, in BasisLocoParams p, float dt, in NativeArray<float> clipLengths, in NativeArray<float2> positions, in NativeArray<float> timeScales, ref FixedList128Bytes<float> weights)
        {
            if (!IsTree(state))
            {
                weights.Clear();
                int clip = state == BasisLocoState.Jump ? JumpClip : state == BasisLocoState.Falling ? FallingClip : LandingClip;
                return clipLengths[clip] > 1e-4f ? dt / clipLengths[clip] : 0f;
            }
            NativeWeights(new float2(p.VelocityX, p.VelocityZ), state, positions, ref weights);
            int start = NativeClipStart(state), count = NativeChildCount(state);
            float duration = 0f;
            for (int i = 0; i < count; i++) duration += weights[i] * (clipLengths[start + i] / timeScales[start + i]);
            return duration > 1e-4f ? dt * p.CurrentSpeed / duration : 0f;
        }
        static bool NativeTransition(BasisLocoState state, float previousNorm, float currentNorm, in BasisLocoParams p, out BasisLocoTransition transition)
        {
            transition = default;
            switch (state)
            {
                case BasisLocoState.Walking:
                    if (p.IsJumping) transition = new BasisLocoTransition { Condition = BasisLocoCondition.IsJumpingTrue, To = BasisLocoState.Jump, DurationSeconds = 0.25f };
                    else if (p.ProneState) transition = new BasisLocoTransition { Condition = BasisLocoCondition.ProneTrue, To = BasisLocoState.Prone, DurationSeconds = 0.25f };
                    else if (p.CrouchedState) transition = new BasisLocoTransition { Condition = BasisLocoCondition.CrouchedTrue, To = BasisLocoState.Crouching, DurationSeconds = 0.25f };
                    else if (p.IsFalling) transition = new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingTrue, To = BasisLocoState.Falling, DurationSeconds = 0.25f };
                    else if (p.LandingTrigger && math.floor(currentNorm - 0.79395604f) > math.floor(previousNorm - 0.79395604f)) transition = new BasisLocoTransition { Condition = BasisLocoCondition.LandingTrigger, To = BasisLocoState.Landing, DurationSeconds = 0.25f };
                    break;
                case BasisLocoState.Crouching:
                    if (p.ProneState) transition = new BasisLocoTransition { Condition = BasisLocoCondition.ProneTrue, To = BasisLocoState.Prone, DurationSeconds = 0.25f };
                    else if (!p.CrouchedState) transition = new BasisLocoTransition { Condition = BasisLocoCondition.CrouchedFalse, To = BasisLocoState.Walking, DurationSeconds = 0.25f };
                    break;
                case BasisLocoState.Jump:
                    if (p.IsFalling) transition = new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingTrue, To = BasisLocoState.Falling, DurationSeconds = 0.25f };
                    break;
                case BasisLocoState.Falling:
                    if (!p.IsFalling && math.floor(currentNorm - 0.75f) > math.floor(previousNorm - 0.75f)) transition = new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingFalse, To = BasisLocoState.Walking, DurationSeconds = 0.25f };
                    else if (p.LandingTrigger) transition = new BasisLocoTransition { Condition = BasisLocoCondition.LandingTrigger, To = BasisLocoState.Landing, DurationSeconds = 0.25f };
                    else if (p.IsJumping) transition = new BasisLocoTransition { Condition = BasisLocoCondition.IsJumpingTrue, To = BasisLocoState.Jump, DurationSeconds = 0.25f };
                    break;
                case BasisLocoState.Landing:
                    if (p.IsFalling) transition = new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingTrue, To = BasisLocoState.Falling, DurationSeconds = 0.25f };
                    else if (p.IsJumping) transition = new BasisLocoTransition { Condition = BasisLocoCondition.IsJumpingTrue, To = BasisLocoState.Jump, DurationSeconds = 0.25f };
                    else if (!p.IsFalling) transition = new BasisLocoTransition { Condition = BasisLocoCondition.IsFallingFalse, To = BasisLocoState.Walking, DurationSeconds = 0.25f };
                    break;
                case BasisLocoState.Prone:
                    if (!p.ProneState) transition = new BasisLocoTransition { Condition = BasisLocoCondition.ProneFalse, To = BasisLocoState.Crouching, DurationSeconds = 0.25f };
                    break;
            }
            return transition.DurationSeconds > 0f;
        }
        static int NativeEmit(BasisLocoState state, float norm, float stateWeight, ref FixedList128Bytes<float> weights, in NativeArray<float> clipLengths, in NativeArray<byte> clipLooping, NativeArray<BasisLocoContribution> contributions, int output)
        {
            if (IsTree(state))
            {
                int start = NativeClipStart(state), count = NativeChildCount(state);
                float phase = norm - math.floor(norm);
                for (int i = 0; i < count; i++)
                {
                    float weight = stateWeight * weights[i];
                    if (weight > 1e-5f) contributions[output++] = new BasisLocoContribution { Clip = start + i, Weight = weight, Time = phase * clipLengths[start + i] };
                }
                return output;
            }
            int clip = state == BasisLocoState.Jump ? JumpClip : state == BasisLocoState.Falling ? FallingClip : LandingClip;
            float singlePhase = clipLooping[clip] != 0 ? norm - math.floor(norm) : math.min(norm, 1f);
            contributions[output++] = new BasisLocoContribution { Clip = clip, Weight = stateWeight, Time = singlePhase * clipLengths[clip] };
            return output;
        }
        public static int StepNative(ref BasisLocoSimState s, ref BasisLocoParams p, float dt, in NativeArray<float> clipLengths, in NativeArray<byte> clipLooping, in NativeArray<float2> positions, in NativeArray<float> timeScales, NativeArray<BasisLocoContribution> contributions)
        {
            FixedList128Bytes<float> currentWeights = default, toWeights = default;
            float previousNorm = s.CurrentNorm;
            s.CurrentNorm += NativeStateDelta(s.Current, p, dt, clipLengths, positions, timeScales, ref currentWeights);
            if (s.InTransition)
            {
                s.ToNorm += NativeStateDelta(s.To, p, dt, clipLengths, positions, timeScales, ref toWeights);
                s.TransitionProgress01 += dt * s.TransitionInvDuration;
                if (s.TransitionProgress01 >= 1f)
                {
                    s.Current = s.To;
                    s.CurrentNorm = s.ToNorm;
                    s.InTransition = false;
                    s.TransitionProgress01 = 0f;
                    currentWeights = toWeights;
                    toWeights = default;
                }
            }
            else if (NativeTransition(s.Current, previousNorm, s.CurrentNorm, p, out BasisLocoTransition transition))
            {
                if (transition.Condition == BasisLocoCondition.LandingTrigger) p.LandingTrigger = false;
                s.InTransition = true;
                s.To = transition.To;
                s.ToNorm = 0f;
                s.TransitionProgress01 = 0f;
                s.TransitionInvDuration = math.rcp(math.max(transition.DurationSeconds, 1e-4f));
                NativeStateDelta(s.To, p, 0f, clipLengths, positions, timeScales, ref toWeights);
            }
            int count = NativeEmit(s.Current, s.CurrentNorm, s.InTransition ? 1f - s.TransitionProgress01 : 1f, ref currentWeights, clipLengths, clipLooping, contributions, 0);
            return s.InTransition ? NativeEmit(s.To, s.ToNorm, s.TransitionProgress01, ref toWeights, clipLengths, clipLooping, contributions, count) : count;
        }
    }

    [BurstCompile]
    public struct BasisLocomotionGraphJob : IJob
    {
        public BasisLocoParams Params;
        public float DeltaTime;
        [ReadOnly] public NativeArray<float> ClipLengths;
        [ReadOnly] public NativeArray<byte> ClipLooping;
        [ReadOnly] public NativeArray<float2> ChildPositions;
        [ReadOnly] public NativeArray<float> ChildTimeScales;
        public NativeArray<BasisLocoSimState> Sim;
        public NativeArray<BasisLocoContribution> Contributions;
        public NativeArray<int> ContributionCount;
        public NativeArray<byte> LandingLatch;
        public void Execute()
        {
            BasisLocoSimState state = Sim[0];
            BasisLocoParams parameters = Params;
            parameters.LandingTrigger = LandingLatch[0] != 0;
            ContributionCount[0] = BasisLocomotionGraph.StepNative(ref state, ref parameters, DeltaTime, ClipLengths, ClipLooping, ChildPositions, ChildTimeScales, Contributions);
            Sim[0] = state;
            LandingLatch[0] = (byte)(parameters.LandingTrigger ? 1 : 0);
        }
    }
}
