using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Concurrent;
using System.Threading;
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
        private static float _timer;
        public static bool HasRequested;

        // Parameters for Euro filter
       // [Header(" Lower values → smoother output, more latency, Higher values → snappier output, more noise passes through")]
        public static float MinCutoff = 0.05f;
      //  [Header("This is the adaptivity knob. It controls how much the filter reacts to speed. Beta multiplies the filtered derivative magnitude:")]
        public static float Beta = 2;
     //   [Header("DerivativeCutoff This controls how noisy the speed estimate itself is.Before the filter adapts, it estimates velocity:")]
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

            for (int Index = 0; Index < BasisNetworkPlayers.ReceiverCount; Index++)
            {
                var rec = BasisNetworkPlayers.ReceiversSnapshot[Index];
                rec.Compute(UnscaledDeltaTime);
                ushort id = rec.playerId;
                if (id > BasisNetworkPlayers.LargestNetworkReceiverID)
                {
                    BasisNetworkPlayers.LargestNetworkReceiverID = id;
                }
            }
            BasisRemoteNetworkDriver.Compute();
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

            BasisRemoteNetworkDriver.Apply();
            for (int Index = 0; Index < BasisNetworkPlayers.ReceiverCount; Index++)
            {
                BasisNetworkPlayers.ReceiversSnapshot[Index].Apply();
            }

            BoneJobSystem = RemoteBoneJobSystem.Schedule();

            RemoteBoneJobSystem.Complete(BoneJobSystem);
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
