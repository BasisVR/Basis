using Basis.Network.Core;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using static SerializableBasis;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Centralized static network manager for Basis. Handles connection lifecycle,
    /// transmitters, simulation ticks, time synchronization, and server/client messaging.
    /// Lifecycle (Initialize/Destroy) is driven by
    /// <see cref="Basis.Scripts.Device_Management.BasisDeviceManagement"/>.
    /// </summary>
    public static class BasisNetworkManagement
    {
        #region Connection Settings

        /// <summary>
        /// Target server IP address. Default matches the historical prefab value;
        /// callers (ServersProvider, headless mode) overwrite this before <see cref="Connect"/>.
        /// </summary>
        public static string Ip = "server1.basisvr.org";

        /// <summary>
        /// Target server port.
        /// </summary>
        public static ushort Port = 4296;

        /// <summary>
        /// Connection password for joining the server.
        /// </summary>
        public static string Password = "default_password";

        /// <summary>
        /// Indicates whether this instance should start as a host.
        /// </summary>
        public static bool IsHostMode = false;

        /// <summary>
        /// Selected networking stack id (matches a registration in
        /// <see cref="Basis.Network.Core.BasisNetworkStackRegistry"/>). Empty falls back
        /// to <see cref="Basis.Network.Core.BasisNetworkStackRegistry.DefaultId"/>.
        /// </summary>
        public static string NetworkStackId = string.Empty;

        public static string HostServerName = "Basis Server";

        public static string HostServerMotd = string.Empty;

        public static int HostPeerLimit = ushort.MaxValue;

        public static bool HostUseAuth = true;

        public static bool HostEnableConsole = true;

        public static bool HostAvatarsLocked = false;

        public static bool HostPropsLocked = false;

        public static bool HostWorldsLocked = true;

        public static bool HostThirdPersonDisabled = false;

        /// <summary>
        /// True once <see cref="BasisNetworkLifeCycle.Initialize"/> has completed.
        /// Replaces the old <c>Instance != null</c> singleton-presence check.
        /// </summary>
        public static bool IsInitialized;

        public static Action OnIstanceCreated;

        /// <summary>
        /// Indicates whether the network is currently running.
        /// </summary>
        public static bool NetworkRunning;

        /// <summary>
        /// Primary network transmitter instance.
        /// </summary>
        public static BasisNetworkTransmitter Transmitter;

        /// <summary>
        /// Reference to the local player's network peer.
        /// </summary>
        public static NetPeer LocalPlayerPeer => BasisNetworkConnection.LocalPlayerPeer;

        /// <summary>
        /// Transmitter for local access. Assigned at connect time in
        /// <see cref="BasisNetworkConnection"/>; null before the first connection.
        /// </summary>
        public static BasisNetworkTransmitter LocalAccessTransmitter;

        /// <summary>
        /// Metadata message received from the server at connect.
        /// </summary>
        public static ServerMetaDataMessage ServerMetaDataMessage = new ServerMetaDataMessage();

        /// <summary>
        /// Local player's effective permissions decoded from the server metadata.
        /// </summary>
        public static HashSet<string> LocalPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Action OnlocalPermissionsChanged;
        /// <summary>
        /// Event fired when an instance of this manager is enabled and initialized.
        /// </summary>
        public static Action OnEnableInstanceCreate;

        /// <summary>
        /// Parameters used for instantiation during network-driven scene loads.
        /// </summary>
        public static InstantiationParameters instantiationParameters;

        /// <summary>
        /// Managed thread ID of the Unity main thread.
        /// </summary>
        public static int mainThreadId;

        #endregion

        #region Thread Checks

        /// <summary>
        /// Checks whether the current code is running on the Unity main thread.
        /// </summary>
        public static bool IsMainThread()
        {
            return Thread.CurrentThread.ManagedThreadId == mainThreadId;
        }

        #endregion

        #region Connection Control

        /// <summary>
        /// Connects to the server using the configured <see cref="Ip"/>, <see cref="Port"/>, and <see cref="Password"/>.
        /// </summary>
        public static void Connect() => BasisNetworkConnection.Connect(Port, Ip, Password, IsHostMode, NetworkStackId);

        #endregion

        #region Simulation

        /// <summary>
        /// Job handle for bone simulation tasks.
        /// </summary>
        public static JobHandle BoneJobSystem;
#if UNITY_EDITOR
        private static readonly System.Diagnostics.Stopwatch _profilerStopwatch = new System.Diagnostics.Stopwatch();
#endif
        private static float _timer;
        public static bool HasRequested;

        // Shared state for Parallel.For to avoid closure allocation per frame.
        // Written on main thread before Parallel.For, read by worker threads.
        static BasisNetworkReceiver[] s_parallelSnapshot;
        static double s_parallelDeltaTime;
        static readonly Action<int> s_parallelComputeBody = ParallelComputeBody;
        // Leave headroom for Unity's job worker threads.
        static readonly ParallelOptions s_parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2)
        };
        // Indices of receivers that owe a main-thread AudioSource apply this tick. Filled
        // during compute so the apply scales with state changes, not total player count.
        static int[] s_decodedIndices = Array.Empty<int>();
        static int s_decodedCount;
        // Pipelined compute: Phase 2 runs as a background task started at the tail of the frame
        // and joined at the top of the next Update (overlaps jiggle CompletePose + the render gap).
        static Task s_computeTask;
        static BasisNetworkReceiver[] s_finishSnapshot;
        static int s_parallelCount;
        static bool s_computePending;
        static readonly Action s_runParallelCompute = RunParallelCompute;
        static void RunParallelCompute() => Parallel.For(0, s_parallelCount, s_parallelOptions, s_parallelComputeBody);
        static void ParallelComputeBody(int i)
        {
            var rec = s_parallelSnapshot[i];
            rec.ComputeData(s_parallelDeltaTime);
            if (rec.AudioReceiverModule.NeedsAudioStateApply)
            {
                s_decodedIndices[Interlocked.Increment(ref s_decodedCount) - 1] = i;
            }
        }

        /// <summary>
        /// Phase 1 (main thread) then kicks off the parallel per-receiver compute (Phase 2) on a
        /// background task. Pair with <see cref="CompleteNetworkCompute"/> at the very top of the
        /// next Update — before any receiver mutation (main-thread action drain, join/leave,
        /// avatar calibration) — so the async pass can never race those writes.
        /// </summary>
        /// <param name="UnscaledDeltaTime">Delta time since last tick (unscaled).</param>
        public static void BeginNetworkCompute(double UnscaledDeltaTime)
        {
            s_computePending = false;
            if (!NetworkRunning)
            {
                return;
            }

            BasisNetworkPlayers.PublishReceiversSnapshot();

            UnscaledDeltaTime = Math.Max(UnscaledDeltaTime, 0f);
            if (!math.isfinite(UnscaledDeltaTime))
            {
                UnscaledDeltaTime = 0;
            }
            if (BasisNetworkPlayers.ReceiverCount > BasisRemoteNetworkDriver.FixedCapacity)
            {
                BasisDebug.LogError($"Exceeded Fixed Capacity! {BasisNetworkPlayers.ReceiverCount} > {BasisRemoteNetworkDriver.FixedCapacity}", BasisDebug.LogTag.Networking);
                return;
            }

            int receiverCount = BasisNetworkPlayers.ReceiverCount;
            var snapshot = BasisNetworkPlayers.ReceiversSnapshot;

            // Phase 1 (main thread, lightweight): Unity object validation + cache.
            // Only does real work when _avatarDirty is set (avatar change/init).
            ushort largestId = 0;
            for (int i = 0; i < receiverCount; i++)
            {
                var rec = snapshot[i];
                rec.PreCompute();
                if (rec.playerId > largestId)
                    largestId = rec.playerId;
            }
            BasisNetworkPlayers.LargestNetworkReceiverID = largestId;

            BasisRemoteNetworkDriver.BeginWrite();

            // Phase 2 (parallel): per-receiver audio decode + packet processing + interpolation +
            // SoA writes. All per-receiver state, no shared-state conflicts. Kicked off here and
            // joined in CompleteNetworkCompute so it overlaps jiggle CompletePose + the render gap.
            s_decodedCount = 0;
            if (s_decodedIndices.Length < receiverCount)
            {
                s_decodedIndices = new int[receiverCount];
            }
            s_finishSnapshot = snapshot;
            s_parallelCount = receiverCount;
            s_computePending = true;

            if (receiverCount > 4)
            {
                s_parallelSnapshot = snapshot;
                s_parallelDeltaTime = UnscaledDeltaTime;
                s_computeTask = Task.Run(s_runParallelCompute);
            }
            else
            {
                for (int i = 0; i < receiverCount; i++)
                {
                    var rec = snapshot[i];
                    rec.ComputeData(UnscaledDeltaTime);
                    if (rec.AudioReceiverModule.NeedsAudioStateApply)
                    {
                        s_decodedIndices[s_decodedCount++] = i;
                    }
                }
                s_computeTask = null;
            }
        }

        /// <summary>
        /// Joins the background compute from <see cref="BeginNetworkCompute"/>, then runs the
        /// main-thread finish: Phase 3 AudioSource apply, interpolation job schedule, shout drain,
        /// and profiler update. Must run before any receiver state is mutated this frame.
        /// </summary>
        public static void CompleteNetworkCompute(float DeltaTime)
        {
            if (!s_computePending)
            {
                return;
            }
            JoinPendingCompute();

            var snapshot = s_finishSnapshot;

            // Phase 3 (main thread): apply AudioSource state only for receivers that decoded
            // audio this tick. Recorded in phase 2, so this no longer scans every player.
            for (int Index = 0; Index < s_decodedCount; Index++)
            {
                snapshot[s_decodedIndices[Index]].PostCompute();
            }

            BasisRemoteNetworkDriver.Compute();
            Basis.Scripts.Networking.Receivers.BasisShoutAudioDriver.DrainAll();
            Basis.Scripts.Networking.VoiceRecording.BasisVoiceRecording.Tick();
#if UNITY_EDITOR
            // Editor-only: counters are fed by AddToCounter, which is [Conditional("UNITY_EDITOR")].
            BasisNetworkProfiler.Update();
#endif

            if (HasRequested)
            {
                _timer += DeltaTime;
                if (_timer >= 0.1f)
                {
                    _timer = 0f;

                    BasisNetworkEvents.RequestStatFrames();
                }
            }
        }

        /// <summary>
        /// Joins any in-flight background compute without running the main-thread finish. Call at
        /// teardown before native buffers are disposed so the async pass can't use-after-free.
        /// </summary>
        public static void JoinPendingCompute()
        {
            if (s_computeTask != null)
            {
                try
                {
                    s_computeTask.Wait();
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Network compute task failed: {ex}", BasisDebug.LogTag.Networking);
                }
                s_computeTask = null;
            }
            s_computePending = false;
        }
        /// <summary>
        /// Applies networked state changes to receivers.
        /// </summary>
        public static unsafe void SimulateNetworkApply()
        {
            if (!NetworkRunning)
            {
                return;
            }

#if UNITY_EDITOR
            bool p = BasisEventDriverProfilerData.Enabled;
            System.Diagnostics.Stopwatch s = null;
            if (p)
            {
                // Check if the interpolation job (scheduled in Update) finished before we need it
                BasisEventDriverProfilerData.Net_InterpolationJobWasIncomplete = !BasisRemoteNetworkDriver.oneEuroJob.IsCompleted;
                s = System.Diagnostics.Stopwatch.StartNew();
            }
#endif
            BasisRemoteNetworkDriver.Apply(); // completes interpolation job
            BasisRemoteNetworkDriver.BeginRead();
#if UNITY_EDITOR
            if (p)
            {
                s.Stop();
                BasisEventDriverProfilerData.Net_RemoteDriverApplyMs = s.Elapsed.TotalMilliseconds;
                s.Restart();
            }
#endif

            int count = BasisNetworkPlayers.ReceiverCount;
            var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
            bool poseLodEnabled = SMModuleDistanceBasedReductions.PoseLODBias > 0f;
#if UNITY_EDITOR
            int _skipped = 0, _applied = 0;
            int _lod0 = 0, _lod1 = 0, _lod2 = 0, _lod3 = 0;
#endif

            byte* skipPtr = BasisRemoteNetworkDriver.SkipBonesPtr();
            bool endEffectorIK = BasisNetworkReceiver.EndEffectorIKEnabled;
            if (endEffectorIK) BasisRemoteNetworkDriver.ResetEffectorAnchored();

            for (int Index = 0; Index < count; Index++)
            {
                var receiver = snapshot[Index];
                var remote = receiver.RemotePlayer;

#if UNITY_EDITOR
                if (p)
                {
                    switch (math.clamp(remote.CurrentLodLevel, 0, 3))
                    {
                        case 0: _lod0++; break;
                        case 1: _lod1++; break;
                        case 2: _lod2++; break;
                        case 3: _lod3++; break;
                    }
                }
#endif

                // LOD-based frame skipping: distant players update pose less often
                if (poseLodEnabled && remote.PoseSkipCounter > 0 && !receiver.HasOverriddenDestination)
                {
                    remote.PoseSkipCounter--;
                    if (skipPtr != null && receiver.playerId < BasisRemoteNetworkDriver.FixedCapacity) skipPtr[receiver.playerId] = 1;
                    if (endEffectorIK) BasisRemoteNetworkDriver.ClearEffectorMask(receiver.playerId);
#if UNITY_EDITOR
                    _skipped++;
#endif
                    continue;
                }
                if (receiver.HasOverriddenDestination)
                {
                    BasisRemoteNetworkDriver.SetFilteredHipsOverride(receiver.playerId, receiver.OverriddenPosition, (quaternion)receiver.OverriddenRotation);
                }
#if UNITY_EDITOR
                _applied++;
#endif

                if (poseLodEnabled)
                {
                    int lod = math.clamp(remote.CurrentLodLevel, 0, 3);
                    remote.PoseSkipCounter = SMModuleDistanceBasedReductions.PoseSkipByLod[lod];
                }
                if (skipPtr != null && receiver.playerId < BasisRemoteNetworkDriver.FixedCapacity) skipPtr[receiver.playerId] = 0;
                if (endEffectorIK) receiver.WriteEffectorJobInputs();
            }
#if UNITY_EDITOR
            if (p)
            {
                BasisEventDriverProfilerData.PoseLod_Applied = _applied;
                BasisEventDriverProfilerData.PoseLod_Skipped = _skipped;
                BasisEventDriverProfilerData.PoseLod_Lod0 = _lod0;
                BasisEventDriverProfilerData.PoseLod_Lod1 = _lod1;
                BasisEventDriverProfilerData.PoseLod_Lod2 = _lod2;
                BasisEventDriverProfilerData.PoseLod_Lod3 = _lod3;
                BasisEventDriverProfilerData.PoseLod_Bias = SMModuleDistanceBasedReductions.PoseLODBias;
            }
#endif
#if UNITY_EDITOR
            if (p)
            {
                s.Stop();
                BasisEventDriverProfilerData.Net_ReceiverApplyLoopMs = s.Elapsed.TotalMilliseconds;
                BasisEventDriverProfilerData.Net_ReceiverCount = count;
                s.Restart();
            }
#endif

            BoneJobSystem = RemoteBoneJobSystem.Schedule();
#if UNITY_EDITOR
            if (p) {
                s.Stop();
                BasisEventDriverProfilerData.Net_BoneJobScheduleMs = s.Elapsed.TotalMilliseconds;
            }
            if (p)
            {
                BasisEventDriverProfilerData.Net_BoneJobWasIncomplete = !BoneJobSystem.IsCompleted;
                s = System.Diagnostics.Stopwatch.StartNew();
            }
#endif
        }
        public static void CompleteRemoteBoneJobSystemJobs()
        {
            if (!NetworkRunning)
            {
                return;
            }
#if UNITY_EDITOR
            bool p = BasisEventDriverProfilerData.Enabled;
            if (p) _profilerStopwatch.Restart();
#endif
            RemoteBoneJobSystem.Complete(BoneJobSystem);
#if UNITY_EDITOR
            if (p) {
                _profilerStopwatch.Stop();
                BasisEventDriverProfilerData.Net_BoneJobCompleteMs = _profilerStopwatch.Elapsed.TotalMilliseconds;
            }
#endif
        }

        #endregion

        #region Time Sync

        /// <summary>
        /// Gets the server time offset in seconds relative to local system time.
        /// </summary>
        public static int GetServerTimeOffsetSeconds()
        {
            var serverTime = RemoteUtcTime();
            return DateTimeToSeconds(serverTime);
        }

        /// <summary>
        /// Gets the server time in milliseconds as an integer offset.
        /// </summary>
        public static int GetServerTimeInMilliseconds()
        {
            DateTime serverTime = RemoteUtcTime();
            int secondsAhead = DateTimeToSeconds(serverTime);
            return secondsAhead;
        }

        #endregion

        #region Messaging

        /// <summary>
        /// Sends a payload intended for server-only handling.
        /// </summary>
        /// <param name="payload">The raw message data.</param>
        /// <param name="mode">The delivery method (reliable, unreliable, etc.).</param>
        public static void SendServerSideMessage(byte[] payload, DeliveryMethod mode)
        {
            if (payload == null || payload.Length == 0)
            {
                BasisDebug.LogWarning("Attempted to send empty server-side message.");
                return;
            }

            var peer = LocalPlayerPeer;
            if (peer == null)
            {
                BasisDebug.LogError("Local NetPeer was null!", BasisDebug.LogTag.Networking);
                return;
            }

            peer.Send(payload, BasisNetworkCommons.ServerBoundChannel, mode);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.SceneData, payload.Length);
        }

        /// <summary>
        /// Sends a database item to the server for storage.
        /// </summary>
        /// <param name="DatabaseID">The ID of the database entry.</param>
        /// <param name="jsonPayload">Key/value data for the item.</param>
        public static void SendServerSideDatabaseItem(string DatabaseID, ConcurrentDictionary<string, object> jsonPayload)
        {
            var peer = LocalPlayerPeer;
            if (peer == null)
            {
                BasisDebug.LogError("Local NetPeer was null!", BasisDebug.LogTag.Networking);
                return;
            }

            DatabasePrimativeMessage databasePrimativeMessage = new DatabasePrimativeMessage
            {
                Name = DatabaseID,
                jsonPayload = jsonPayload
            };

            NetDataWriter netDataWriter = new NetDataWriter();
            databasePrimativeMessage.Serialize(netDataWriter);
            BasisNetworkConnection.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.RequestStoreDatabaseChannel, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Requests a database item from the server by ID.
        /// </summary>
        /// <param name="DatabaseID">The ID of the requested database entry.</param>
        public static void RequestServerSideDatabaseItem(string DatabaseID)
        {
            var peer = LocalPlayerPeer;
            if (peer == null)
            {
                BasisDebug.LogError("Local NetPeer was null!", BasisDebug.LogTag.Networking);
                return;
            }

            DataBaseRequest DataBaseRequest = new DataBaseRequest
            {
                DatabaseID = DatabaseID
            };

            NetDataWriter netDataWriter = new NetDataWriter();
            DataBaseRequest.Serialize(netDataWriter);
            BasisNetworkConnection.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.RequestStoreDatabaseChannel, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Event fired when a server-side database item is returned.
        /// </summary>
        public static Action<DatabasePrimativeMessage> OnRequestServerSideDatabaseItem;

        #endregion

        #region Peer Helpers

        /// <summary>
        /// Remote time delta in ticks from the peer.
        /// </summary>
        public static long RemoteTimeDelta() => LocalPlayerPeer?.RemoteTimeDelta ?? 0;

        /// <summary>
        /// Remote UTC time reported by the server peer.
        /// </summary>
        public static DateTime RemoteUtcTime() => LocalPlayerPeer?.RemoteUtcTime ?? DateTime.UtcNow;

        /// <summary>
        /// Time since last packet received from peer.
        /// </summary>
        public static float TimeSinceLastPacket() => LocalPlayerPeer?.TimeSinceLastPacket ?? float.MaxValue;

        /// <summary>
        /// Network statistics snapshot from the peer.
        /// </summary>
        // public static NetStatistics Statistics() => LocalPlayerPeer?.Statistics;

        /// <summary>
        /// Converts a DateTime to a relative number of seconds from now.
        /// </summary>
        public static int DateTimeToSeconds(DateTime dateTime)
        {
            var timeSpan = dateTime - DateTime.Now;
            return (int)timeSpan.TotalSeconds;
        }

        #endregion
    }
}
