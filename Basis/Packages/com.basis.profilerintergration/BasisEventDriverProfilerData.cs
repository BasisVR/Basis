#if UNITY_EDITOR
using System.Diagnostics;

/// <summary>
/// Lightweight per-frame timing data for BasisEventDriver.
/// Entirely stripped from player builds via UNITY_EDITOR define.
/// Zero overhead when <see cref="Enabled"/> is false (single bool check per section).
/// Written by BasisEventDriver, read by the editor profiler window.
/// </summary>
public static class BasisEventDriverProfilerData
{
    /// <summary>Set true by the editor window to start collecting. BasisEventDriver checks this.</summary>
    public static volatile bool Enabled;

    // ── Network apply breakdown ─────────────────────────────────────────
    public static double NetworkApplyMs;
    public static double Net_TransmitPickupsMs;
    public static double Net_FireBeforeApplyMs;
    public static double Net_SimulateNetworkApplyMs;
    public static double Net_CompleteRemoteLerpMs;

    // SimulateNetworkApply sub-breakdown
    public static bool Net_InterpolationJobWasIncomplete;
    public static double Net_RemoteDriverApplyMs;
    public static double Net_ReceiverApplyLoopMs;
    public static int Net_ReceiverCount;
    public static double Net_BoneJobScheduleMs;
    public static double Net_BoneJobCompleteMs;
    public static bool Net_BoneJobWasIncomplete;

    // BasisTransmissionResults.Simulate breakdown
    public static double Net_TransmitSim_FillPositionsMs;
    public static double Net_TransmitSim_JobScheduleMs;
    public static double Net_TransmitSim_CompressMs;
    public static double Net_TransmitSim_JobCompleteMs;
    public static double Net_TransmitSim_PostProcessMs;
    public static double Net_TransmitSim_TalkingPointsMs;
    public static bool Net_TransmitSimRanThisTick;

    // Device management
    public static double DeviceManagementMs;

    // ── Remote audio breakdown ──────────────────────────────────────────
    public static double RemoteAudioSimulateMs;
    public static double RemoteAudioApplyMs;
    public static int RemoteAudioDriverCount;

    // ── Remote face breakdown ───────────────────────────────────────────
    public static double RemoteFaceSimulateMs;
    public static double RemoteFaceApplyMs;
    public static double RemoteFace_JobCompleteMs;
    public static double RemoteFace_EyeWriteMs;
    public static double RemoteFace_BlinkWriteMs;
    public static int RemoteFace_Count;
    public static int RemoteFace_BlinkWriteCount;
    public static bool RemoteFaceJobWasIncomplete;

    // Local player
    public static double LocalPlayerMs;

    // BlendShape driver
    public static double BlendShapeSimulateMs;
    public static double BlendShapeApplyMs;

    // JigglePhysics
    public static double JiggleScheduleMs;
    public static double JigglePoseMs;
    public static double JiggleCompletePoseMs;

    // Nameplate
    public static double NamePlateScheduleMs;
    public static double NamePlateCompleteMs;
    public static bool NamePlateJobWasIncomplete;

    // Pose LOD diagnostics
    public static int PoseLod_Applied;
    public static int PoseLod_Skipped;
    public static int PoseLod_Lod0;
    public static int PoseLod_Lod1;
    public static int PoseLod_Lod2;
    public static int PoseLod_Lod3;
    public static float PoseLod_Bias;

    // Misc
    public static double BTweenMs;
    public static double MicrophoneMs;
    public static double NetworkTransmitMs;
    public static double ShadowCloneMs;

    // Totals
    public static double LateUpdateTotalMs;
    public static double OnBeforeRenderMs;
    public static int FrameCount;

    // ── History ring buffer ─────────────────────────────────────────────
    public const int HistorySize = 300;
    public static readonly double[] LateUpdateHistory = new double[HistorySize];
    public static readonly double[] NetworkApplyHistory = new double[HistorySize];
    public static readonly double[] RemoteAudioHistory = new double[HistorySize];
    public static readonly double[] RemoteFaceHistory = new double[HistorySize];
    public static readonly double[] JiggleHistory = new double[HistorySize];
    public static readonly double[] LocalPlayerHistory = new double[HistorySize];
    public static readonly double[] BlendShapeHistory = new double[HistorySize];
    public static int HistoryIndex;

    public static void PushHistory()
    {
        int idx = HistoryIndex % HistorySize;
        LateUpdateHistory[idx] = LateUpdateTotalMs;
        NetworkApplyHistory[idx] = NetworkApplyMs;
        RemoteAudioHistory[idx] = RemoteAudioSimulateMs + RemoteAudioApplyMs;
        RemoteFaceHistory[idx] = RemoteFaceSimulateMs + RemoteFaceApplyMs;
        JiggleHistory[idx] = JiggleScheduleMs + JigglePoseMs + JiggleCompletePoseMs;
        LocalPlayerHistory[idx] = LocalPlayerMs;
        BlendShapeHistory[idx] = BlendShapeSimulateMs + BlendShapeApplyMs;
        HistoryIndex++;
        FrameCount++;
    }

    // ── Stopwatch helper ────────────────────────────────────────────────
    public static readonly Stopwatch SW = new Stopwatch();
    public static readonly Stopwatch SW2 = new Stopwatch(); // nested timing

    public static void Begin()  { SW.Restart(); }
    public static double End()  { SW.Stop(); return SW.Elapsed.TotalMilliseconds; }
    public static void Begin2() { SW2.Restart(); }
    public static double End2() { SW2.Stop(); return SW2.Elapsed.TotalMilliseconds; }
}
#endif
