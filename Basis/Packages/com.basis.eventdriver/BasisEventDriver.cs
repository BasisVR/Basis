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
using Basis.Scripts.Platform;
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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            BasisFiniteWatchdog.Checkpoint("UpdateStart (render / physics / previous frame tail)");
            BasisFiniteWatchdog.CheckpointRemote("UpdateStart (render / physics / previous frame tail)");
#endif

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

                    // Dispatch here rather than leaving it to the remote-audio stage further
                    // down: that stage runs AFTER the local viseme Apply, so the local mouth
                    // could never pick up work queued in the same frame and was permanently a
                    // frame behind the mic. The batch task drains anything the remote stage
                    // adds later, so starting early costs the remote players nothing.
                    BasisOpenLipSyncContext.ProcessAllPending();
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
            // After the input pump, so the device poses this reads are the ones polled this frame.
            using (Prof.BodyEvidenceSample.Auto())
            {
                BasisBodyEvidenceSampler.Simulate(DeltaTime);
                BasisHeightDriver.TickObservedEvidence(DeltaTime);
            }
            using (Prof.HighPlayerCap.Auto())
            {
                BasisPerformanceMode.Simulate();
                BasisHighPlayerCapPerformanceMode.Simulate();
            }
            // Replays OS file drops collected on the window procedure. Kept on the main thread and
            // out of the WndProc itself: a drop lands in the middle of a native message pump, which
            // is no place to spawn a prop or force a menu open.
            if (!IsHeadlessClient)
            {
                using (Prof.DesktopFileDrop.Auto())
                {
                    BasisDesktopFileDrop.Dispatch();
                }
            }
            using (Prof.OnUpdateCallbacks.Auto())
            {
                InvokeEventCallbacks(OnUpdate, nameof(OnUpdate), ref _onUpdateCachedDelegate, ref _onUpdateInvocationList);
            }
            timeSinceLastUpdate += DeltaTime;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            BasisFiniteWatchdog.Checkpoint("UpdateTail (pre-animator)");
            BasisFiniteWatchdog.CheckpointRemote("UpdateTail (pre-animator)");
#endif
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
        // The cache is Action[], not the Delegate[] GetInvocationList hands back: every avatar
        // with an eye-tracking actuation subscribes, so this list is per-avatar long and runs
        // every frame — a downcast per entry per frame is pure overhead when the element type
        // is already known. The cast is paid once, on rebuild.
        private static Action _afterAvatarChangesCachedDelegate;
        private static Action[] _afterAvatarChangesInvocationList = System.Array.Empty<Action>();

        private static void InvokeAfterAvatarChangesSafely()
        {
            Action current = BasisNetworkTransmitter.AfterAvatarChanges;
            if (!ReferenceEquals(current, _afterAvatarChangesCachedDelegate))
            {
                _afterAvatarChangesCachedDelegate = current;
                if (current == null)
                {
                    _afterAvatarChangesInvocationList = System.Array.Empty<Action>();
                }
                else
                {
                    Delegate[] raw = current.GetInvocationList();
                    Action[] typed = new Action[raw.Length];
                    for (int Index = 0; Index < raw.Length; Index++)
                    {
                        typed[Index] = (Action)raw[Index];
                    }
                    _afterAvatarChangesInvocationList = typed;
                }
            }

            Action[] list = _afterAvatarChangesInvocationList;
            int count = list.Length;
            for (int Index = 0; Index < count; Index++)
            {
                try
                {
                    list[Index]();
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

            // Bone-writing systems are fenced from each other through this method, so a local-space
            // scan between them narrows a bad value to the one that just ran. Costs nothing unless
            // the watchdog is armed from Basis/Debug/Finite Watchdog.
            BasisFiniteWatchdog.Checkpoint("FrameStart (animator / physics / previous frame)");
            BasisFiniteWatchdog.CheckpointRemote("FrameStart (animator / physics / previous frame)");

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
                BasisFiniteWatchdog.CheckpointRemote("PostSyncInterpolation (synced transforms)");
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
            BasisFiniteWatchdog.CheckpointRemote("PostNetworkApply (remote pose apply)");

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
            BasisFiniteWatchdog.Checkpoint("PostDeviceManagement (tracker / device poses)");
            BasisFiniteWatchdog.CheckpointBoneControls("PostDeviceManagement (bone control pose data)");

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
                    BasisLocalPlayer localplayer = BasisLocalPlayer.Instance;
                    // Returns with the FBIK solve and the finger slerp job in flight; the remote-side
                    // stages below are their overlap window, joined in the FinishSimulate block after
                    // the remote audio simulate.
                    using (Prof.LocalPlayerSimulate.Auto())
                    {
                        localplayer.Simulate(DeltaTime);
                    }
                }
            }
            ProfileEnd(PROF_LOCAL_PLAYER);
            BasisFiniteWatchdog.Checkpoint("PostLocalPlayerSimulate (FBIK solve in flight)");

            // Blink and viseme write local blendshape weights only — no pose reads, no jobs — so
            // they run inside the solve window as overlap rather than ahead of Simulate. They stay
            // ahead of the head blendshape read scheduled further down.
            if (BasisLocalPlayer.PlayerReady)
            {
                BasisLocalPlayer localplayer = BasisLocalPlayer.Instance;
                using (Prof.FacialBlink.Auto())
                {
                    localplayer.FacialBlinkDriver.Simulate(TimeAsDouble);
                }
                using (Prof.VisemeApply.Auto())
                {
                    localplayer.LocalVisemeDriver.Apply(DeltaTime);
                }
            }

            using (Prof.RemoteBoneComplete.Auto())
            {
                BasisNetworkManagement.CompleteRemoteBoneJobSystemJobs();
            }
            BasisFiniteWatchdog.CheckpointRemote("PostRemoteBoneJobs (skeleton / hips / mouth / nameplate write)");

            // The beacon reads a remote MouthTransform's world position on the main thread, so it
            // has to sit in the one window where that hierarchy carries no in-flight job: the mouth
            // pose is final as of the join above, and the schedule cluster below re-covers remote
            // hierarchies (face eye-write, nameplate, billboard) until their completes late in the
            // frame. Downstream of those it also landed after the jiggle pose dispatch, where the
            // read stalled on the pose jobs for as long as a player stayed highlighted.
            using (Prof.SimulateBeacon.Auto())
            {
                IndividualPlayerProvider.SimulateBeacon(DeltaTime);
            }

            // Finger apply + eye schedule run inside the solve window: the head is the solve's
            // INPUT (head-pinned), and the eye driver reads only the render-latched camera statics
            // plus remote gaze-target positions (remote bones joined just above) — never the solved
            // pose. Eye bones are outside the scatter set, and the apply job writes eye LOCAL
            // rotations, so the head landing afterwards carries them correctly. The finger TAA must
            // retire first — the eye TAA schedules over the same avatar hierarchy.
            if (BasisLocalPlayer.PlayerReady)
            {
                BasisLocalPlayer localplayer = BasisLocalPlayer.Instance;
                using (Prof.LocalHandApply.Auto())
                {
                    localplayer.LocalHandDriver.Apply();
                }
                BasisFiniteWatchdog.Checkpoint("PostFingerApply");
                using (Prof.LocalEyeSimulate.Auto())
                {
                    localplayer.LocalEyeDriver.Simulate(DeltaTime);
                }
            }

            using (Prof.PickupReweld.Auto())
            {
                Basis.Scripts.Networking.Sync.BasisSyncDriver.ReweldAttachedPickups();
            }
            BasisFiniteWatchdog.Checkpoint("PostPickupReweld");

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

            // Retire the eye TAA before the IK join below scatters the same hierarchy from the
            // main thread; its jobs overlapped the reweld/schedule-cluster/remote-audio stages.
            // Stays ahead of the SteamAudio schedule further down, as before.
            if (BasisLocalPlayer.PlayerReady)
            {
                using (Prof.LocalEyeApply.Auto())
                {
                    BasisLocalPlayer.Instance.LocalEyeDriver.Apply();
                }
                BasisFiniteWatchdog.Checkpoint("PostEyeApply");
            }

            // ── Local player finish: IK solve join + post-IK tail ──
            // Everything since Simulate returned (remote bone join, finger apply, eye schedule,
            // pickup reweld, the schedule cluster, remote audio) reads nothing the FBIK solve
            // writes, so the solve ran through it on a worker. Join it now, then run the stages
            // that need the solved pose: AfterSimulateOnLate (pickup weld reads IKWorldData) and
            // the camera, which follows the AfterSimulateOnLate camera subscribers as before.
            if (BasisLocalPlayer.PlayerReady)
            {
                using (Prof.LocalPlayerFinish.Auto())
                {
                    BasisLocalPlayer localplayer = BasisLocalPlayer.Instance;
                    localplayer.FinishSimulate();
                    using (Prof.LocalCameraSimulate.Auto())
                    {
                        BasisLocalCameraDriver.Instance.Simulate(DeltaTime);
                    }
                }
                BasisFiniteWatchdog.Checkpoint("PostLocalPlayerFinish (IK join + camera)");
            }
            else
            {
                // The PIP driver's Simulate (its only steady-state fence) rides
                // AfterSimulateOnLate inside FinishSimulate. While the local player is not
                // ready its previously scheduled transform jobs would otherwise stay in
                // flight across frames, with remote pucks free to be destroyed under them.
                BasisNetworkPIPCameraDriver.CompletePending();
            }

            // ── Transmit range/distance schedule ──
            // The transmit tick's distance/reduce/avatar-cap/audio-cap/dampen chain is kicked here
            // and joined down in the AfterAvatarChanges block. Its inputs are final as of this
            // line — remote mouth positions came off the bone job join above, the head position
            // and gaze off the camera simulate immediately preceding — and nothing between here
            // and the join reads the range/LOD arrays it writes, so the chain runs through the
            // SteamAudio schedule, remote audio apply, blendshape read, authored motion,
            // constraints and the jiggle prepare instead of being fenced a few microseconds
            // after it was scheduled.
            using (Prof.TransmitSchedule.Auto())
            {
                try { BasisNetworkTransmitter.ScheduleTransmitJobs?.Invoke(); }
                catch (Exception ex) { BasisDebug.LogErrorOnce($"ScheduleTransmitJobs failed: {ex}", BasisDebug.LogTag.Event); }
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
                BasisRemoteAudioDriver.Apply(DeltaTime);
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
            BasisFiniteWatchdog.Checkpoint("PostAuthoredMotion");
            BasisFiniteWatchdog.CheckpointRemote("PostAuthoredMotion");

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
            // Post-load avatar setup runs HERE rather than wherever its bundle-load continuation
            // happened to resume. This is the frame's install-safe window (see the frame-sync
            // note below): remote bone jobs joined, constraints fenced, jiggle prepared but not
            // dispatched — so calibration's container mutation never blocks on an in-flight
            // chain. Ahead of the transmit tick so the far LOD pass below sees this frame's
            // freshly installed avatars. Loads only park for this when the wait is free; see
            // BasisAvatarSetupBudget.
            using (Prof.AvatarInstallPump.Auto())
            {
                Basis.Scripts.Avatar.BasisAvatarSetupBudget.RunQuietPoint();
            }
            BasisFiniteWatchdog.Checkpoint("PostAvatarInstall (calibration / far-LOD swap)");
            BasisFiniteWatchdog.CheckpointRemote("PostAvatarInstall (calibration / far-LOD swap)");

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
            BasisFiniteWatchdog.Checkpoint("PostConstraints (+ avatar install, transmit)");
            BasisFiniteWatchdog.CheckpointRemote("PostConstraints (+ avatar install, transmit)");

            using (Prof.FrameSync.Auto())
            {
                BasisFrameSyncRegistry.Simulate();
            }
            // Jiggle grab targets ride the same window: skeletons posed, nothing dispatched yet,
            // so the pin targets pushed here are sampled by this frame's simulate.
            Basis.Scripts.BasisSdk.Interactions.BasisJiggleGrabDriver.FrameTick();
            // Touch reporting reads the same posed bones. Returns on a count check when no content
            // has asked for jiggle events, which is the usual case.
            Basis.Scripts.BasisSdk.Interactions.BasisJiggleInteractionEvents.FrameTick();
            // Avatar visibility schedules here and is joined at the LateUpdate tail. Bounds come off
            // the avatar roots through a TransformAccessArray job, so the main thread only packs the
            // camera frusta; this is the earliest that transform job can go — it needs the same
            // free-of-in-flight-transform-jobs window as the frame sync above — and the join is the
            // latest point still ahead of every Application.onBeforeRender handler, which is where
            // mirrors render. Everything between the two overlaps the cull.
            if (Basis.Scripts.Rendering.BasisVisibilitySystem.Enabled)
            {
                using (Prof.AvatarVisibilitySchedule.Auto())
                {
                    Basis.Scripts.Rendering.BasisVisibilitySystem.Schedule(default);
                    JobHandle.ScheduleBatchedJobs();
                }
            }
            BasisFiniteWatchdog.Checkpoint("PostFrameSync (pre jiggle dispatch)");
            BasisFiniteWatchdog.CheckpointRemote("PostFrameSync (pre jiggle dispatch)");

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
            BasisFiniteWatchdog.CheckpointRemote("PostNamePlates");

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
                BasisFiniteWatchdog.Checkpoint("PostJigglePose (inline)");
                BasisFiniteWatchdog.CheckpointRemote("PostJigglePose (inline)");
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
            // Join the avatar cull scheduled back at the frame-sync window and write the changed
            // renderers. Returns immediately when nothing was scheduled, so the toggle can flip
            // mid-frame. Last stop before rendering: mirrors read these flags in onBeforeRender.
            using (Prof.AvatarVisibilityApply.Auto())
            {
                Basis.Scripts.Rendering.BasisVisibilitySystem.CompleteAndApply();
            }

            // One-shot NaN hunter for the Invalid AABB / IsFinite(distanceForSort) spam: names
            // the first non-finite camera/renderer and disarms. Armed from Basis/Debug/Finite
            // Watchdog; costs nothing while off.
            BasisFiniteWatchdog.Checkpoint("LateUpdateTail");
            BasisFiniteWatchdog.CheckpointRemote("LateUpdateTail");
            BasisFiniteWatchdog.Tick();
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

            // Outside the PlayerReady gate on purpose: this is the fence for the eye transform
            // job scheduled in LateUpdate. Skipping it while the local avatar reloads would
            // leave that job in flight across the frame boundary with remote avatars free to
            // be destroyed under it.
            try { using (Prof.RemoteFaceApply.Auto()) BasisRemoteFaceManagement.Apply(); }
            catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver remote-face apply failed: {ex}", BasisDebug.LogTag.Event); }
            BasisFiniteWatchdog.CheckpointRemote("PostRemoteFaceApply (remote eye bones)");

            if (BasisLocalPlayer.PlayerReady)
            {
                try { using (Prof.SimulateOnRender.Auto()) BasisLocalPlayer.Instance.SimulateOnRender(); }
                catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver.SimulateOnRender failed: {ex}", BasisDebug.LogTag.Event); }
                BasisFiniteWatchdog.Checkpoint("PostSimulateOnRender");


                try { using (Prof.EyeTrackingSimulate.Auto()) Basis.Scripts.Device_Management.EyeTracking.BasisEyeTrackingManager.Simulate(); }
                catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver eye-tracking simulate failed: {ex}", BasisDebug.LogTag.Event); }
#if !BASIS_DISABLE_MICROPHONE
                try { using (Prof.MicrophoneIcon.Auto()) BasisLocalCameraDriver.Instance.microphoneIconDriver.Simulate(DeltaTime); }
                catch (Exception ex) { BasisDebug.LogErrorOnce($"BasisEventDriver microphone-icon simulate failed: {ex}", BasisDebug.LogTag.Event); }
#endif
            }

            // Last thing before the render: the camera, head and eye statics have settled above, so
            // anything late-latching to them (sandboxed world scripts via BasisBeforeRenderShim, and
            // any native subscriber) gets a final pose. RaiseBeforeRender isolates its own handlers.
            using (Prof.BeforeRenderCallbacks.Auto())
            {
                BasisLocalCameraDriver.RaiseBeforeRender();
            }
            BasisFiniteWatchdog.Checkpoint("PostBeforeRenderCallbacks (last write before the render)");
            BasisFiniteWatchdog.CheckpointRemote("PostBeforeRenderCallbacks (last write before the render)");

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
