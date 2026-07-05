using Basis.BTween;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Debugging;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Transmitters;
using Basis.BasisUI;
using Basis.Scripts.UI;
using Basis.Scripts.UI.NamePlate;
using GatorDragonGames.JigglePhysics;
using HVR.Basis.Comms;
using SteamAudio;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using static Basis.EventDriver.BasisEventDriverProfileSections;

namespace Basis.EventDriver
{
    /// <summary>
    /// Central per-frame driver that coordinates device actions, networking compute/apply,
    /// physics scheduling for JigglePhysics, and various local simulation hooks.
    /// </summary>
    [DefaultExecutionOrder(-31950)]
    public partial class BasisEventDriver : MonoBehaviour
    {
        // ── Platform flag (single #if, used as runtime bool everywhere else) ──
        public static readonly bool IsHeadlessClient =
#if UNITY_SERVER
        true;
#else
            false;
#endif
        private static readonly List<Camera> JiggleCullCameras = new List<Camera>(8);
        // Profiler section IDs live in BasisEventDriverProfileSections (pulled in via `using static`).
        // ── Partial method declarations (calls are stripped in non-editor builds) ──
        partial void ProfileLateUpdateInit();
        partial void ProfileBegin(int section);
        partial void ProfileBegin2();
        partial void ProfileEnd(int section);
        partial void ProfileEnd2(int section);
        partial void ProfileLateUpdateFinish();
        partial void ProfileBeforeRenderInit();
        partial void ProfileBeforeRenderFinish();
        /// <summary>
        /// Accumulator used to track elapsed time since the last interval tick.
        /// </summary>
        public float timeSinceLastUpdate = 0f;
        /// <summary>
        /// Frame delta time (scaled).
        /// </summary>
        public float DeltaTime;
        /// <summary>
        /// Current time as a double (scaled), mirrored from <see cref="Time.timeAsDouble"/>.
        /// </summary>
        public double TimeAsDouble;
        /// <summary>
        /// Fixed-step time as a double, mirrored from <see cref="Time.fixedTimeAsDouble"/>.
        /// </summary>
        public double fixedTimeAsDouble;
        /// <summary>
        /// Fixed-step delta time in seconds.
        /// </summary>
        public float fixedDeltaTime;
        /// <summary>
        /// Unscaled frame delta time in seconds.
        /// </summary>
        public float unscaledDeltaTime;
        /// <summary>
        /// realtimeSinceStartupAsDouble
        /// </summary>
        public double realtimeSinceStartupAsDouble;
        /// <summary>
        /// material we use to display jiggle physics visually
        /// </summary>
        [SerializeField]
        private UnityEngine.Material proceduralMaterial;
        /// <summary>
        /// mesh we use to display around the jiggle physics
        /// </summary>
        [SerializeField]
        private Mesh sphereMesh;
        /// <summary>
        /// mesh we use to display capsule jiggle physics colliders
        /// </summary>
        [SerializeField]
        private Mesh capsuleMesh;
        /// <summary>
        /// Instance of Basis Event Driver
        /// </summary>
        public static BasisEventDriver Instance;
        public static bool StateOfOnRenderBefore = false;
        /// <summary>
        /// Unity enable hook. Subscribes render callbacks (client), initializes scene and network drivers.
        /// </summary>
        public void OnEnable()
        {
            Instance = this;
            if (!IsHeadlessClient)
            {
                Application.onBeforeRender += OnBeforeRender;
            }
            BasisOpenLipSyncDriver.BeginInitialize();
            BasisSceneFactory.Initialize();
            Basis.Scripts.Networking.Sync.BasisSyncDriver.Initialize();
            RemoteBoneJobSystem.Initialize();
            BasisOpenLipSyncDriver.EndInitialize();
        }

        /// <summary>
        /// Unity destroy hook. Cleans up network/physics resources and unsubscribes callbacks.
        /// </summary>
        public void OnDestroy()
        {
            BasisOpenLipSyncDriver.Shutdown();
            Basis.Scripts.Networking.Sync.BasisSyncDriver.OnDestroy();
            Application.onBeforeRender -= OnBeforeRender;
            RemoteBoneJobSystem.Dispose();
            BasisAuthoredMotionSystem.Dispose();
            BasisAvatarBufferPool.Deinitialize();
        }

        /// <summary>
        /// Unity disable hook. Unsubscribes from the before-render callback on clients.
        /// </summary>
        public void OnDisable()
        {
            if (!IsHeadlessClient)
            {
                Application.onBeforeRender -= OnBeforeRender;
            }
        }
        /// <summary>
        /// Unity update loop. Drains main-thread actions, advances network simulation (compute),
        /// schedules remote interpolation, updates input on clients, and runs periodic tasks.
        /// </summary>
        public void Update()
        {
            try { UpdateBody(); }
            catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver.Update failed: {ex}", BasisDebug.LogTag.Event); }
        }

        private void UpdateBody()
        {
            DeltaTime = Time.deltaTime;
            unscaledDeltaTime = Time.unscaledDeltaTime;
            realtimeSinceStartupAsDouble = Time.realtimeSinceStartupAsDouble;
            TimeAsDouble = Time.timeAsDouble;

            // Join the network compute kicked off at the tail of the previous LateUpdate, before
            // the main-thread action drain and join/leave lifecycle below mutate any receiver.
            BasisNetworkManagement.CompleteNetworkCompute(DeltaTime);

            BasisFrameClock.Tick(unscaledDeltaTime);

            if (BasisLocalPlayer.PlayerReady)
            {
                BasisLocalPlayer.Instance.LocalVisemeDriver.Simulate(DeltaTime);
            }
            // Drain everything that arrived from worker threads
            while (BasisDeviceManagement.mainThreadActions.TryDequeue(out System.Action action))
            {
                try
                {
                    action.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"MainThread action failed: {ex}");
                }
            }
            // Player join/leave work is budgeted separately so a mass disconnect
            // (hundreds of players at once) can't chain N synchronous GameObject.Destroy
            // calls in a single frame and stall the renderer.
            BasisNetworkHandleRemoval.ProcessLifecycleQueue(BasisNetworkHandleRemoval.LifecycleBudgetPerFrame);
            if (!IsHeadlessClient)
            {
                InputSystem.Update();
            }

            OSCAcquisitionServer.Simulate();
            SMModuleAvatarPerformanceLimits.Simulate();
            SMModuleDebugOptions.Simulate();
            Basis.Scripts.Device_Management.EyeTracking.BasisGazeFoveationAutoDriver.Simulate();
            BasisHighPlayerCapPerformanceMode.Simulate();
            timeSinceLastUpdate += DeltaTime;
        }

        /// <summary>
        /// Fixed-step simulation used for scene-level processing.
        /// </summary>
        public void FixedUpdate()
        {
            try { FixedUpdateBody(); }
            catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver.FixedUpdate failed: {ex}", BasisDebug.LogTag.Event); }
        }

        private void FixedUpdateBody()
        {
            fixedDeltaTime = Time.fixedDeltaTime;
            fixedTimeAsDouble = Time.fixedTimeAsDouble;
            if (BasisLocalPlayer.PlayerReady)
            {
                BasisSceneFactory.Simulate(fixedDeltaTime);
            }
        }
        /// <summary>
        /// LateUpdate step for device management loop, eye simulation, local player late sim,
        /// microphone updates (client), network apply, and JigglePhysics scheduling/pose/render.
        /// </summary>
        public void LateUpdate()
        {
            try { LateUpdateBody(); }
            catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver.LateUpdate failed: {ex}", BasisDebug.LogTag.Event); }
        }

        private void LateUpdateBody()
        {
            ProfileLateUpdateInit();

            if (StateOfOnRenderBefore)
            {
                OnBeforeRender();
            }

            // Comms eye/Vixxy/activity actuation is pumped in front of the network-apply barrier so
            // its main-thread cost overlaps the in-flight BasisRemoteNetworkDriver interpolation
            // jobs (InterpolateBoneRotationsJob + FilterBoneRotationsOneEuroJob), which
            // SimulateNetworkApply's Apply() completes below. Vixxy actuates blendshapes/materials,
            // so it must stay ahead of BasisBlendShapeDriver and the render. VariableNetworking is
            // split off to a later barrier (see the AuthoredMotion schedule/complete below) — it
            // produces no visible write this frame, so it hides behind a different job. Safe: the
            // comms batch reads only its own networked variable state, never the remote pose/bone
            // output produced by SimulateNetworkApply.
            Basis.Scripts.Device_Management.EyeTracking.BasisEyeTrackingManager.Simulate();
            HVRCommsUpdateDriver.SimulateActuators();

            ProfileBegin(PROF_NETWORK_APPLY);
            ProfileBegin2();
            BasisLocalPlayer.FireJustBeforeNetworkApply();
            ProfileEnd2(PROF_NET_FIRE_BEFORE_APPLY);
            ProfileBegin2();
            Basis.Scripts.Networking.Sync.BasisSyncDriver.TransmitOwned(TimeAsDouble);
            ProfileEnd2(PROF_NET_TRANSMIT_PICKUPS);
            ProfileBegin2();
#if !UNITY_SERVER && !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.MicrophoneUpdate();
#endif
            ProfileEnd2(PROF_NET_MICROPHONE);
            Basis.Scripts.Networking.Sync.BasisSyncDriver.ScheduleRemote(DeltaTime);
            ProfileBegin2();
            Basis.Scripts.Networking.Sync.BasisSyncDriver.CompleteRemote();
            ProfileEnd2(PROF_NET_COMPLETE_REMOTE_LERP);
            BasisLocalPlayer.FireAfterRemoteSyncInterpolated();
            ProfileBegin2();
            BasisNetworkManagement.SimulateNetworkApply();
            ProfileEnd2(PROF_NET_SIMULATE_APPLY);
            ProfileEnd(PROF_NETWORK_APPLY);

            // ── Device management ──
            ProfileBegin(PROF_DEVICE_MANAGEMENT);
            if (BasisDeviceManagement.HasEvents)
            {
                BasisDeviceManagement.Instance.Simulate();
            }
            ProfileEnd(PROF_DEVICE_MANAGEMENT);

            // ── BTween ──
            ProfileBegin(PROF_BTWEEN);
            BasisTweenManager.Simulate(realtimeSinceStartupAsDouble);
            ProfileEnd(PROF_BTWEEN);

            // ── Local player ──
            ProfileBegin(PROF_LOCAL_PLAYER);
            if (BasisLocalPlayer.PlayerReady)
            {
                BasisLocalCameraDriver LocalCameraDriver = BasisLocalCameraDriver.Instance;
                BasisLocalPlayer localplayer = BasisLocalPlayer.Instance;
                localplayer.FacialBlinkDriver.Simulate(TimeAsDouble);
                localplayer.LocalVisemeDriver.Apply();
                localplayer.Simulate(DeltaTime);
                // Complete the finger slerp job (TransformAccessArray write) before touching the
                // camera transform, so Simulate never overlaps jobified transform access.
                localplayer.LocalHandDriver.Apply();
                LocalCameraDriver.Simulate(DeltaTime);
                localplayer.LocalEyeDriver.Simulate(DeltaTime);
            }
            ProfileEnd(PROF_LOCAL_PLAYER);

            BasisNetworkManagement.CompleteRemoteBoneJobSystemJobs();

            // ── Remote audio simulate ──
            ProfileBegin(PROF_REMOTE_AUDIO_SIMULATE);
            BasisRemoteAudioDriver.Simulate(DeltaTime);
            ProfileEnd(PROF_REMOTE_AUDIO_SIMULATE);

            // Complete the eye apply here rather than right after its schedule in LocalEyeDriver.Simulate,
            // so the eye compute/apply jobs overlap the remote bone complete + remote audio simulate above.
            // Still ahead of JigglePhysics.ScheduleSimulate, so the transform write has no jiggle job to stall on.
            if (BasisLocalPlayer.PlayerReady)
            {
                BasisLocalPlayer.Instance.LocalEyeDriver.Apply();
            }

            // ── Nameplate schedule ──
            ProfileBegin(PROF_NAMEPLATE_SCHEDULE);
            BasisRemoteNamePlateDriver.ScheduleSimulate(TimeAsDouble);
            ProfileEnd(PROF_NAMEPLATE_SCHEDULE);
            BasisContentSphereBillboardDriver.ScheduleSimulate();
#if STEAMAUDIO_ENABLED
            SteamAudioManager.Schedule();
#endif

            // ── Remote face simulate (job schedule) ──
            ProfileBegin(PROF_REMOTE_FACE_SIMULATE);
            BasisRemoteFaceManagement.Simulate(TimeAsDouble, DeltaTime);
            ProfileEnd(PROF_REMOTE_FACE_SIMULATE);

            // ── Remote audio apply ──
            ProfileBegin(PROF_REMOTE_AUDIO_APPLY);
            BasisRemoteAudioDriver.Apply();
#if STEAMAUDIO_ENABLED
            SteamAudioManager.Apply();
#endif
            ProfileEnd(PROF_REMOTE_AUDIO_APPLY);

            try
            {
                HVRBasisBuiltInAddresses.Simulate();
            }
            catch (Exception ex)
            {
                BasisDebug.LogErrorOnce($"HVRBasisBuiltInAddresses.Simulate failed: {ex}", BasisDebug.LogTag.Event);
            }

            // ── BlendShape apply ──
            if (BasisSettingsDefaults.LocalHeadBlendShapes.RawValue)
            {
                BasisAvatarDriver.ScheduleReadBlendShapes();
            }

            // ── Authored motion: write non-humanoid authored bones before jiggle samples them ──
            // Split schedule/complete (was a synchronous Complete(Schedule())) so the authored
            // transform-write job overlaps a slice of independent main-thread work instead of
            // stalling on it. VariableNetworking is that filler: it touches none of the authored
            // transforms, produces no avatar-visible write this frame, and stays ahead of the
            // AfterAvatarChanges eye read below — so its cost hides behind the job's wall-clock.
            var authoredMotionJob = BasisAuthoredMotionSystem.Schedule();
            HVRCommsUpdateDriver.SimulateVariableNetworking();
            BasisAuthoredMotionSystem.Complete(authoredMotionJob);

            // ── JigglePhysics schedule ──
            ProfileBegin(PROF_JIGGLE_SCHEDULE);

            JiggleCullCameras.Clear();
            var jiggleCullCamera = BasisLocalCameraDriver.CameraInstance;
            if (jiggleCullCamera != null)
            {
                JiggleCullCameras.Add(jiggleCullCamera);
            }
            BasisCullingCameraRegistry.CollectInto(JiggleCullCameras);
            JigglePhysics.SetCullingCameras(JiggleCullCameras);

            fixedDeltaTime = Time.fixedDeltaTime;
            JigglePhysics.ScheduleSimulate(TimeAsDouble, fixedDeltaTime);

            ProfileEnd(PROF_JIGGLE_SCHEDULE);

            // ── Network transmit (reads bone results via GetOutGoingMouth) ──
            ProfileBegin(PROF_NETWORK_TRANSMIT);
            BasisNetworkTransmitter.AfterAvatarChanges?.Invoke();
            ProfileEnd(PROF_NETWORK_TRANSMIT);

            // ── JigglePhysics pose ──
            ProfileBegin(PROF_JIGGLE_POSE);
            JigglePhysics.SchedulePose(TimeAsDouble);
            ProfileEnd(PROF_JIGGLE_POSE);
            // ── Nameplate complete ──
            ProfileBegin(PROF_NAMEPLATE_COMPLETE);
            BasisRemoteNamePlateDriver.CompleteNamePlates();
            ProfileEnd(PROF_NAMEPLATE_COMPLETE);
            BasisContentSphereBillboardDriver.Complete();

            BasisJoinLeaveNotification.Simulate(TimeAsDouble);
            IndividualPlayerProvider.SimulateBeacon(DeltaTime);

            bool drawJiggle = SMModuleDebugOptions.UseGizmos && SMModuleDebugOptions.UseJiggleVisuals;
            if (drawJiggle)
            {
                JigglePhysics.ScheduleRender();
                JigglePhysics.CompleteRender(proceduralMaterial, sphereMesh, capsuleMesh);
            }

            // ── Kick off pipelined network compute: runs on worker threads through the jiggle pose
            //    completion and the render gap, joined at the top of the next Update. ──
            BasisNetworkManagement.BeginNetworkCompute(unscaledDeltaTime);

            // ── JigglePhysics complete pose ──
            ProfileBegin(PROF_JIGGLE_COMPLETE_POSE);
            JigglePhysics.CompletePose();
            ProfileEnd(PROF_JIGGLE_COMPLETE_POSE);

            // ── Shadow clone blendshapes ──
            ProfileBegin(PROF_SHADOW_CLONE);
            if (BasisSettingsDefaults.LocalHeadBlendShapes.RawValue)
            {
                BasisAvatarDriver.ApplyShadowCloneBlendShapes();
            }
            ProfileEnd(PROF_SHADOW_CLONE);

            StateOfOnRenderBefore = true;
            if (IsHeadlessClient)
            {
                OnBeforeRender();
            }

            ProfileLateUpdateFinish();
        }
        /// <summary>
        /// Callback invoked before rendering each frame (client), used to run final local player
        /// render-time simulation and to publish avatar changes.
        /// </summary>
        private void OnBeforeRender()
        {
            try { OnBeforeRenderBody(); }
            catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver.OnBeforeRender failed: {ex}", BasisDebug.LogTag.Event); }
        }

        private void OnBeforeRenderBody()
        {
            ProfileBeforeRenderInit();

            if (BasisLocalPlayer.PlayerReady)
            {
                BasisLocalPlayer.Instance.SimulateOnRender();
                Basis.Scripts.Device_Management.EyeTracking.BasisEyeTrackingManager.Simulate();
                BasisRemoteFaceManagement.Apply();
#if !BASIS_DISABLE_MICROPHONE
                BasisLocalCameraDriver.Instance.microphoneIconDriver.Simulate(DeltaTime);
#endif
            }
            StateOfOnRenderBefore = false;

            ProfileBeforeRenderFinish();
        }

        /// <summary>
        /// Application quit hook. Disposes physics and stops microphone processing.
        /// </summary>
        public void OnApplicationQuit()
        {
            JigglePhysics.Dispose();
#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.StopProcessingThread();
#endif
            BasisRemoteNamePlateDriver.Dispose();
            BasisContentSphereBillboardDriver.Dispose();
        }

        public void OnDrawGizmosSelected()
        {
            if (IsHeadlessClient)
            {
                return;
            }

            JigglePhysics.OnDrawGizmos();
        }

        // ── Editor-only profiling implementation ────────────────────
        // Partial methods with no implementation are stripped by the compiler,
        // so all Profile*() calls above become zero-cost no-ops in non-editor builds.
#if UNITY_EDITOR
        private bool _profiling;
        private System.Diagnostics.Stopwatch _lateUpdateSW;
        private System.Diagnostics.Stopwatch _beforeRenderSW;

        partial void ProfileLateUpdateInit()
        {
            _profiling = BasisEventDriverProfilerData.Enabled;
            if (_profiling)
                _lateUpdateSW = System.Diagnostics.Stopwatch.StartNew();
        }

        partial void ProfileBegin(int section)
        {
            if (!_profiling) return;
            switch (section)
            {
                case PROF_REMOTE_AUDIO_SIMULATE:
                    BasisEventDriverProfilerData.RemoteAudioDriverCount = BasisRemoteAudioDriver.DriversCount;
                    break;
                case PROF_NAMEPLATE_COMPLETE:
                    BasisEventDriverProfilerData.NamePlateJobWasIncomplete = false;
                    break;
            }
            BasisEventDriverProfilerData.Begin();
        }

        partial void ProfileBegin2()
        {
            if (_profiling)
                BasisEventDriverProfilerData.Begin2();
        }

        partial void ProfileEnd(int section)
        {
            if (!_profiling) return;
            double ms = BasisEventDriverProfilerData.End();
            switch (section)
            {
                case PROF_NETWORK_APPLY: BasisEventDriverProfilerData.NetworkApplyMs = ms; break;
                case PROF_DEVICE_MANAGEMENT: BasisEventDriverProfilerData.DeviceManagementMs = ms; break;
                case PROF_REMOTE_AUDIO_SIMULATE: BasisEventDriverProfilerData.RemoteAudioSimulateMs = ms; break;
                case PROF_NAMEPLATE_SCHEDULE: BasisEventDriverProfilerData.NamePlateScheduleMs = ms; break;
                case PROF_BTWEEN: BasisEventDriverProfilerData.BTweenMs = ms; break;
                case PROF_LOCAL_PLAYER: BasisEventDriverProfilerData.LocalPlayerMs = ms; break;
                case PROF_REMOTE_FACE_SIMULATE:
                    BasisEventDriverProfilerData.RemoteFaceSimulateMs = ms;
                    BasisEventDriverProfilerData.RemoteFace_Count = BasisRemoteFaceManagement.count;
                    break;
                case PROF_REMOTE_AUDIO_APPLY: BasisEventDriverProfilerData.RemoteAudioApplyMs = ms; break;
                case PROF_JIGGLE_SCHEDULE: BasisEventDriverProfilerData.JiggleScheduleMs = ms; break;
                case PROF_NETWORK_TRANSMIT: BasisEventDriverProfilerData.NetworkTransmitMs = ms; break;
                case PROF_JIGGLE_POSE: BasisEventDriverProfilerData.JigglePoseMs = ms; break;
                case PROF_MICROPHONE: BasisEventDriverProfilerData.MicrophoneMs = ms; break;
                case PROF_NAMEPLATE_COMPLETE: BasisEventDriverProfilerData.NamePlateCompleteMs = ms; break;
                case PROF_JIGGLE_COMPLETE_POSE: BasisEventDriverProfilerData.JiggleCompletePoseMs = ms; break;
                case PROF_SHADOW_CLONE: BasisEventDriverProfilerData.ShadowCloneMs = ms; break;
            }
        }

        partial void ProfileEnd2(int section)
        {
            if (!_profiling) return;
            double ms = BasisEventDriverProfilerData.End2();
            switch (section)
            {
                case PROF_NET_TRANSMIT_PICKUPS: BasisEventDriverProfilerData.Net_TransmitPickupsMs = ms; break;
                case PROF_NET_FIRE_BEFORE_APPLY: BasisEventDriverProfilerData.Net_FireBeforeApplyMs = ms; break;
                case PROF_NET_SIMULATE_APPLY: BasisEventDriverProfilerData.Net_SimulateNetworkApplyMs = ms; break;
                case PROF_NET_COMPLETE_REMOTE_LERP: BasisEventDriverProfilerData.Net_CompleteRemoteLerpMs = ms; break;
                case PROF_NET_MICROPHONE: BasisEventDriverProfilerData.MicrophoneMs = ms; break;
            }
        }

        partial void ProfileLateUpdateFinish()
        {
            if (!_profiling) return;
            _lateUpdateSW.Stop();
            BasisEventDriverProfilerData.LateUpdateTotalMs = _lateUpdateSW.Elapsed.TotalMilliseconds;
            BasisEventDriverProfilerData.PushHistory();
        }

        partial void ProfileBeforeRenderInit()
        {
            _profiling = BasisEventDriverProfilerData.Enabled;
            if (_profiling)
            {
                BasisEventDriverProfilerData.RemoteFaceJobWasIncomplete =
                    BasisRemoteFaceManagement.HasJob && !BasisRemoteFaceManagement.handle.IsCompleted;
                _beforeRenderSW = System.Diagnostics.Stopwatch.StartNew();
            }
        }

        partial void ProfileBeforeRenderFinish()
        {
            if (!_profiling || _beforeRenderSW == null) return;
            _beforeRenderSW.Stop();
            BasisEventDriverProfilerData.OnBeforeRenderMs = _beforeRenderSW.Elapsed.TotalMilliseconds;
        }
#endif
    }
}
