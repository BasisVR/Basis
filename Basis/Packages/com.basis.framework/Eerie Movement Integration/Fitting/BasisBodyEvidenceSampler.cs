using Basis.IK;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
public static class BasisBodyEvidenceSampler
{
    public const int FrameInterval = 5;
    static NativeReference<BasisBodyEvidenceState> sstate;
    static JobHandle handle;
    static bool scheduled;
    static bool allocated;
    static int frameCounter;
    static float secondsSinceLastSample;
    static readonly System.Collections.Generic.List<float> strackerHeights = new(16);
    public static bool IsRunning => allocated;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Initialize()
    {
        Shutdown();
        sstate = new NativeReference<BasisBodyEvidenceState>(default, Allocator.Persistent);
        allocated = true;
        frameCounter = 0;
        secondsSinceLastSample = 0f;
        Application.quitting -= Shutdown;
        Application.quitting += Shutdown;
    }
    static void Shutdown()
    {
        if (!allocated)
        {
            return;
        }
        CompleteIfPending();
        if (sstate.IsCreated)
        {
            sstate.Dispose();
        }
        allocated = false;
    }
    public static void ResetEvidence()
    {
        if (!allocated)
        {
            return;
        }
        CompleteIfPending();
        sstate.Value = default;
        frameCounter = 0;
        secondsSinceLastSample = 0f;
        BasisDebug.Log("Body evidence reset; re-observing the player's size.", BasisDebug.LogTag.Avatar);
    }
    static void CompleteIfPending()
    {
        if (scheduled)
        {
            handle.Complete();
            scheduled = false;
        }
    }
    public static void Simulate(float deltaTime)
    {
        if (!allocated)
        {
            return;
        }

        secondsSinceLastSample += deltaTime;

        if (BasisDeviceManagement.IsUserInDesktop())
        {
            return;
        }

        frameCounter++;
        if (frameCounter < FrameInterval)
        {
            return;
        }
        frameCounter = 0;

        CompleteIfPending();

        if (!TryGather(out BasisBodyEvidenceSample sample, out FixedList128Bytes<float> trackerHeights))
        {
            return;
        }

        secondsSinceLastSample = 0f;

        var job = new BasisBodyEvidenceJob
        {
            State = sstate,
            Sample = sample,
            TrackerHeights = trackerHeights,
            FootMountAllowance = BasisCalibrationMath.FootMountAllowanceMeters,
            FootBand = BasisCalibrationMath.FootBandMeters,
            MinFootBandTrackers = BasisCalibrationMath.MinFootBandTrackers,
            MinPlausible = BasisHeightDriver.MinPlausibleBodyMeasure,
            MaxPlausible = BasisHeightDriver.MaxPlausibleBodyMeasure,
        };
        handle = job.Schedule();
        scheduled = true;
        // Self-sufficient kick, same reasoning as BasisAvatarDriver.ScheduleReadBlendShapes and
        // BasisContentSphereBillboardDriver.ScheduleSimulate (project_basis_schedule_kick_audit Round
        // 6): something later in this same frame's LateUpdate always calls
        // JobHandle.ScheduleBatchedJobs() before CompleteIfPending() ever joins this handle, so today
        // this is covered incidentally rather than guaranteed by this function on its own.
        JobHandle.ScheduleBatchedJobs();
    }
    static bool TryGather(out BasisBodyEvidenceSample sample, out FixedList128Bytes<float> trackerHeights)
    {
        sample = default;
        trackerHeights = default;

        BasisDeviceManagement manager = BasisDeviceManagement.Instance;
        if (manager == null)
        {
            return false;
        }

        sample.DeltaSeconds = secondsSinceLastSample;

        BasisInput head = BasisLocalCameraDriver.Instance?.BasisLockToInput?.BasisInput;
        if (head != null && !SMModuleSitStand.IsSteatedMode)
        {
            Vector3 headPos = head.UnscaledDeviceCoord.position;
            if (headPos.sqrMagnitude > 1e-4f)
            {
                sample.HeadY = headPos.y;
                sample.HeadValid = true;
                sample.InjectedVerticalOffset = BasisLocalPlayspaceMover.VerticalOffset + BasisHeightDriver.HeightModeGroundingOffset;
            }
        }

        if (manager.FindDevice(out BasisInput left, BasisBoneTrackedRole.LeftHand) && manager.FindDevice(out BasisInput right, BasisBoneTrackedRole.RightHand))
        {
            Vector3 l = HandSpanPoint(left);
            Vector3 r = HandSpanPoint(right);
            if (l.sqrMagnitude > 1e-4f && r.sqrMagnitude > 1e-4f)
            {
                sample.HandSpan = Vector3.Distance(new Vector3(l.x, 0f, l.z), new Vector3(r.x, 0f, r.z));
                sample.HandsValid = sample.HandSpan > 0f;
            }
        }

        if (!sample.HeadValid && !sample.HandsValid)
        {
            return false;
        }

        strackerHeights.Clear();
        BasisObservableList<BasisInput> devices = manager.AllInputDevices;
        int count = devices.Count;
        for (int Index = 0; Index < count; Index++)
        {
            BasisInput input = devices[Index];
            if (input == null) continue;
            if (input is BasisTouchInputDevice) continue;
            if (input.IsLinked) continue;
            if (input.DeviceMatchSettings != null && input.DeviceMatchSettings.HasTrackedRole) continue;

            Vector3 unscaled = input.UnscaledDeviceCoord.position;
            if (unscaled.sqrMagnitude < 1e-4f) continue;
            strackerHeights.Add(unscaled.y);
            if (strackerHeights.Count >= trackerHeights.Capacity) break;
        }

        for (int Index = 0; Index < strackerHeights.Count; Index++)
        {
            trackerHeights.Add(strackerHeights[Index]);
        }
        return true;
    }
    static Vector3 HandSpanPoint(BasisInput input) => input is BasisInputController controller ? controller.UnscaledHandTarget : input.UnscaledDeviceCoord.position;
    public static bool TryGetEyeHeight(out float eyeHeight, out float confidence)
    {
        eyeHeight = 0f;
        confidence = 0f;
        if (!allocated)
        {
            return false;
        }
        CompleteIfPending();
        BasisBodyEvidenceState state = sstate.Value;
        return BasisBodyEvidenceCore.TryGetEstimate(state.Eye, out eyeHeight, out confidence);
    }
    public static bool TryGetArmSpan(out float armSpan, out float confidence)
    {
        armSpan = 0f;
        confidence = 0f;
        if (!allocated)
        {
            return false;
        }
        CompleteIfPending();
        BasisBodyEvidenceState state = sstate.Value;
        return BasisBodyEvidenceCore.TryGetEstimate(state.ArmSpan, out armSpan, out confidence);
    }
    public static bool LooksLikeADifferentPerson()
    {
        if (!allocated)
        {
            return false;
        }
        CompleteIfPending();
        BasisBodyEvidenceState state = sstate.Value;
        return BasisBodyEvidenceCore.LooksLikeADifferentPerson(state.Eye);
    }
    public static void GetSampleCounts(out int eyeSamples, out int spanSamples)
    {
        eyeSamples = 0;
        spanSamples = 0;
        if (!allocated)
        {
            return;
        }
        CompleteIfPending();
        BasisBodyEvidenceState state = sstate.Value;
        eyeSamples = state.Eye.SampleCount;
        spanSamples = state.ArmSpan.SampleCount;
    }
    [BurstCompile]
    struct BasisBodyEvidenceJob : IJob
    {
        public NativeReference<BasisBodyEvidenceState> State;
        public BasisBodyEvidenceSample Sample;
        public FixedList128Bytes<float> TrackerHeights;
        public float FootMountAllowance, FootBand;
        public int MinFootBandTrackers;
        public float MinPlausible, MaxPlausible;
        public void Execute()
        {
            BasisBodyEvidenceState state = State.Value;
            bool hasFloor = BasisBodyEvidenceCore.TryEstimateFloor( TrackerHeights, Sample.HeadY, FootMountAllowance, FootBand, MinFootBandTrackers, MinPlausible, MaxPlausible, out float floorY);
            BasisBodyEvidenceCore.Fold(ref state, Sample, hasFloor, floorY, MinPlausible, MaxPlausible);
            State.Value = state;
        }
    }
}
