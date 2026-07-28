using Unity.Profiling;

namespace Basis.EventDriver
{
    /// <summary>
    /// Unity profiler markers for every call <see cref="BasisEventDriver"/> makes per frame.
    /// Unlike the stopwatch sections in <see cref="BasisEventDriverProfileSections"/>, these are
    /// live in any player with <c>ENABLE_PROFILER</c> — a development build — and compile out
    /// entirely otherwise. Every name starts with <c>BasisDriver.</c> so one search term in the
    /// profiler brings up the whole frame.
    /// </summary>
    internal static class BasisEventDriverMarkers
    {
        public static readonly ProfilerMarker Update = new ProfilerMarker("BasisDriver.Update");
        public static readonly ProfilerMarker FixedUpdate = new ProfilerMarker("BasisDriver.FixedUpdate");
        public static readonly ProfilerMarker LateUpdate = new ProfilerMarker("BasisDriver.LateUpdate");
        public static readonly ProfilerMarker BeforeRender = new ProfilerMarker("BasisDriver.OnBeforeRender");

        public static readonly ProfilerMarker NetworkCompleteCompute = new ProfilerMarker("BasisDriver.Network.CompleteCompute");
        public static readonly ProfilerMarker FrameClockTick = new ProfilerMarker("BasisDriver.FrameClock.Tick");
        public static readonly ProfilerMarker VisemeSimulate = new ProfilerMarker("BasisDriver.LocalPlayer.VisemeSimulate");
        public static readonly ProfilerMarker MainThreadActions = new ProfilerMarker("BasisDriver.MainThreadActions");
        public static readonly ProfilerMarker LifecycleQueue = new ProfilerMarker("BasisDriver.Network.LifecycleQueue");
        public static readonly ProfilerMarker ConnectionWatchdog = new ProfilerMarker("BasisDriver.Network.ConnectionWatchdog");
        public static readonly ProfilerMarker InputSystemUpdate = new ProfilerMarker("BasisDriver.Input.InputSystemUpdate");
        public static readonly ProfilerMarker OscAcquisition = new ProfilerMarker("BasisDriver.OSC.Acquisition");
        public static readonly ProfilerMarker PerformanceLimits = new ProfilerMarker("BasisDriver.PerfLimits.AvatarLimits");
        public static readonly ProfilerMarker DebugOptions = new ProfilerMarker("BasisDriver.DebugOptions");
        public static readonly ProfilerMarker GazeFoveationAuto = new ProfilerMarker("BasisDriver.Eye.GazeFoveationAuto");
        public static readonly ProfilerMarker HighPlayerCap = new ProfilerMarker("BasisDriver.PerfLimits.HighPlayerCap");
        public static readonly ProfilerMarker OnUpdateCallbacks = new ProfilerMarker("BasisDriver.OnUpdateCallbacks");

        public static readonly ProfilerMarker SceneFactorySimulate = new ProfilerMarker("BasisDriver.SceneFactory.Simulate");

        public static readonly ProfilerMarker EyeTrackingSimulate = new ProfilerMarker("BasisDriver.Eye.TrackingSimulate");
        public static readonly ProfilerMarker CommsActuators = new ProfilerMarker("BasisDriver.HVRComms.Actuators");
        public static readonly ProfilerMarker NetworkApply = new ProfilerMarker("BasisDriver.Network.Apply");
        public static readonly ProfilerMarker NetFireBeforeApply = new ProfilerMarker("BasisDriver.Network.FireBeforeApply");
        public static readonly ProfilerMarker SyncTransmitOwned = new ProfilerMarker("BasisDriver.Sync.TransmitOwned");
        public static readonly ProfilerMarker MicrophoneUpdate = new ProfilerMarker("BasisDriver.Microphone.Update");
        public static readonly ProfilerMarker SyncScheduleRemote = new ProfilerMarker("BasisDriver.Sync.ScheduleRemote");
        public static readonly ProfilerMarker SyncCompleteRemote = new ProfilerMarker("BasisDriver.Sync.CompleteRemote");
        public static readonly ProfilerMarker NetFireAfterRemoteSync = new ProfilerMarker("BasisDriver.Network.FireAfterRemoteSync");
        public static readonly ProfilerMarker NetSimulateApply = new ProfilerMarker("BasisDriver.Network.SimulateApply");
        public static readonly ProfilerMarker DeviceManagementKick = new ProfilerMarker("BasisDriver.DeviceManagement.Kick");
        public static readonly ProfilerMarker DeviceManagement = new ProfilerMarker("BasisDriver.DeviceManagement.Simulate");
        public static readonly ProfilerMarker BTween = new ProfilerMarker("BasisDriver.BTween.Simulate");
        public static readonly ProfilerMarker LocalPlayer = new ProfilerMarker("BasisDriver.LocalPlayer");
        public static readonly ProfilerMarker FacialBlink = new ProfilerMarker("BasisDriver.LocalPlayer.FacialBlink");
        public static readonly ProfilerMarker VisemeApply = new ProfilerMarker("BasisDriver.LocalPlayer.VisemeApply");
        public static readonly ProfilerMarker LocalPlayerSimulate = new ProfilerMarker("BasisDriver.LocalPlayer.Simulate");
        public static readonly ProfilerMarker LocalHandApply = new ProfilerMarker("BasisDriver.LocalPlayer.HandApply");
        public static readonly ProfilerMarker LocalCameraSimulate = new ProfilerMarker("BasisDriver.LocalPlayer.CameraSimulate");
        public static readonly ProfilerMarker LocalEyeSimulate = new ProfilerMarker("BasisDriver.LocalPlayer.EyeSimulate");
        public static readonly ProfilerMarker LocalEyeApply = new ProfilerMarker("BasisDriver.LocalPlayer.EyeApply");
        public static readonly ProfilerMarker RemoteBoneComplete = new ProfilerMarker("BasisDriver.RemoteBone.CompleteJobs");
        public static readonly ProfilerMarker RemoteAudioSimulate = new ProfilerMarker("BasisDriver.RemoteAudio.Simulate");
        public static readonly ProfilerMarker RemoteAudioApply = new ProfilerMarker("BasisDriver.RemoteAudio.Apply");
        public static readonly ProfilerMarker NamePlateSchedule = new ProfilerMarker("BasisDriver.NamePlate.Schedule");
        public static readonly ProfilerMarker NamePlateComplete = new ProfilerMarker("BasisDriver.NamePlate.Complete");
        public static readonly ProfilerMarker NamePlateFinish = new ProfilerMarker("BasisDriver.NamePlate.Finish");
        public static readonly ProfilerMarker ContentSphereSchedule = new ProfilerMarker("BasisDriver.ContentSphere.Schedule");
        public static readonly ProfilerMarker ContentSphereComplete = new ProfilerMarker("BasisDriver.ContentSphere.Complete");
        public static readonly ProfilerMarker SteamAudioSchedule = new ProfilerMarker("BasisDriver.SteamAudio.Schedule");
        public static readonly ProfilerMarker SteamAudioApply = new ProfilerMarker("BasisDriver.SteamAudio.Apply");
        public static readonly ProfilerMarker RemoteFaceSimulate = new ProfilerMarker("BasisDriver.RemoteFace.Simulate");
        public static readonly ProfilerMarker RemoteFaceApply = new ProfilerMarker("BasisDriver.RemoteFace.Apply");
        public static readonly ProfilerMarker BuiltInAddresses = new ProfilerMarker("BasisDriver.HVRComms.BuiltInAddresses");
        public static readonly ProfilerMarker ReadBlendShapes = new ProfilerMarker("BasisDriver.BlendShape.ScheduleRead");
        public static readonly ProfilerMarker ShadowClone = new ProfilerMarker("BasisDriver.BlendShape.ShadowClone");
        public static readonly ProfilerMarker AuthoredMotionSchedule = new ProfilerMarker("BasisDriver.AuthoredMotion.Schedule");
        public static readonly ProfilerMarker AuthoredMotionComplete = new ProfilerMarker("BasisDriver.AuthoredMotion.Complete");
        public static readonly ProfilerMarker VariableNetworking = new ProfilerMarker("BasisDriver.HVRComms.VariableNetworking");
        public static readonly ProfilerMarker ConstraintSchedule = new ProfilerMarker("BasisDriver.Constraints.Schedule");
        public static readonly ProfilerMarker ConstraintComplete = new ProfilerMarker("BasisDriver.Constraints.Complete");
        public static readonly ProfilerMarker JiggleSchedule = new ProfilerMarker("BasisDriver.Jiggle.Schedule");
        public static readonly ProfilerMarker JiggleCullCameras = new ProfilerMarker("BasisDriver.Jiggle.CullingCameras");
        public static readonly ProfilerMarker JigglePrepare = new ProfilerMarker("BasisDriver.Jiggle.PrepareSimulate");
        public static readonly ProfilerMarker JiggleDispatch = new ProfilerMarker("BasisDriver.Jiggle.DispatchSimulate");
        public static readonly ProfilerMarker JiggleSchedulePose = new ProfilerMarker("BasisDriver.Jiggle.SchedulePose");
        public static readonly ProfilerMarker JiggleCompletePose = new ProfilerMarker("BasisDriver.Jiggle.CompletePose");
        public static readonly ProfilerMarker JiggleCompletePoseDeferred = new ProfilerMarker("BasisDriver.Jiggle.CompletePose(Deferred)");
        public static readonly ProfilerMarker JiggleRender = new ProfilerMarker("BasisDriver.Jiggle.Render");
        public static readonly ProfilerMarker AfterAvatarChanges = new ProfilerMarker("BasisDriver.Network.AfterAvatarChanges");
        public static readonly ProfilerMarker FrameSync = new ProfilerMarker("BasisDriver.FrameSync");
        public static readonly ProfilerMarker JoinLeaveNotification = new ProfilerMarker("BasisDriver.UI.JoinLeaveNotification");
        public static readonly ProfilerMarker SimulateBeacon = new ProfilerMarker("BasisDriver.Player.SimulateBeacon");
        public static readonly ProfilerMarker NetworkBeginCompute = new ProfilerMarker("BasisDriver.Network.BeginCompute");
        public static readonly ProfilerMarker OnLateUpdateCallbacks = new ProfilerMarker("BasisDriver.OnLateUpdateCallbacks");
        public static readonly ProfilerMarker GizmoRender = new ProfilerMarker("BasisDriver.Gizmo.Render");

        public static readonly ProfilerMarker SimulateOnRender = new ProfilerMarker("BasisDriver.LocalPlayer.SimulateOnRender");
        public static readonly ProfilerMarker MicrophoneIcon = new ProfilerMarker("BasisDriver.Microphone.IconSimulate");
    }
}
