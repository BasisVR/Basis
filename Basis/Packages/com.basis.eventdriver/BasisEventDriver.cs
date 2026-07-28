using Basis.BTween;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Constraints;
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
using Unity.Jobs;
using UnityEngine;
using static Basis.EventDriver.BasisEventDriverProfileSections;
using Prof = Basis.EventDriver.BasisEventDriverMarkers;

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
        public static event Action OnUpdate;
        public static event Action OnLateUpdate;

        private static Action _onUpdateCachedDelegate;
        private static Delegate[] _onUpdateInvocationList = System.Array.Empty<Delegate>();
        private static Action _onLateUpdateCachedDelegate;
        private static Delegate[] _onLateUpdateInvocationList = System.Array.Empty<Delegate>();

        private static void ResetEventCallbacks()
        {
            OnUpdate = null;
            OnLateUpdate = null;
            _onUpdateCachedDelegate = null;
            _onUpdateInvocationList = System.Array.Empty<Delegate>();
            _onLateUpdateCachedDelegate = null;
            _onLateUpdateInvocationList = System.Array.Empty<Delegate>();
            // Same teardown semantics as the events above: nothing survives the driver going away.
            // This is also what makes the registry's cross-instance cap safe to enforce.
            BasisFrameSyncRegistry.Clear();
        }

        private static void InvokeEventCallbacks(Action callbacks, string callbackName, ref Action cachedDelegate, ref Delegate[] cachedInvocationList)
        {
            if (!ReferenceEquals(callbacks, cachedDelegate))
            {
                cachedDelegate = callbacks;
                cachedInvocationList = callbacks == null ? System.Array.Empty<Delegate>() : callbacks.GetInvocationList();
            }

            Delegate[] invocationList = cachedInvocationList;
            int invocationCount = invocationList.Length;
            for (int index = 0; index < invocationCount; index++)
            {
                Delegate callback = invocationList[index];
                try
                {
                    ((Action)callback).Invoke();
                }
                catch (Exception ex)
                {
                    BasisDebug.LogErrorOnce(
                        $"BasisEventDriver.{callbackName} callback "
                        + $"{callback.Method.DeclaringType?.FullName}.{callback.Method.Name} "
                        + $"failed: {ex}",
                        BasisDebug.LogTag.Event);
                }
            }
        }

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
            try
            {
                BasisOpenLipSyncDriver.Shutdown();
                Basis.Scripts.Networking.Sync.BasisSyncDriver.OnDestroy();
                Application.onBeforeRender -= OnBeforeRender;
                RemoteBoneJobSystem.Dispose();
                BasisAuthoredMotionSystem.Dispose();
                BasisConstraintSystem.Dispose();
                BasisAvatarBufferPool.Deinitialize();
            }
            finally
            {
                if (ReferenceEquals(Instance, this))
                {
                    Instance = null;
                    ResetEventCallbacks();
                }
            }
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
            using var updateScope = Prof.Update.Auto();

            DeltaTime = Time.deltaTime;
            unscaledDeltaTime = Time.unscaledDeltaTime;
            realtimeSinceStartupAsDouble = Time.realtimeSinceStartupAsDouble;
            TimeAsDouble = Time.timeAsDouble;

#if !UNITY_SERVER && !BASIS_DISABLE_MICROPHONE
            // Capture pump first thing in the frame: the Unity mic-read APIs are
            // main-thread-only, but everything after them (downmix, filtering, Opus
            // encode, network send) runs on the mic processing thread — kicking it here
            // gives that thread the whole frame instead of waking it mid-LateUpdate.
            using (Prof.MicrophoneUpdate.Auto())
            {
                BasisLocalMicrophoneDriver.PumpCapture();
            }
#endif

            // Join the network compute kicked off at the tail of the previous LateUpdate, before
            // the main-thread action drain and join/leave lifecycle below mutate any receiver.
            using (Prof.NetworkCompleteCompute.Auto())
            {
                BasisNetworkManagement.CompleteNetworkCompute(DeltaTime);
            }

            using (Prof.FrameClockTick.Auto())
            {
                BasisFrameClock.Tick(unscaledDeltaTime);
            }

            if (BasisLocalPlayer.PlayerReady)
            {
                using (Prof.VisemeSimulate.Auto())
                {
                    BasisLocalPlayer.Instance.LocalVisemeDriver.Simulate(DeltaTime);
                }
            }
            // Drain everything that arrived from worker threads
            using (Prof.MainThreadActions.Auto())
            {
                while (BasisDeviceManagement.mainThreadActions.TryDequeue(out System.Action action))
                {
                    try
                    {
                        action.Invoke();
                    }
                    catch (Exception ex)
                    {
                        BasisDebug.LogError(
                            $"MainThread action failed: {ex}",
                            BasisDebug.LogTag.Event
                        );
                    }
                }
            }
            // Player join/leave work is budgeted separately so a mass disconnect
            // (hundreds of players at once) can't chain N synchronous GameObject.Destroy
            // calls in a single frame and stall the renderer.
            using (Prof.LifecycleQueue.Auto())
            {
                BasisNetworkHandleRemoval.ProcessLifecycleQueue(BasisNetworkHandleRemoval.LifecycleBudgetPerFrame);
            }
            using (Prof.ConnectionWatchdog.Auto())
            {
                BasisNetworkConnectionWatchdog.Tick(unscaledDeltaTime);
            }
            if (!IsHeadlessClient)
            {
                using (Prof.InputSystemUpdate.Auto())
                {
                    BasisInputSystemPump.Pump(realtimeSinceStartupAsDouble);
                }
            }

            using (Prof.OscAcquisition.Auto())
            {
                OSCAcquisitionServer.Simulate();
            }
            using (Prof.PerformanceLimits.Auto())
            {
                SMModuleAvatarPerformanceLimits.Simulate();
            }
            using (Prof.DebugOptions.Auto())
            {
                SMModuleDebugOptions.Simulate();
            }
            using (Prof.GazeFoveationAuto.Auto())
            {
                Basis.Scripts.Device_Management.EyeTracking.BasisGazeFoveationAutoDriver.Simulate();
            }
            using (Prof.HighPlayerCap.Auto())
            {
                BasisPerformanceMode.Simulate();
                BasisHighPlayerCapPerformanceMode.Simulate();
            }
            using (Prof.OnUpdateCallbacks.Auto())
            {
                InvokeEventCallbacks(OnUpdate, nameof(OnUpdate), ref _onUpdateCachedDelegate, ref _onUpdateInvocationList);
            }
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
            using var fixedUpdateScope = Prof.FixedUpdate.Auto();

            fixedDeltaTime = Time.fixedDeltaTime;
            fixedTimeAsDouble = Time.fixedTimeAsDouble;
            if (BasisLocalPlayer.PlayerReady)
            {
                using (Prof.SceneFactorySimulate.Auto())
                {
                    BasisSceneFactory.Simulate(fixedDeltaTime);
                }
            }
        }
        // AfterAvatarChanges carries BOTH avatar-content hooks (e.g. HVR eye reads, which run
        // arbitrary per-avatar code) AND the local avatar transmit (TransmissionResults.Simulate
        // → Compress → every outgoing avatar/face packet). A bare multicast Invoke lets one
        // throwing content hook abort every later subscriber — which silently killed all avatar
        // transmission. Invoke each subscriber under its own catch instead; the invocation list
        // is cached and only rebuilt when the delegate instance changes.
        private static Action _afterAvatarChangesCachedDelegate;
        private static Delegate[] _afterAvatarChangesInvocationList = System.Array.Empty<Delegate>();

        private static void InvokeAfterAvatarChangesSafely()
        {
            Action current = BasisNetworkTransmitter.AfterAvatarChanges;
            if (!ReferenceEquals(current, _afterAvatarChangesCachedDelegate))
            {
                _afterAvatarChangesCachedDelegate = current;
                _afterAvatarChangesInvocationList = current == null ? System.Array.Empty<Delegate>() : current.GetInvocationList();
            }

            Delegate[] list = _afterAvatarChangesInvocationList;
            int count = list.Length;
            for (int Index = 0; Index < count; Index++)
            {
                try
                {
                    ((Action)list[Index])();
                }
                catch (Exception ex)
                {
                    BasisDebug.LogErrorOnce($"AfterAvatarChanges subscriber {list[Index].Method?.DeclaringType?.Name}.{list[Index].Method?.Name} failed: {ex}", BasisDebug.LogTag.Event);
                }
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
            using var lateUpdateScope = Prof.LateUpdate.Auto();

            ProfileLateUpdateInit();

            if (StateOfOnRenderBefore)
            {
                OnBeforeRender();
            }

            // Comms eye/Vixxy/activity actuation is pumped in front of the network-apply barrier so
            // its main-thread cost overlaps the in-flight BasisRemoteNetworkDriver interpolation
            // jobs (UpdateAllAvatarsJob + InterpolateBoneRotationsJob), which
            // SimulateNetworkApply's Apply() completes below. Vixxy actuates blendshapes/materials,
            // so it must stay ahead of BasisBlendShapeDriver and the render. VariableNetworking is
            // split off to a later barrier (see the AuthoredMotion schedule/complete below) — it
            // produces no visible write this frame, so it hides behind a different job. Safe: the
            // comms batch reads only its own networked variable state, never the remote pose/bone
            // output produced by SimulateNetworkApply.
            // ── Sync interpolation schedule ──
            // Scheduled here, at the top of LateUpdate: every Update-phase network dispatch is
            // done, so the interp targets are as fresh as they will get this frame. The complete
            // stays down in the NetworkApply block, so vehicles (main-thread custom apply) land
            // at exactly the same point relative to the seat/override chain — the jobs simply
            // overlap the eye/comms/transmit work above instead of a zero window. Mid-window
            // register/unregister only touches managed lists (layout rebuilds inside the next
            // schedule), and Destroy is end-of-frame deferred, so in-flight jobs stay safe.
            using (Prof.SyncScheduleRemote.Auto())
            {
                Basis.Scripts.Networking.Sync.BasisSyncDriver.ScheduleRemote(DeltaTime);
            }

            using (Prof.EyeTrackingSimulate.Auto())
            {
                Basis.Scripts.Device_Management.EyeTracking.BasisEyeTrackingManager.Simulate();
            }

            // Kick the SteamVR input update onto its worker thread. Placed after the eye block
            // (the last main-thread reader of action state) so it overlaps the comms/network-apply
            // work below; the DeviceManagement.Simulate block joins it before the local player
            // polls input.
            if (BasisDeviceManagement.HasEvents)
            {
                using (Prof.DeviceManagementKick.Auto())
                {
                    BasisDeviceManagement.Instance.SimulateKick();
                }
            }

            using (Prof.CommsActuators.Auto())
            {
                HVRCommsUpdateDriver.SimulateActuators();
            }

            using (Prof.NetworkApply.Auto())
            {
                RemoteBoneJobSystem.ScheduleGathers();
                ProfileBegin(PROF_NETWORK_APPLY);
                ProfileBegin2();
                using (Prof.NetFireBeforeApply.Auto())
                {
                    BasisLocalPlayer.FireJustBeforeNetworkApply();
                }
                ProfileEnd2(PROF_NET_FIRE_BEFORE_APPLY);
                ProfileBegin2();
                using (Prof.SyncTransmitOwned.Auto())
                {
                    Basis.Scripts.Networking.Sync.BasisSyncDriver.TransmitOwned(TimeAsDouble);
                }
                ProfileEnd2(PROF_NET_TRANSMIT_PICKUPS);
                ProfileBegin2();
                using (Prof.SyncCompleteRemote.Auto())
                {
                    Basis.Scripts.Networking.Sync.BasisSyncDriver.CompleteRemote();
                }
                ProfileEnd2(PROF_NET_COMPLETE_REMOTE_LERP);
                using (Prof.NetFireAfterRemoteSync.Auto())
                {
                    BasisLocalPlayer.FireAfterRemoteSyncInterpolated();
                }
                ProfileBegin2();
                using (Prof.NetSimulateApply.Auto())
                {
                    BasisNetworkManagement.SimulateNetworkApply();
                }
                ProfileEnd2(PROF_NET_SIMULATE_APPLY);
                ProfileEnd(PROF_NETWORK_APPLY);
            }

            // ── Device management ──
            ProfileBegin(PROF_DEVICE_MANAGEMENT);
            if (BasisDeviceManagement.HasEvents)
            {
                using (Prof.DeviceManagement.Auto())
                {
                    BasisDeviceManagement.Instance.Simulate();
                }
            }
            ProfileEnd(PROF_DEVICE_MANAGEMENT);

            // ── BTween ──
            ProfileBegin(PROF_BTWEEN);
            using (Prof.BTween.Auto())
            {
                BasisTweenManager.Simulate(realtimeSinceStartupAsDouble);
            }
            ProfileEnd(PROF_BTWEEN);

            // ── Local player ──
            ProfileBegin(PROF_LOCAL_PLAYER);
            if (BasisLocalPlayer.PlayerReady)
            {
                using (Prof.LocalPlayer.Auto())
                {
                    BasisLocalCameraDriver LocalCameraDriver = BasisLocalCameraDriver.Instance;
                    BasisLocalPlayer localplayer = BasisLocalPlayer.Instance;
                    using (Prof.FacialBlink.Auto())
                    {
                        localplayer.FacialBlinkDriver.Simulate(TimeAsDouble);
                    }
                    using (Prof.VisemeApply.Auto())
                    {
                        localplayer.LocalVisemeDriver.Apply();
                    }
                    using (Prof.LocalPlayerSimulate.Auto())
                    {
                        localplayer.Simulate(DeltaTime);
                    }
                    // Complete the finger slerp job (TransformAccessArray write) before touching the
                    // camera transform, so Simulate never overlaps jobified transform access.
                    using (Prof.LocalHandApply.Auto())
                    {
                        localplayer.LocalHandDriver.Apply();
                    }
                    using (Prof.LocalCameraSimulate.Auto())
                    {
                        LocalCameraDriver.Simulate(DeltaTime);
                    }
                    using (Prof.LocalEyeSimulate.Auto())
                    {
                        localplayer.LocalEyeDriver.Simulate(DeltaTime);
                    }
                }
            }
            ProfileEnd(PROF_LOCAL_PLAYER);

            using (Prof.RemoteBoneComplete.Auto())
            {
                BasisNetworkManagement.CompleteRemoteBoneJobSystemJobs();
            }

            // ── Schedule cluster: nameplate pulse, billboard, remote face ──
            // Grouped directly after the remote bone complete (the face eye-write TAA touches
            // remote hierarchies, so the remote bone jobs must be joined first) and kicked as
            // one batch, so the jobs run through the remote audio / local eye / steam audio
            // stages below instead of waiting for the first implicit flush.
            ProfileBegin(PROF_NAMEPLATE_SCHEDULE);
            using (Prof.NamePlateSchedule.Auto())
            {
                BasisRemoteNamePlateDriver.ScheduleSimulate(TimeAsDouble);
            }
            ProfileEnd(PROF_NAMEPLATE_SCHEDULE);
            using (Prof.ContentSphereSchedule.Auto())
            {
                BasisContentSphereBillboardDriver.ScheduleSimulate();
            }

            // ── Remote face simulate (job schedule) ──
            ProfileBegin(PROF_REMOTE_FACE_SIMULATE);
            using (Prof.RemoteFaceSimulate.Auto())
            {
                BasisRemoteFaceManagement.Simulate(TimeAsDouble, DeltaTime);
            }
            ProfileEnd(PROF_REMOTE_FACE_SIMULATE);

            JobHandle.ScheduleBatchedJobs();

            // ── Remote audio simulate ──
            ProfileBegin(PROF_REMOTE_AUDIO_SIMULATE);
            using (Prof.RemoteAudioSimulate.Auto())
            {
                BasisRemoteAudioDriver.Simulate(DeltaTime);
            }
            ProfileEnd(PROF_REMOTE_AUDIO_SIMULATE);

            // Complete the eye apply here rather than right after its schedule in LocalEyeDriver.Simulate,
            // so the eye compute/apply jobs overlap the remote bone complete, the schedule cluster, and
            // the remote audio simulate above.
            // Still ahead of JigglePhysics.ScheduleSimulate, so the transform write has no jiggle job to stall on.
            if (BasisLocalPlayer.PlayerReady)
            {
                using (Prof.LocalEyeApply.Auto())
                {
                    BasisLocalPlayer.Instance.LocalEyeDriver.Apply();
                }
            }

#if STEAMAUDIO_ENABLED
            // Stays after the local eye apply: the listener/source gather TAA can share the local
            // player hierarchy with the eye-write job, and the two have never been in flight
            // together — the gather also wants the final camera-side poses for this frame.
            using (Prof.SteamAudioSchedule.Auto())
            {
                SteamAudioManager.Schedule();
            }
            JobHandle.ScheduleBatchedJobs();
#endif

            // ── Remote audio apply ──
            ProfileBegin(PROF_REMOTE_AUDIO_APPLY);
            using (Prof.RemoteAudioApply.Auto())
            {
                BasisRemoteAudioDriver.Apply();
            }
            ProfileEnd(PROF_REMOTE_AUDIO_APPLY);

            try
            {
                using (Prof.BuiltInAddresses.Auto())
                {
                    HVRBasisBuiltInAddresses.Simulate();
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogErrorOnce($"HVRBasisBuiltInAddresses.Simulate failed: {ex}", BasisDebug.LogTag.Event);
            }

            // ── BlendShape apply ──
            if (BasisSettingsDefaults.LocalHeadBlendShapes.RawValue)
            {
                using (Prof.ReadBlendShapes.Auto())
                {
                    BasisAvatarDriver.ScheduleReadBlendShapes();
                }
            }

            // ── Authored motion: write non-humanoid authored bones before jiggle samples them ──
            // Split schedule/complete (was a synchronous Complete(Schedule())) so the authored
            // transform-write job overlaps a slice of independent main-thread work instead of
            // stalling on it. VariableNetworking is that filler: it touches none of the authored
            // transforms, produces no avatar-visible write this frame, and stays ahead of the
            // AfterAvatarChanges eye read below — so its cost hides behind the job's wall-clock.
            JobHandle authoredMotionJob;
            using (Prof.AuthoredMotionSchedule.Auto())
            {
                authoredMotionJob = BasisAuthoredMotionSystem.Schedule();
            }
            using (Prof.VariableNetworking.Auto())
            {
                HVRCommsUpdateDriver.SimulateVariableNetworking();
            }
            using (Prof.AuthoredMotionComplete.Auto())
            {
                BasisAuthoredMotionSystem.Complete(authoredMotionJob);
            }

            // ── Constraints: resolve the BasisConstraint* components ──
            // Sits after authored motion (so a constraint may source an authored bone) and ahead of
            // the jiggle schedule below (so jiggle samples the constrained pose, not the stale one).
            //
            // Scheduled here but completed further down, on the far side of jiggle's preparation
            // AND dispatch. The preparation is main-thread work — parameter pushes, collider and
            // tree commits — that reads no bone pose on a steady frame, and the dispatch hands
            // this handle to the jiggle chain as a job dependency (its transform reads wait on the
            // constraint write in-graph), so the solve runs through both stages instead of the
            // main thread waiting ahead of them. The fence stays ahead of AfterAvatarChanges,
            // whose transmit reads local bone rotations inline on the main thread.
            JobHandle constraintJob;
            using (Prof.ConstraintSchedule.Auto())
            {
                constraintJob = BasisConstraintSystem.Schedule();
            }

            // ── JigglePhysics schedule ──
            bool jiggleReady = false;
            ProfileBegin(PROF_JIGGLE_SCHEDULE);
            using (Prof.JiggleSchedule.Auto())
            {
                using (Prof.JiggleCullCameras.Auto())
                {
                    JiggleCullCameras.Clear();
                    var jiggleCullCamera = BasisLocalCameraDriver.CameraInstance;
                    if (jiggleCullCamera != null)
                    {
                        JiggleCullCameras.Add(jiggleCullCamera);
                    }
                    BasisCullingCameraRegistry.CollectInto(JiggleCullCameras);
                    JigglePhysics.SetCullingCameras(JiggleCullCameras);
                }

                fixedDeltaTime = Time.fixedDeltaTime;

                // A pending tree rebuild measures rest lengths off live bone positions, which the solve
                // is in the middle of writing — so on those frames the overlap is given up rather than
                // letting the rebuild read half-solved poses. Rebuilds are rare; steady frames are not.
                if (JigglePhysics.WillRebuildTrees)
                {
                    using (Prof.ConstraintComplete.Auto())
                    {
                        BasisConstraintSystem.Complete(constraintJob);
                    }
                    constraintJob = default;
                }

                using (Prof.JigglePrepare.Auto())
                {
                    jiggleReady = JigglePhysics.PrepareSimulate(TimeAsDouble, fixedDeltaTime);
                }
                // On rebuild frames the constraint solve was already completed above and the
                // handle zeroed — skip the empty second fence instead of logging a no-op marker.
                if (!constraintJob.Equals(default(JobHandle)))
                {
                    using (Prof.ConstraintComplete.Auto())
                    {
                        BasisConstraintSystem.Complete(constraintJob);
                    }
                }
            }
            ProfileEnd(PROF_JIGGLE_SCHEDULE);

            // ── Network transmit (reads bone results via GetOutGoingMouth) ──
            // Ahead of the jiggle dispatch: the transmit reads local bones inline on the main
            // thread, which would force a sync on freshly-dispatched jiggle transform jobs over
            // the same avatar hierarchies. Constraints are fenced above, so this window is
            // job-free for those bones.
            ProfileBegin(PROF_NETWORK_TRANSMIT);
            using (Prof.AfterAvatarChanges.Auto())
            {
                InvokeAfterAvatarChangesSafely();
            }
            ProfileEnd(PROF_NETWORK_TRANSMIT);

            // ── Frame sync entries (Cilbox transform / blendshape sync shims) ──
            // Deliberately pinned between the transmit above and the jiggle dispatch below: this
            // is the one window in the frame where an arbitrary Transform — including an avatar
            // bone a sandboxed script picked — is free of in-flight jobs on the main thread. IK,
            // the local player sim and authored motion have posed; the constraint solve is fenced
            // inside the jiggle schedule block; the remote bone jobs were joined further up; and
            // jiggle has prepared but not yet dispatched. A read here cannot stall on a
            // TransformAccessArray job, and a write here is picked up by JigglePhysics this frame
            // instead of next. Every entry runs under its own catch inside the registry.
            using (Prof.FrameSync.Auto())
            {
                BasisFrameSyncRegistry.Simulate();
            }

            if (jiggleReady)
            {
                using (Prof.JiggleDispatch.Auto())
                {
                    JigglePhysics.DispatchSimulate(constraintJob);
                }
            }

            // ── JigglePhysics pose ──
            ProfileBegin(PROF_JIGGLE_POSE);
            using (Prof.JiggleSchedulePose.Auto())
            {
                JigglePhysics.SchedulePose(TimeAsDouble);
            }
            ProfileEnd(PROF_JIGGLE_POSE);
            // ── Nameplate complete ──
            ProfileBegin(PROF_NAMEPLATE_COMPLETE);
            using (Prof.NamePlateComplete.Auto())
            {
                BasisRemoteNamePlateDriver.CompleteNamePlates(TimeAsDouble);
            }
            ProfileEnd(PROF_NAMEPLATE_COMPLETE);

#if STEAMAUDIO_ENABLED
            // ── SteamAudio apply ──
            // After the jiggle pose schedule, not in OnBeforeRender: its reap/push/snapshot
            // main-thread cost overlaps the in-flight pose jobs. Nothing it reads is fresher
            // by render time — the gather ran at SteamAudioSchedule, and the listener has
            // always been read ahead of SimulateOnRender's render-time pose work.
            using (Prof.SteamAudioApply.Auto())
            {
                SteamAudioManager.Apply();
            }
#endif

            using (Prof.ContentSphereComplete.Auto())
            {
                BasisContentSphereBillboardDriver.Complete();
            }

            using (Prof.JoinLeaveNotification.Auto())
            {
                BasisJoinLeaveNotification.Simulate(TimeAsDouble);
            }
            using (Prof.SimulateBeacon.Auto())
            {
                IndividualPlayerProvider.SimulateBeacon(DeltaTime);
            }

            bool drawJiggle = SMModuleDebugOptions.UseGizmos && SMModuleDebugOptions.UseJiggleVisuals;
            if (drawJiggle)
            {
                using (Prof.JiggleRender.Auto())
                {
                    JigglePhysics.ScheduleRender();
                    JigglePhysics.CompleteRender(proceduralMaterial, sphereMesh, capsuleMesh);
                }
            }

            // ── Kick off pipelined network compute: runs on worker threads through the jiggle pose
            //    completion and the render gap, joined at the top of the next Update. ──
            using (Prof.NetworkBeginCompute.Auto())
            {
                BasisNetworkManagement.BeginNetworkCompute(unscaledDeltaTime);
            }

            // ── JigglePhysics complete pose ──
            // Deferred to a player-loop step just ahead of the particle update when possible, so the
            // rest of the frame overlaps the pose jobs instead of the main thread waiting on them
            // here. Nothing between this point and there reads a jiggled bone; rendering is the
            // consumer. Falls back to completing inline when the loop step could not be installed.
            ProfileBegin(PROF_JIGGLE_COMPLETE_POSE);
            if (BasisLateJiggleCompletion.Enabled)
            {
                BasisLateJiggleCompletion.MarkPosePending();
            }
            else
            {
                using (Prof.JiggleCompletePose.Auto())
                {
                    JigglePhysics.CompletePose();
                }
            }
            ProfileEnd(PROF_JIGGLE_COMPLETE_POSE);

            // ── Shadow clone blendshapes ──
            ProfileBegin(PROF_SHADOW_CLONE);
            if (BasisSettingsDefaults.LocalHeadBlendShapes.RawValue)
            {
                using (Prof.ShadowClone.Auto())
                {
                    BasisAvatarDriver.ApplyShadowCloneBlendShapes();
                }
            }
            ProfileEnd(PROF_SHADOW_CLONE);

            StateOfOnRenderBefore = true;
            if (IsHeadlessClient)
            {
                OnBeforeRender();
            }

            using (Prof.OnLateUpdateCallbacks.Auto())
            {
                InvokeEventCallbacks(OnLateUpdate, nameof(OnLateUpdate), ref _onLateUpdateCachedDelegate, ref _onLateUpdateInvocationList);
            }

            // Batched debug-gizmo submission (instanced spheres, the shared line mesh, and the
            // nearest-K label ranking). Sits at the very end of LateUpdate so every producer this
            // frame — the Update-tick consumers, IK/bone drivers, OnLateUpdate subscribers — is
            // captured before the draw; early-outs to nothing while no gizmos exist. The tracker
            // marker balls tick immediately ahead of the submission so they carry this frame's
            // latched device poses.
            using (Prof.GizmoRender.Auto())
            {
                BasisTrackerMarkerGizmos.Tick();
                BasisGizmoManager.Render(BasisLocalCameraDriver.Position);
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
            using var beforeRenderScope = Prof.BeforeRender.Auto();

            ProfileBeforeRenderInit();

            // Publish the nameplate work scheduled back in CompleteNamePlates — the plate
            // matrix build (and any CPU vertex transforms) completed on workers through the
            // tail of LateUpdate, and the per-plate GPU buffer uploads land here, off the
            // NamePlate.Complete marker.
            using (Prof.NamePlateFinish.Auto())
            {
                BasisGlobalNamePlateRenderer.FinishFrame();
            }

            if (BasisLocalPlayer.PlayerReady)
            {
                try { using (Prof.SimulateOnRender.Auto()) BasisLocalPlayer.Instance.SimulateOnRender(); }
                catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver.SimulateOnRender failed: {ex}", BasisDebug.LogTag.Event); }


                try { using (Prof.EyeTrackingSimulate.Auto()) Basis.Scripts.Device_Management.EyeTracking.BasisEyeTrackingManager.Simulate(); }
                catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver eye-tracking simulate failed: {ex}", BasisDebug.LogTag.Event); }

                try { using (Prof.RemoteFaceApply.Auto()) BasisRemoteFaceManagement.Apply(); }
                catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver remote-face apply failed: {ex}", BasisDebug.LogTag.Event); }
#if !BASIS_DISABLE_MICROPHONE
                try { using (Prof.MicrophoneIcon.Auto()) BasisLocalCameraDriver.Instance.microphoneIconDriver.Simulate(DeltaTime); }
                catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver microphone-icon simulate failed: {ex}", BasisDebug.LogTag.Event); }
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
            try
            {
                JigglePhysics.Dispose();
#if !BASIS_DISABLE_MICROPHONE
                BasisLocalMicrophoneDriver.StopProcessingThread();
#endif
                BasisRemoteNamePlateDriver.Dispose();
                BasisContentSphereBillboardDriver.Dispose();
            }
            finally
            {
                if (ReferenceEquals(Instance, this)) Instance = null;
                ResetEventCallbacks();
            }
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
