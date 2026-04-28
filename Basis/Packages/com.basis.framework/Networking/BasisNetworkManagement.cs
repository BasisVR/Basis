using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Helpers;
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
    /// Centralized network manager for Basis. Handles connection lifecycle, transmitters,
    /// simulation ticks, time synchronization, and server/client messaging.
    /// </summary>
    [DefaultExecutionOrder(15001)]
    public class BasisNetworkManagement : MonoBehaviour
    {
        #region Connection Settings

        /// <summary>
        /// Target server IP address.
        /// </summary>
        [Header("Connection")]
        public string Ip = "170.64.184.249";

        /// <summary>
        /// Target server port.
        /// </summary>
        public ushort Port = 4296;

        /// <summary>
        /// Connection password for joining the server.
        /// </summary>
        [HideInInspector]
        public string Password = "default_password";

        /// <summary>
        /// Indicates whether this instance should start as a host.
        /// </summary>
        public bool IsHostMode = false;

        /// <summary>
        /// Singleton instance of <see cref="BasisNetworkManagement"/>.
        /// </summary>
        public static BasisNetworkManagement Instance;

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
        /// Transmitter for local access, serialized for inspector reference.
        /// </summary>
        [SerializeField]
        public BasisNetworkTransmitter LocalAccessTransmitter;

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

        #region Unity Lifecycle

        private void OnEnable()
        {
            if (!BasisHelpers.CheckInstance(Instance))
            {
                enabled = false;
                return;
            }

            Instance = this;
            BasisNetworkLifeCycle.Initalize(this);
            OnIstanceCreated?.Invoke();
        }

        private async void OnDisable()
        {
            BasisNetworkConnection.OnDestroy();
            await BasisNetworkLifeCycle.Destroy(this);
        }

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
        public void Connect() => BasisNetworkConnection.Connect(Port, Ip, Password, IsHostMode);

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
        static void ParallelComputeBody(int i)
        {
            s_parallelSnapshot[i].ComputeData(s_parallelDeltaTime);
        }

        // Parameters for Euro filter (defaults; overridden at runtime by settings bindings)
        public static float MinCutoff = 0.05f;
        public static float Beta = 2;
        public static float DerivativeCutoff = 2;
        /// <summary>
        /// Simulates network computation step (state updates, bone drivers, profiler update).
        /// </summary>
        /// <param name="UnscaledDeltaTime">Delta time since last tick (unscaled).</param>
        public static void SimulateNetworkCompute(double UnscaledDeltaTime)
        {
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

            BasisRemoteNetworkDriver.BeginWrite();

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

            // Phase 2 (parallel): Audio decode + packet processing + window management +
            // interpolation + SoA writes. All per-receiver state, no shared-state conflicts.
            // Threshold is low because Opus decode dominates per-receiver cost; Parallel.For
            // overhead (~50us) pays for itself well before 16 receivers.
            if (receiverCount > 4)
            {
                s_parallelSnapshot = snapshot;
                s_parallelDeltaTime = UnscaledDeltaTime;
                Parallel.For(0, receiverCount, s_parallelOptions, s_parallelComputeBody);
            }
            else
            {
                for (int i = 0; i < receiverCount; i++)
                {
                    snapshot[i].ComputeData(UnscaledDeltaTime);
                }
            }

            // Phase 3 (main thread, lightweight): Apply AudioSource state changes.
            // Just checks a bool per receiver — only receivers that decoded audio do work.
            for (int i = 0; i < receiverCount; i++)
            {
                snapshot[i].PostCompute();
            }

            BasisRemoteNetworkDriver.Compute();
            Basis.Scripts.Networking.Receivers.BasisShoutAudioDriver.DrainAll();
            BasisNetworkProfiler.Update();

            if (HasRequested)
            {
                _timer += Time.deltaTime;
                if (_timer >= 0.1f)
                {
                    _timer = 0f;

                    BasisNetworkEvents.RequestStatFrames();
                }
            }
        }
        /// <summary>
        /// Applies networked state changes to receivers.
        /// </summary>
        public static void SimulateNetworkApply()
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
                if (poseLodEnabled && remote.PoseSkipCounter > 0)
                {
                    remote.PoseSkipCounter--;
                    BasisRemoteNetworkDriver.SetSkipMuscles(receiver.playerId, true);
#if UNITY_EDITOR
                    _skipped++;
#endif
                    continue;
                }
                if (receiver.HasOverridenDestination)
                {
                    BasisRemoteNetworkDriver.SetFilteredHipsOverride(receiver.playerId, receiver.OverridenPosition, (quaternion)receiver.OverridenRotation);
                }
#if UNITY_EDITOR
                _applied++;
#endif

                if (poseLodEnabled)
                {
                    int lod = math.clamp(remote.CurrentLodLevel, 0, 3);
                    remote.PoseSkipCounter = SMModuleDistanceBasedReductions.PoseSkipByLod[lod];
                }
                BasisRemoteNetworkDriver.SetSkipMuscles(receiver.playerId, false);
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
