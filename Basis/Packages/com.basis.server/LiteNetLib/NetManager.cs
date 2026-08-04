#if UNITY_2018_3_OR_NEWER
#define UNITY_SOCKET_FIX
#endif
using LiteNetLib.Layers;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LiteNetLib
{
    public sealed class NetPacketReader : NetDataReader
    {
        private NetPacket _packet;
        private readonly NetManager _manager;
        private readonly NetEvent _evt;

        internal NetPacketReader(NetManager manager, NetEvent evt)
        {
            _manager = manager;
            _evt = evt;
        }

        // RecycleEvent pushes onto the free list unconditionally, so recycling the same event
        // twice sets evt.Next = evt and every later rent returns that one event. Cleared on each
        // rent via SetSource.
        private bool _recycled;

        internal void SetSource(NetPacket packet, int headerSize)
        {
            _recycled = false;
            if (packet == null)
                return;
            _packet = packet;
            SetSource(packet.RawData, headerSize, packet.Size);
        }

        internal void RecycleInternal()
        {
            if (_recycled)
                return;
            _recycled = true;
            Clear();
            if (_packet != null)
                _manager.PoolRecycle(_packet);
            _packet = null;
            _manager.RecycleEvent(_evt);
        }

        public void Recycle()
        {
            if (_manager.AutoRecycle)
                return;
            RecycleInternal();
        }
    }

    internal sealed class NetEvent
    {
        public NetEvent Next;

        public enum EType
        {
            Connect,
            Disconnect,
            Receive,
            ReceiveUnconnected,
            Error,
            ConnectionLatencyUpdated,
            Broadcast,
            ConnectionRequest,
            MessageDelivered,
            PeerAddressChanged
        }
        public EType Type;

        public NetPeer Peer;
        public IPEndPoint RemoteEndPoint;
        public object UserData;
        public int Latency;
        public SocketError ErrorCode;
        public DisconnectReason DisconnectReason;
        public ConnectionRequest ConnectionRequest;
        public DeliveryMethod DeliveryMethod;
        public byte ChannelNumber;
        public readonly NetPacketReader DataReader;

        public NetEvent(NetManager manager)
        {
            DataReader = new NetPacketReader(manager, this);
        }
    }

    /// <summary>
    /// Main class for all network operations. Can be used as client and/or server.
    /// </summary>
    public partial class NetManager : IEnumerable<NetPeer>
    {
        public struct NetPeerEnumerator : IEnumerator<NetPeer>
        {
            private readonly NetPeer _initialPeer;
            private NetPeer _p;

            public NetPeerEnumerator(NetPeer p)
            {
                _initialPeer = p;
                _p = null;
            }

            public void Dispose()
            {

            }

            public bool MoveNext()
            {
                _p = _p == null ? _initialPeer : _p.NextPeer;
                return _p != null;
            }

            public void Reset()
            {
                throw new NotSupportedException();
            }

            public NetPeer Current => _p;
            object IEnumerator.Current => _p;
        }

        private struct IncomingData
        {
            public NetPacket Data;
            public IPEndPoint EndPoint;
            public DateTime TimeWhenGet;
        }
        private readonly List<IncomingData> _pingSimulationList = new List<IncomingData>();
        private readonly Random _randomGenerator = new Random();
        private const int MinLatencyThreshold = 5;

        private Thread _logicThread;
        private bool _manualMode;
        private readonly AutoResetEvent _updateTriggerEvent = new AutoResetEvent(true);

        private NetEvent _pendingEventHead;
        private NetEvent _pendingEventTail;

        private NetEvent _netEventPoolHead;
        private readonly INetEventListener _netEventListener;
        private readonly IDeliveryEventListener _deliveryEventListener;
        private readonly INtpEventListener _ntpEventListener;
        private readonly IPeerAddressChangedListener _peerAddressChangedListener;

        private readonly Dictionary<IPEndPoint, ConnectionRequest> _requestsDict = new Dictionary<IPEndPoint, ConnectionRequest>();
        private readonly ConcurrentDictionary<IPEndPoint, NtpRequest> _ntpRequests = new ConcurrentDictionary<IPEndPoint, NtpRequest>();
        private long _connectedPeersCount;
        private readonly List<NetPeer> _connectedPeerListCache = new List<NetPeer>();
        private readonly PacketLayerBase _extraPacketLayer;
        private int _lastPeerId;
        private ConcurrentQueue<int> _peerIds = new ConcurrentQueue<int>();
        private byte _channelsCount = 1;
        private readonly object _eventLock = new object();
        private volatile bool _isRunning;

        // Pre-allocated to avoid per-tick heap allocations in UpdateLogic (called every 2ms).
        private readonly List<NetPeer> _updateSnapshot = new List<NetPeer>(64);
        private readonly ConcurrentQueue<NetPeer> _peersToRemoveQueue = new ConcurrentQueue<NetPeer>();

        private const int ParallelPeerThreshold = 8;
        private float _currentElapsed;
        private readonly Action<NetPeer> _peerUpdateBody;

        /// <summary>
        ///     Used with <see cref="SimulateLatency"/> and <see cref="SimulatePacketLoss"/> to tag packets that
        ///     need to be dropped. Only relevant when <c>DEBUG</c> is defined.
        /// </summary>
        private bool _dropPacket;

        //config section
        /// <summary>
        /// Enable messages receiving without connection. (with SendUnconnectedMessage method)
        /// </summary>
        public bool UnconnectedMessagesEnabled = false;

        /// <summary>
        /// Enable nat punch messages
        /// </summary>
        public bool NatPunchEnabled = false;

        /// <summary>
        /// Library logic update and send period in milliseconds
        /// Lowest values in Windows doesn't change much because of Thread.Sleep precision
        /// To more frequent sends (or sends tied to your game logic) use <see cref="TriggerUpdate"/>
        /// </summary>
        public int UpdateTime = 15;

        /// <summary>
        /// Interval for latency detection and checking connection (in milliseconds)
        /// </summary>
        public int PingInterval = 1000;

        /// <summary>
        /// If NetManager doesn't receive any packet from remote peer during this time (in milliseconds) then connection will be closed
        /// (including library internal keepalive packets)
        /// </summary>
        public int DisconnectTimeout = 5000;

        /// <summary>
        /// Simulate packet loss by dropping random amount of packets. (Works only in DEBUG mode)
        /// </summary>
        public bool SimulatePacketLoss = false;

        /// <summary>
        /// Simulate latency by holding packets for random time. (Works only in DEBUG mode)
        /// </summary>
        public bool SimulateLatency = false;

        /// <summary>
        /// Chance of packet loss when simulation enabled. value in percents (1 - 100).
        /// </summary>
        public int SimulationPacketLossChance = 10;

        /// <summary>
        /// Minimum simulated latency (in milliseconds)
        /// </summary>
        public int SimulationMinLatency = 30;

        /// <summary>
        /// Maximum simulated latency (in milliseconds)
        /// </summary>
        public int SimulationMaxLatency = 100;

        /// <summary>
        /// Events automatically will be called without PollEvents method from another thread
        /// </summary>
        public bool UnsyncedEvents = false;

        /// <summary>
        /// If true - receive event will be called from "receive" thread immediately otherwise on PollEvents call
        /// </summary>
        public bool UnsyncedReceiveEvent = false;

        /// <summary>
        /// If true - delivery event will be called from "receive" thread immediately otherwise on PollEvents call
        /// </summary>
        public bool UnsyncedDeliveryEvent = false;

        /// <summary>
        /// Allows receive broadcast packets
        /// </summary>
        public bool BroadcastReceiveEnabled = false;

        /// <summary>
        /// Delay between initial connection attempts (in milliseconds)
        /// </summary>
        public int ReconnectDelay = 500;

        /// <summary>
        /// Maximum connection attempts before client stops and call disconnect event.
        /// </summary>
        public int MaxConnectAttempts = 10;

        /// <summary>
        /// Enables socket option "ReuseAddress" for specific purposes
        /// </summary>
        public bool ReuseAddress = false;

        /// <summary>
        /// Number of UDP sockets to bind on the same port using SO_REUSEPORT (Linux only).
        /// 1 (default) = single socket / single receive thread, current behavior. Values &gt;1
        /// spawn additional sockets and receive threads so the Linux kernel RSS-hashes inbound
        /// datagrams across them, lifting the single-recv-thread pps ceiling. Each per-peer
        /// 4-tuple still hashes to one socket, so per-peer packet order is preserved.
        /// On non-Linux platforms this falls back to 1 with a warning logged at Start().
        /// </summary>
        public int MultiSocketCount = 1;

        /// <summary>
        /// Maximum number of fragments allowed per message.
        /// Default: ushort.MaxValue (65535)
        /// </summary>
        public ushort MaxFragmentsCount = ushort.MaxValue;

        /// <summary>
        /// UDP Only Socket Option
        /// Normally IP sockets send packets of data through routers and gateways until they reach the final destination.
        /// If the DontRoute flag is set to True, then data will be delivered on the local subnet only.
        /// </summary>
        public bool DontRoute = false;

        /// <summary>
        /// Statistics of all connections
        /// </summary>
        public readonly NetStatistics Statistics = new NetStatistics();

        /// <summary>
        /// Toggles the collection of network statistics for the instance and all known peers
        /// </summary>
        public bool EnableStatistics = false;

        /// <summary>
        /// NatPunchModule for NAT hole punching operations
        /// </summary>
        public readonly NatPunchModule NatPunchModule;

        /// <summary>
        /// Returns true if socket listening and update thread is running
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Local EndPoint (host and port)
        /// </summary>
        public int LocalPort { get; private set; }

        /// <summary>
        /// Automatically recycle NetPacketReader after OnReceive event
        /// </summary>
        public bool AutoRecycle;

        /// <summary>
        /// IPv6 support
        /// </summary>
        public bool IPv6Enabled = true;

        /// <summary>
        /// Override MTU for all new peers registered in this NetManager, will ignores MTU Discovery!
        /// </summary>
        public int MtuOverride = 0;

        /// <summary>
        /// Automatically discovery mtu starting from. Use at own risk because some routers can break MTU detection
        /// and connection in result
        /// </summary>
        public bool MtuDiscovery = false;

        /// <summary>
        /// First peer. Useful for Client mode
        /// </summary>
        public NetPeer FirstPeer => _headPeer;

        /// <summary>
        /// Experimental feature mostly for servers. Only for Windows/Linux
        /// use direct socket calls for send/receive to drastically increase speed and reduce GC pressure
        /// </summary>
        public bool UseNativeSockets = false;

        /// <summary>
        /// How long (ms) a partly-filled merge buffer may wait for more data before being sent.
        /// A full buffer is always sent immediately, so this only ever delays small sends — it
        /// caps added latency rather than adding it. 0 sends every logic pass (legacy behaviour).
        /// </summary>
        public float MergeHoldMs = 0f;

        /// <summary>
        /// Worker cap for the per-peer update pass in <see cref="UpdateLogic"/>. 0 = scale with the
        /// peer count, which is what you want.
        ///
        /// This pass used to run on an uncapped Parallel.ForEach. At a few hundred passes a second
        /// that spread across every core the threadpool would give it — profiling a 500-player
        /// server found 40 distinct threads in the pass, with three quarters of all GC-poll time
        /// coming from Parallel's own worker replication rather than from updating peers.
        /// </summary>
        public int PeerUpdateParallelism = 0;

        /// <summary>
        /// Peers per worker when <see cref="PeerUpdateParallelism"/> is auto.
        ///
        /// A fixed cap is wrong at one end or the other. Capping at 8 regardless of population
        /// fixed the oversubscription at 500 players but starved the pass at 3500, where a single
        /// pass was measured peaking at 1204 ms. That interval is the floor on reliable latency:
        /// the direct-connect handshake needs several round trips inside the client's 4 s budget,
        /// so P2P times out long before anything else looks wrong. Scaling with the peer count
        /// keeps a small instance cheap and a large one responsive.
        /// </summary>
        private int _peersPerUpdateWorker = 128;

        /// <summary>
        /// Peers each worker in the per-peer pass is expected to service. Lower means more workers
        /// for the same population.
        ///
        /// This is the number that decides how much of a large host the server can actually use,
        /// and 128 was fitted to a 32-thread machine with fast cores. It is a ceiling, not a
        /// target: at 4000 peers it picks 31 workers no matter how many cores exist, so a 128-core
        /// host runs at about a quarter utilisation with the pass still over its latency target.
        /// Halving it doubles the workers.
        ///
        /// Tune it against the pass time in the [CPU] log line: above PeerPassTargetMs with cores
        /// to spare means this is too high. Slower cores want a lower value than fast ones, because
        /// a worker gets through fewer peers per pass.
        /// </summary>
        public int PeersPerUpdateWorker
        {
            get => _peersPerUpdateWorker;
            set => _peersPerUpdateWorker = value > 0 ? value : 128;
        }

        /// <summary>
        /// Pass duration above which the pool is under-provisioned. Diagnostic only.
        ///
        /// Three controllers were built to size this pool from live load and all three measured
        /// worse. Duty cycle is degenerate — the loop only waits when a pass fits inside UpdateTime,
        /// so anything longer reports ~1.00 whether it takes 3 ms or 300 ms. Steering on pass
        /// duration alone cannot tell a host with spare cores, where widening is free, from a
        /// saturated one, where it took CPU from 16.2 to 25.2 cores at 2000 players and lowered
        /// throughput. Sizing stays explicit; this exists so an operator can see if theirs is right.
        /// </summary>
        public const double PeerPassTargetMs = 25.0;

        /// <summary>Workers currently running the per-peer pass. Diagnostics.</summary>
        public int PeerUpdateWorkers { get; private set; }

        /// <summary>
        /// Fraction of the machine the host process is using, 0..1. Set by the host; 0 means
        /// unknown, and the pool then stays on the population figure alone.
        ///
        /// Without this the pool cannot tell "slow because it is short of workers" from "slow
        /// because the machine is full", and those want opposite responses.
        /// </summary>
        public double MachineUtilization;

        /// <summary>Widen only below this — above it the cores would come out of something else.</summary>
        private const double GrowBelowUtilization = 0.70;

        /// <summary>Above this the machine is full; hand workers back rather than add contention.</summary>
        private const double ShrinkAboveUtilization = 0.88;

        private long _lastWorkerStepTicks;
        private static readonly long WorkerStepIntervalTicks = Stopwatch.Frequency / 10;   // 100 ms

        // A widening is held for a settle window, then kept only if the pass actually got shorter.
        private int _probeFromWorkers;
        private double _probePassMsBefore;
        private int _probeSettleSteps;
        private int _cooldownSteps;
        private const int ProbeSettleSteps = 8;       // ~800 ms, enough for the average to follow
        private const int ProbeCooldownSteps = 30;    // ~3 s before trying again
        private const double ProbeMustImproveBy = 0.10;

        /// <summary>Smoothed per-peer pass duration in ms — the floor on reliable delivery.</summary>
        public double PeerUpdatePassMs => Volatile.Read(ref _passBusyEma);

        private long _peersUpdatedTotal;
        private long _peerUpdateBusyMicros;

        /// <summary>
        /// Peers updated since start, paired with <see cref="PeerUpdateBusyMicros"/>.
        ///
        /// The ratio of the two is peers per millisecond of pass time — how fast this pass chews
        /// through peers while it is running, as opposed to how many it gets through per second of
        /// wall clock, which is set by the pass interval and says nothing about workers. A host
        /// that samples both over a window can see whether changing the worker count moved it.
        /// </summary>
        public long PeersUpdatedTotal => Interlocked.Read(ref _peersUpdatedTotal);

        /// <summary>Microseconds the per-peer pass has spent working. Pairs with <see cref="PeersUpdatedTotal"/>.</summary>
        public long PeerUpdateBusyMicros => Interlocked.Read(ref _peerUpdateBusyMicros);

        /// <summary>
        /// Ceiling for the auto-sized worker count.
        ///
        /// The host sets this, because this pool is not the only one on the machine — the server's
        /// reduction system runs an overlapping one, and sizing both against the core count
        /// oversubscribes the box (measured at 4000 players: 23.6 cores / 634 MB/s / 153 ms worst
        /// pass with both at full width, against 18.0 / 644 / 108 once they were given shares).
        /// Basis sets it from BasisCpuBudget. Standalone users who never assign it get three
        /// quarters of the box, which is the right answer when this is the only such pool.
        /// </summary>
        public int PeerUpdateWorkerCap = 0;

        private int ResolvedPeerUpdateCap =>
            PeerUpdateWorkerCap > 0
                ? Math.Min(PeerUpdateWorkerCap, Environment.ProcessorCount)
                : Math.Max(4, Environment.ProcessorCount * 3 / 4);

        /// <summary>
        /// Width the auto-sized pool starts from before population raises it. A preference, not a
        /// guarantee — <see cref="ResolvedPeerUpdateCap"/> outranks it on a host that cannot spare
        /// this many.
        /// </summary>
        private const int MinPeerUpdateWorkers = 4;

        /// <summary>
        /// Maximum unreliable packets queued per peer before the oldest are dropped. 0 = unbounded.
        ///
        /// Unbounded is not a safe default for a broadcast server: if the send loop enqueues faster
        /// than the logic pass drains — which is what being CPU-bound looks like — the backlog is
        /// the only thing that grows, and it grows until the process dies. Bounding it turns an
        /// overload into dropped position updates, which is what unreliable delivery is for.
        /// </summary>
        public int MaxUnreliableQueuePerPeer = 256;

        // Slow-pass diagnostics. A pass over this long means reliable delivery is queueing behind
        // it; 50ms is well inside the client's 4s direct-connect handshake budget but already far
        // enough from the normal sub-millisecond pass to be worth saying out loud.
        // Duty cycle of the per-peer pass: how much of each cycle it spends working rather than
        // waiting. This is the pool's "am I behind" signal, and it is deliberately the same shape
        // as the reduction system's so the two can be compared against each other and the core
        // budget shifted toward whichever is actually short. Near 1 means the pass is continuously
        // busy — more workers would let it finish sooner.
        private double _passBusyEma;
        private double _passPeriodEma;

        /// <summary>
        /// Fraction of wall time the per-peer update pass is busy, 0..1. Smoothed, so a single long
        /// pass does not swing it.
        /// </summary>
        public double PeerUpdatePressure
        {
            get
            {
                double period = Volatile.Read(ref _passPeriodEma);
                if (period <= 0.001) return 0;
                double duty = Volatile.Read(ref _passBusyEma) / period;
                return duty < 0 ? 0 : duty > 1 ? 1 : duty;
            }
        }

        private const double SlowPassWarnMs = 50.0;
        private const int SlowPassReportEvery = 200;
        private long _slowPassCount;
        private int _slowPassSinceReport;
        private double _slowPassPeak;

        private long _unreliableDropped;

        /// <summary>Unreliable packets dropped because a peer's send queue was over budget.</summary>
        public long UnreliableDropped => Interlocked.Read(ref _unreliableDropped);

        internal void NoteUnreliableDropped() => Interlocked.Increment(ref _unreliableDropped);

        private ParallelOptions _peerUpdateOptions;

        private ParallelOptions PeerUpdateOptions => GetPeerUpdateOptions(_updateSnapshot.Count);

        private int _adaptivePeerWorkers;

        private ParallelOptions GetPeerUpdateOptions(int peerCount)
        {
            int desired;
            if (PeerUpdateParallelism > 0)
            {
                desired = Math.Min(PeerUpdateParallelism, Environment.ProcessorCount);
            }
            else
            {
                int cap = ResolvedPeerUpdateCap;

                // Population sets the floor, not the ceiling. As a ceiling it capped the whole
                // server: peers-per-worker picks 31 workers for 4000 peers however many cores
                // exist, so a 128-core host ran at about a quarter utilisation with the pass still
                // over target and no way to reach the idle cores.
                //
                // Where floor and cap disagree the cap wins: it is the machine-wide grant, and on a
                // host too small to satisfy every lease's floor the allocator trims the floors to
                // fit, so this pool is legitimately granted 3 on a 4-core box. Passing the two to
                // Math.Clamp as min and max threw out of the entire logic pass when that happened —
                // no peer updates, no reliable delivery, no timeout detection, and a tight loop of
                // ArgumentException in place of the pass, since the throw skips the sleep below.
                int floor = peerCount / _peersPerUpdateWorker;
                if (floor < MinPeerUpdateWorkers) floor = MinPeerUpdateWorkers;
                if (floor > cap) floor = cap;

                int current = _adaptivePeerWorkers;
                if (current < floor) current = floor;
                if (current > cap) current = cap;

                long now = Stopwatch.GetTimestamp();
                if (now - _lastWorkerStepTicks >= WorkerStepIntervalTicks)
                {
                    _lastWorkerStepTicks = now;

                    double passMs = Volatile.Read(ref _passBusyEma);
                    double util = MachineUtilization;

                    if (util <= 0)
                    {
                        // Host is not reporting utilisation, so there is no way to tell short-of-
                        // workers from short-of-cores. Stay on the population figure.
                        current = floor;
                    }
                    else if (util > ShrinkAboveUtilization && current > floor)
                    {
                        // Machine is full. More workers here is contention, not throughput.
                        current--;
                    }
                    else if (_probeSettleSteps > 0)
                    {
                        // A widening is in flight. Hold it until the pass average reflects it, then
                        // judge — headroom measured *before* growing is not proof it helped, since
                        // growing is itself what consumes the headroom. Without this check the pool
                        // grows into saturation and stays: measured at 2000 players it climbed to
                        // the cap, took utilisation from 35% to 78%, and left the pass at 40-55 ms.
                        if (--_probeSettleSteps == 0)
                        {
                            bool helped = passMs < _probePassMsBefore * (1.0 - ProbeMustImproveBy);
                            if (!helped)
                            {
                                current = Math.Max(floor, _probeFromWorkers);
                                _cooldownSteps = ProbeCooldownSteps;
                            }
                        }
                    }
                    else if (_cooldownSteps > 0)
                    {
                        _cooldownSteps--;
                    }
                    else if (passMs > PeerPassTargetMs && util < GrowBelowUtilization && current < cap)
                    {
                        // Slow, and there are cores to fix it with. Climb quickly — an
                        // under-provisioned pass delays every reliable message while it is short,
                        // and on a large host the gap can be a hundred workers.
                        _probeFromWorkers = current;
                        _probePassMsBefore = passMs;
                        _probeSettleSteps = ProbeSettleSteps;
                        current = Math.Min(cap, current + Math.Max(1, current / 4));
                    }
                    else if (passMs < PeerPassTargetMs / 2 && current > floor)
                    {
                        // Comfortably inside target — give one back, slowly.
                        current--;
                    }
                }

                _adaptivePeerWorkers = current;
                desired = current;
            }

            PeerUpdateWorkers = desired;

            var options = _peerUpdateOptions;
            if (options == null || options.MaxDegreeOfParallelism != desired)
            {
                options = new ParallelOptions { MaxDegreeOfParallelism = desired };
                _peerUpdateOptions = options;
            }
            return options;
        }

        /// <summary>
        /// Disconnect peers if HostUnreachable or NetworkUnreachable spawned (old behaviour 0.9.x was true)
        /// </summary>
        public bool DisconnectOnUnreachable = false;

        /// <summary>
        /// Allows peer change it's ip (lte to wifi, wifi to lte, etc). Use only on server
        /// </summary>
        public bool AllowPeerAddressChange = false;

        /// <summary>
        /// QoS channel count per message type (value must be between 1 and 64 channels)
        /// </summary>
        public byte ChannelsCount
        {
            get => _channelsCount;
            set
            {
                if (value < 1 || value > 64)
                    throw new ArgumentException("Channels count must be between 1 and 64");
                _channelsCount = value;
            }
        }

        /// <summary>
        /// Returns connected peers list (with internal cached list)
        /// </summary>
        public List<NetPeer> ConnectedPeerList
        {
            get
            {
                GetPeersNonAlloc(_connectedPeerListCache, ConnectionState.Connected);
                return _connectedPeerListCache;
            }
        }

        /// <summary>
        /// Returns connected peers count
        /// </summary>
        public int ConnectedPeersCount => (int)Interlocked.Read(ref _connectedPeersCount);

        public int ExtraPacketSizeForLayer => _extraPacketLayer?.ExtraPacketSizeForLayer ?? 0;

        /// <summary>
        /// NetManager constructor
        /// </summary>
        /// <param name="listener">Network events listener (also can implement IDeliveryEventListener)</param>
        /// <param name="extraPacketLayer">Extra processing of packages, like CRC checksum or encryption. All connected NetManagers must have same layer.</param>
#if UNITY_SOCKET_FIX
        public NetManager(INetEventListener listener, PacketLayerBase extraPacketLayer = null, bool useSocketFix = true)
        {
            _useSocketFix = useSocketFix;
#else
        public NetManager(INetEventListener listener, PacketLayerBase extraPacketLayer = null)
        {
#endif
            _netEventListener = listener;
            _deliveryEventListener = listener as IDeliveryEventListener;
            _ntpEventListener = listener as INtpEventListener;
            _peerAddressChangedListener = listener as IPeerAddressChangedListener;
            NatPunchModule = new NatPunchModule(this);
            _extraPacketLayer = extraPacketLayer;
            _peerUpdateBody = UpdatePeer;
        }

        private void UpdatePeer(NetPeer netPeer)
        {
            if (netPeer.ConnectionState == ConnectionState.Disconnected &&
                netPeer.TimeSinceLastPacket > DisconnectTimeout)
            {
                _peersToRemoveQueue.Enqueue(netPeer);
            }
            else
            {
                netPeer.Update(_currentElapsed);
            }
        }

        internal void ConnectionLatencyUpdated(NetPeer fromPeer, int latency)
        {
            CreateEvent(NetEvent.EType.ConnectionLatencyUpdated, fromPeer, latency: latency);
        }

        internal void MessageDelivered(NetPeer fromPeer, object userData)
        {
            if (_deliveryEventListener != null)
                CreateEvent(NetEvent.EType.MessageDelivered, fromPeer, userData: userData);
        }

        internal void DisconnectPeerForce(NetPeer peer,
            DisconnectReason reason,
            SocketError socketErrorCode,
            NetPacket eventData)
        {
            DisconnectPeer(peer, reason, socketErrorCode, true, null, 0, 0, eventData);
        }

        private void DisconnectPeer(
            NetPeer peer,
            DisconnectReason reason,
            SocketError socketErrorCode,
            bool force,
            byte[] data,
            int start,
            int count,
            NetPacket eventData)
        {
            var shutdownResult = peer.Shutdown(data, start, count, force);
            if (shutdownResult == ShutdownResult.None)
                return;
            if (shutdownResult == ShutdownResult.WasConnected)
            {
                Interlocked.Decrement(ref _connectedPeersCount);
                RecomputePoolCap();
            }
            CreateEvent(
                NetEvent.EType.Disconnect,
                peer,
                errorCode: socketErrorCode,
                disconnectReason: reason,
                readerSource: eventData);
        }

        private void CreateEvent(
            NetEvent.EType type,
            NetPeer peer = null,
            IPEndPoint remoteEndPoint = null,
            SocketError errorCode = 0,
            int latency = 0,
            DisconnectReason disconnectReason = DisconnectReason.ConnectionFailed,
            ConnectionRequest connectionRequest = null,
            DeliveryMethod deliveryMethod = DeliveryMethod.Unreliable,
            byte channelNumber = 0,
            NetPacket readerSource = null,
            object userData = null)
        {
            NetEvent evt;
            bool unsyncEvent = UnsyncedEvents;

            if (type == NetEvent.EType.Connect)
            {
                Interlocked.Increment(ref _connectedPeersCount);
                RecomputePoolCap();
            }
            else if (type == NetEvent.EType.MessageDelivered)
                unsyncEvent = UnsyncedDeliveryEvent;

            lock (_eventLock)
            {
                evt = _netEventPoolHead;
                if (evt == null)
                    evt = new NetEvent(this);
                else
                    _netEventPoolHead = evt.Next;
            }

            evt.Next = null;
            evt.Type = type;
            evt.DataReader.SetSource(readerSource, readerSource?.GetHeaderSize() ?? 0);
            evt.Peer = peer;
            evt.RemoteEndPoint = remoteEndPoint;
            evt.Latency = latency;
            evt.ErrorCode = errorCode;
            evt.DisconnectReason = disconnectReason;
            evt.ConnectionRequest = connectionRequest;
            evt.DeliveryMethod = deliveryMethod;
            evt.ChannelNumber = channelNumber;
            evt.UserData = userData;

            if (unsyncEvent || _manualMode)
            {
                ProcessEvent(evt);
            }
            else
            {
                lock (_eventLock)
                {
                    if (_pendingEventTail == null)
                        _pendingEventHead = evt;
                    else
                        _pendingEventTail.Next = evt;
                    _pendingEventTail = evt;
                }
            }
        }

        private void ProcessEvent(NetEvent evt)
        {
            //NetDebug.Write("[NM] Processing event: " + evt.Type);
            bool emptyData = evt.DataReader.IsNull;
            switch (evt.Type)
            {
                case NetEvent.EType.Connect:
                    _netEventListener.OnPeerConnected(evt.Peer);
                    break;
                case NetEvent.EType.Disconnect:
                    var info = new DisconnectInfo
                    {
                        Reason = evt.DisconnectReason,
                        AdditionalData = evt.DataReader,
                        SocketErrorCode = evt.ErrorCode
                    };
                    _netEventListener.OnPeerDisconnected(evt.Peer, info);
                    break;
                case NetEvent.EType.Receive:
                    _netEventListener.OnNetworkReceive(evt.Peer, evt.DataReader, evt.ChannelNumber, evt.DeliveryMethod);
                    break;
                case NetEvent.EType.ReceiveUnconnected:
                    _netEventListener.OnNetworkReceiveUnconnected(evt.RemoteEndPoint, evt.DataReader, UnconnectedMessageType.BasicMessage);
                    break;
                case NetEvent.EType.Broadcast:
                    _netEventListener.OnNetworkReceiveUnconnected(evt.RemoteEndPoint, evt.DataReader, UnconnectedMessageType.Broadcast);
                    break;
                case NetEvent.EType.Error:
                    _netEventListener.OnNetworkError(evt.RemoteEndPoint, evt.ErrorCode);
                    break;
                case NetEvent.EType.ConnectionLatencyUpdated:
                    _netEventListener.OnNetworkLatencyUpdate(evt.Peer, evt.Latency);
                    break;
                case NetEvent.EType.ConnectionRequest:
                    _netEventListener.OnConnectionRequest(evt.ConnectionRequest);
                    break;
                case NetEvent.EType.MessageDelivered:
                    _deliveryEventListener.OnMessageDelivered(evt.Peer, evt.UserData);
                    break;
                case NetEvent.EType.PeerAddressChanged:
                    IPEndPoint previousAddress = null;
                    {
                        _peersLock.EnterWriteLock();
                        try
                        {
                            if (ContainsPeer(evt.Peer))
                            {
                                RemovePeerFromSet(evt.Peer);
                                previousAddress = new IPEndPoint(evt.Peer.Address, evt.Peer.Port);
                                evt.Peer.FinishEndPointChange(evt.RemoteEndPoint);
                                AddPeerToSet(evt.Peer);
                            }
                        }
                        finally
                        {
                            _peersLock.ExitWriteLock();
                        }
                    }
                    if (previousAddress != null && _peerAddressChangedListener != null)
                        _peerAddressChangedListener.OnPeerAddressChanged(evt.Peer, previousAddress);
                    break;
            }
            //Recycle if not message
            if (emptyData)
                RecycleEvent(evt);
            else if (AutoRecycle)
                evt.DataReader.RecycleInternal();
        }

        internal void RecycleEvent(NetEvent evt)
        {
            evt.Peer = null;
            evt.ErrorCode = 0;
            evt.RemoteEndPoint = null;
            evt.ConnectionRequest = null;
            lock (_eventLock)
            {
                evt.Next = _netEventPoolHead;
                _netEventPoolHead = evt;
            }
        }

        //Update function
        private void UpdateLogic()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            while (_isRunning)
            {
                try
                {
                    ProcessDelayedPackets();

                    float elapsed = (float)(stopwatch.ElapsedTicks / (double)Stopwatch.Frequency * 1000.0);
                    elapsed = elapsed <= 0.0f ? 0.001f : elapsed;
                    stopwatch.Restart();

                    // 1. Snapshot peers under read lock (reuse pre-allocated list)
                    {
                        _peersLock.EnterReadLock();
                        try
                        {
                            _updateSnapshot.Clear();
                            for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
                            {
                                _updateSnapshot.Add(netPeer);
                            }
                        }
                        finally
                        {
                            _peersLock.ExitReadLock();
                        }
                    }

                    // 2. Update each peer (serial below threshold to skip Parallel.ForEach overhead on small peer lists)
                    //
                    // Each worker opens a send batch for the duration of its partition and flushes
                    // it in localFinally, so a partition's worth of merged datagrams reaches the
                    // kernel in one call instead of one per peer. Peers are only ever touched by
                    // the worker that owns them, so per-peer send order is preserved.
                    _currentElapsed = elapsed;
                    int snapshotCount = _updateSnapshot.Count;
                    if (snapshotCount <= ParallelPeerThreshold)
                    {
                        var batcher = BeginBatch(this);
                        try
                        {
                            for (int i = 0; i < snapshotCount; i++)
                            {
                                _peerUpdateBody(_updateSnapshot[i]);
                            }
                        }
                        finally
                        {
                            EndBatch(batcher);
                        }
                    }
                    else
                    {
                        // Each worker opens its own send batch for its partition and flushes it in
                        // localFinally, so a partition's merged datagrams reach the kernel
                        // together. A peer is only ever touched by the worker holding it, so
                        // per-peer send order is preserved.
                        //
                        // Dedicated threads were tried here instead of Parallel.ForEach and were
                        // measurably worse (+19% CPU per unit throughput): this pass runs a few
                        // hundred times a second and is often trivial, and a fixed pool wakes every
                        // worker regardless while Parallel scales down to the work available.
                        System.Threading.Tasks.Parallel.ForEach(
                            _updateSnapshot,
                            PeerUpdateOptions,
                            () => BeginBatch(this),
                            (peer, _, batcher) => { _peerUpdateBody(peer); return batcher; },
                            EndBatch);
                    }

                    // 3. Remove peers under write lock
                    if (!_peersToRemoveQueue.IsEmpty)
                    {
                        _peersLock.EnterWriteLock();
                        try
                        {
                            while (_peersToRemoveQueue.TryDequeue(out var peer))
                            {
                                RemovePeer(peer, false);
                            }
                        }
                        finally
                        {
                            _peersLock.ExitWriteLock();
                        }
                    }

                    ProcessNtpRequests(elapsed);

                    // Reliable traffic — the P2P handshake, avatar changes, chat — only reaches the
                    // wire when this pass runs, so the pass interval is the floor on reliable
                    // latency. When it stretches, things with their own deadlines start failing
                    // (the client gives a direct-connect handshake 4 s), and nothing else in the
                    // logs would say why. Reported as a rate-limited summary, not per pass.
                    double passMs = stopwatch.Elapsed.TotalMilliseconds;

                    // Busy time against full cycle time (elapsed was read before the restart, so it
                    // spans the previous cycle including its wait). Their ratio is the duty cycle
                    // the budget allocator balances on.
                    _passBusyEma = _passBusyEma <= 0 ? passMs : _passBusyEma * 0.9 + passMs * 0.1;
                    _passPeriodEma = _passPeriodEma <= 0 ? elapsed : _passPeriodEma * 0.9 + elapsed * 0.1;

                    // Peers per millisecond of pass time, accumulated for the host's core allocator
                    // to differentiate. This pair is what lets it measure the width past which more
                    // workers stop shortening the pass, rather than trusting a shipped constant.
                    // Kept as raw totals, not a rate, so the reader picks its own window.
                    _peersUpdatedTotal += snapshotCount;
                    _peerUpdateBusyMicros += (long)(passMs * 1000.0);

                    if (passMs > _slowPassPeak) _slowPassPeak = passMs;
                    if (passMs > SlowPassWarnMs)
                    {
                        _slowPassCount++;
                        if (stopwatch.ElapsedTicks != 0 && ++_slowPassSinceReport >= SlowPassReportEvery)
                        {
                            _slowPassSinceReport = 0;
                            NetDebug.WriteError(
                                $"[NM] Peer update pass is slow: {_slowPassCount} passes over {SlowPassWarnMs}ms " +
                                $"(peak {_slowPassPeak:F0}ms) across {snapshotCount} peers with {PeerUpdateOptions.MaxDegreeOfParallelism} workers. " +
                                "Reliable delivery — including direct-connect handshakes — is delayed by this much.");
                            _slowPassPeak = 0;
                        }
                    }

                    int sleepTime = UpdateTime - (int)stopwatch.ElapsedMilliseconds;
                    if (sleepTime > 0)
                    {
                        _updateTriggerEvent.WaitOne(sleepTime);
                    }
                }
                catch (ThreadAbortException)
                {
                    return;
                }
                catch (Exception e)
                {
                    NetDebug.WriteError("[NM] LogicThread error: " + e);
                }
            }

            stopwatch.Stop();
        }


        [Conditional("DEBUG")]
        private void ProcessDelayedPackets()
        {
            if (!SimulateLatency)
                return;

            var time = DateTime.UtcNow;
            lock (_pingSimulationList)
            {
                for (int i = 0; i < _pingSimulationList.Count; i++)
                {
                    var incomingData = _pingSimulationList[i];
                    if (incomingData.TimeWhenGet <= time)
                    {
                        HandleMessageReceived(incomingData.Data, incomingData.EndPoint);
                        _pingSimulationList.RemoveAt(i);
                        i--;
                    }
                }
            }
        }

        private void ProcessNtpRequests(float elapsedMilliseconds)
        {
            if (_ntpRequests.IsEmpty)
            {
                return;
            }

            List<IPEndPoint> requestsToRemove = null;
            foreach (var ntpRequest in _ntpRequests)
            {
                ntpRequest.Value.Send(_udpSocketv4, elapsedMilliseconds);
                if (ntpRequest.Value.NeedToKill)
                {
                    if (requestsToRemove == null)
                        requestsToRemove = new List<IPEndPoint>();
                    requestsToRemove.Add(ntpRequest.Key);
                }
            }

            if (requestsToRemove != null)
            {
                foreach (var ipEndPoint in requestsToRemove)
                {
                    _ntpRequests.TryRemove(ipEndPoint, out _);
                }
            }
        }

        /// <summary>
        /// Update and send logic. Use this only when NetManager started in manual mode
        /// </summary>
        /// <param name="elapsedMilliseconds">elapsed milliseconds since last update call</param>
        public void ManualUpdate(float elapsedMilliseconds)
        {
            if (!_manualMode)
                return;

            for (var netPeer = _headPeer; netPeer != null;)
            {
                var next = netPeer.NextPeer;
                if (netPeer.ConnectionState == ConnectionState.Disconnected && netPeer.TimeSinceLastPacket > DisconnectTimeout)
                {
                    RemovePeer(netPeer, false);
                }
                else
                {
                    netPeer.Update(elapsedMilliseconds);
                }
                netPeer = next;
            }
            ProcessNtpRequests(elapsedMilliseconds);
        }

        internal NetPeer OnConnectionSolved(ConnectionRequest request, byte[] rejectData, int start, int length)
        {
            NetPeer netPeer = null;

            if (request.Result == ConnectionRequestResult.RejectForce)
            {
                //NetDebug.Write(NetLogLevel.Trace, "[NM] Peer connect reject force.");
                if (rejectData != null && length > 0)
                {
                    var shutdownPacket = PoolGetWithProperty(PacketProperty.Disconnect, length);
                    shutdownPacket.ConnectionNumber = request.InternalPacket.ConnectionNumber;
                    FastBitConverter.GetBytes(shutdownPacket.RawData, 1, request.InternalPacket.ConnectionTime);
                    if (shutdownPacket.Size >= NetConstants.PossibleMtu[0])
                    {
                        NetDebug.WriteError("[Peer] Disconnect additional data size more than MTU!");
                    }
                    else
                        Buffer.BlockCopy(rejectData, start, shutdownPacket.RawData, 9, length);
                    SendRawAndRecycle(shutdownPacket, request.RemoteEndPoint);
                }
                lock (_requestsDict)
                    _requestsDict.Remove(request.RemoteEndPoint);
            }
            else lock (_requestsDict)
            {
                if (TryGetPeer(request.RemoteEndPoint, out netPeer))
                {
                    //already have peer
                }
                else if (request.Result == ConnectionRequestResult.Reject)
                {
                    netPeer = new NetPeer(this, request.RemoteEndPoint, GetNextPeerId());
                    netPeer.Reject(request.InternalPacket, rejectData, start, length);
                    AddPeer(netPeer);
                    //NetDebug.Write(NetLogLevel.Trace, "[NM] Peer connect reject.");
                }
                else //Accept
                {
                    netPeer = new NetPeer(this, request, GetNextPeerId());
                    AddPeer(netPeer);
                    CreateEvent(NetEvent.EType.Connect, netPeer);
                    //NetDebug.Write(NetLogLevel.Trace, $"[NM] Received peer connection Id: {netPeer.ConnectTime}, EP: {netPeer}");
                }
                _requestsDict.Remove(request.RemoteEndPoint);
            }

            return netPeer;
        }

        private int GetNextPeerId()
        {
            return _peerIds.TryDequeue(out int id) ? id : _lastPeerId++;
        }

        private void ProcessConnectRequest(
            IPEndPoint remoteEndPoint,
            NetPeer netPeer,
            NetConnectRequestPacket connRequest)
        {
            //if we have peer
            if (netPeer != null)
            {
                var processResult = netPeer.ProcessConnectRequest(connRequest);
                //NetDebug.Write($"ConnectRequest LastId: {netPeer.ConnectTime}, NewId: {connRequest.ConnectionTime}, EP: {remoteEndPoint}, Result: {processResult}");

                switch (processResult)
                {
                    case ConnectRequestResult.Reconnection:
                        DisconnectPeerForce(netPeer, DisconnectReason.Reconnect, 0, null);
                        RemovePeer(netPeer, true);
                        //go to new connection
                        break;
                    case ConnectRequestResult.NewConnection:
                        RemovePeer(netPeer, true);
                        //go to new connection
                        break;
                    case ConnectRequestResult.P2PLose:
                        DisconnectPeerForce(netPeer, DisconnectReason.PeerToPeerConnection, 0, null);
                        RemovePeer(netPeer, true);
                        //go to new connection
                        break;
                    default:
                        //no operations needed
                        return;
                }
                //ConnectRequestResult.NewConnection
                //Set next connection number
                if (processResult != ConnectRequestResult.P2PLose)
                    connRequest.ConnectionNumber = (byte)((netPeer.ConnectionNum + 1) % NetConstants.MaxConnectionNumber);
                //To reconnect peer
            }
            else
            {
                //NetDebug.Write($"ConnectRequest Id: {connRequest.ConnectionTime}, EP: {remoteEndPoint}");
            }

            ConnectionRequest req;
            lock (_requestsDict)
            {
                if (_requestsDict.TryGetValue(remoteEndPoint, out req))
                {
                    req.UpdateRequest(connRequest);
                    return;
                }
                req = new ConnectionRequest(remoteEndPoint, connRequest, this);
                _requestsDict.Add(remoteEndPoint, req);
            }
            //NetDebug.Write($"[NM] Creating request event: {connRequest.ConnectionTime}");
            CreateEvent(NetEvent.EType.ConnectionRequest, connectionRequest: req);
        }

        private void OnMessageReceived(NetPacket packet, IPEndPoint remoteEndPoint)
        {
            if (packet.Size == 0)
            {
                PoolRecycle(packet);
                return;
            }

            _dropPacket = false;
            HandleSimulateLatency(packet, remoteEndPoint);
            HandleSimulatePacketLoss();
            if (_dropPacket)
            {
                return;
            }

            // ProcessEvents
            HandleMessageReceived(packet, remoteEndPoint);
        }

        [Conditional("DEBUG")]
        private void HandleSimulateLatency(NetPacket packet, IPEndPoint remoteEndPoint)
        {
            if (!SimulateLatency)
            {
                return;
            }

            int latency = _randomGenerator.Next(SimulationMinLatency, SimulationMaxLatency);
            if (latency > MinLatencyThreshold)
            {
                lock (_pingSimulationList)
                {
                    _pingSimulationList.Add(new IncomingData
                    {
                        Data = packet,
                        EndPoint = remoteEndPoint,
                        TimeWhenGet = DateTime.UtcNow.AddMilliseconds(latency)
                    });
                }
                // hold packet
                _dropPacket = true;
            }
        }

        [Conditional("DEBUG")]
        private void HandleSimulatePacketLoss()
        {
            if (SimulatePacketLoss && _randomGenerator.NextDouble() * 100 < SimulationPacketLossChance)
            {
                _dropPacket = true;
            }
        }

        private void HandleMessageReceived(NetPacket packet, IPEndPoint remoteEndPoint)
        {
            var originalPacketSize = packet.Size;
            if (EnableStatistics)
            {
                Statistics.IncrementPacketsReceived();
                Statistics.AddBytesReceived(originalPacketSize);
            }

            if (_ntpRequests.Count > 0 && _ntpRequests.TryGetValue(remoteEndPoint, out var request))
            {
                if (packet.Size < 48)
                {
                    //NetDebug.Write(NetLogLevel.Trace, $"NTP response too short: {packet.Size}");
                    return;
                }

                byte[] copiedData = new byte[packet.Size];
                Buffer.BlockCopy(packet.RawData, 0, copiedData, 0, packet.Size);
                NtpPacket ntpPacket = NtpPacket.FromServerResponse(copiedData, DateTime.UtcNow);
                try
                {
                    ntpPacket.ValidateReply();
                }
                catch (InvalidOperationException ex)
                {
                    //NetDebug.Write(NetLogLevel.Trace, $"NTP response error: {ex.Message}");
                    ntpPacket = null;
                }

                if (ntpPacket != null)
                {
                    _ntpRequests.TryRemove(remoteEndPoint, out _);
                    _ntpEventListener?.OnNtpResponse(ntpPacket);
                }
                return;
            }

            if (_extraPacketLayer != null)
            {
                _extraPacketLayer.ProcessInboundPacket(ref remoteEndPoint, ref packet.RawData, ref packet.Size);
                if (packet.Size == 0)
                    return;
            }

            if (!packet.Verify())
            {
                NetDebug.WriteError("[NM] DataReceived: bad!");
                PoolRecycle(packet);
                return;
            }

            switch (packet.Property)
            {
                //special case connect request
                case PacketProperty.ConnectRequest:
                    if (NetConnectRequestPacket.GetProtocolId(packet) != NetConstants.ProtocolId)
                    {
                        SendRawAndRecycle(PoolGetWithProperty(PacketProperty.InvalidProtocol), remoteEndPoint);
                        return;
                    }
                    break;
                //unconnected messages
                case PacketProperty.Broadcast:
                    if (!BroadcastReceiveEnabled)
                        return;
                    CreateEvent(NetEvent.EType.Broadcast, remoteEndPoint: remoteEndPoint, readerSource: packet);
                    return;
                case PacketProperty.UnconnectedMessage:
                    if (!UnconnectedMessagesEnabled)
                        return;
                    CreateEvent(NetEvent.EType.ReceiveUnconnected, remoteEndPoint: remoteEndPoint, readerSource: packet);
                    return;
                case PacketProperty.NatMessage:
                    if (NatPunchEnabled)
                        NatPunchModule.ProcessMessage(remoteEndPoint, packet);
                    return;
            }

            //Check normal packets
            bool peerFound = remoteEndPoint is NetPeer netPeer || TryGetPeer(remoteEndPoint, out netPeer);

            if (peerFound && EnableStatistics)
            {
                netPeer.Statistics.IncrementPacketsReceived();
                netPeer.Statistics.AddBytesReceived(originalPacketSize);
            }

            switch (packet.Property)
            {
                case PacketProperty.ConnectRequest:
                    var connRequest = NetConnectRequestPacket.FromData(packet);
                    if (connRequest != null)
                        ProcessConnectRequest(remoteEndPoint, netPeer, connRequest);
                    break;
                case PacketProperty.PeerNotFound:
                    if (peerFound) //local
                    {
                        if (netPeer.ConnectionState != ConnectionState.Connected)
                            return;
                        if (packet.Size == 1)
                        {
                            //first reply
                            //send NetworkChanged packet
                            netPeer.ResetMtu();
                            SendRaw(NetConnectAcceptPacket.MakeNetworkChanged(netPeer), remoteEndPoint);
                            //NetDebug.Write($"PeerNotFound sending connection info: {remoteEndPoint}");
                        }
                        else if (packet.Size == 2 && packet.RawData[1] == 1)
                        {
                            //second reply
                            DisconnectPeerForce(netPeer, DisconnectReason.PeerNotFound, 0, null);
                        }
                    }
                    else if (packet.Size > 1) //remote
                    {
                        //check if this is old peer
                        bool isOldPeer = false;

                        if (AllowPeerAddressChange)
                        {
                            //NetDebug.Write($"[NM] Looks like address change: {packet.Size}");
                            var remoteData = NetConnectAcceptPacket.FromData(packet);
                            if (remoteData != null &&
                                remoteData.PeerNetworkChanged &&
                                remoteData.PeerId < _peersArray.Length)
                            {
                                NetPeer peer;
                                {
                                    _peersLock.EnterReadLock();
                                    try
                                    {
                                        peer = _peersArray[remoteData.PeerId];
                                    }
                                    finally
                                    {
                                        _peersLock.ExitReadLock();
                                    }
                                }
                                if (peer != null &&
                                    peer.ConnectTime == remoteData.ConnectionTime &&
                                    peer.ConnectionNum == remoteData.ConnectionNumber)
                                {
                                    if (peer.ConnectionState == ConnectionState.Connected)
                                    {
                                        peer.InitiateEndPointChange();
                                        CreateEvent(NetEvent.EType.PeerAddressChanged, peer, remoteEndPoint);
                                        //NetDebug.Write("[NM] PeerNotFound change address of remote peer");
                                    }
                                    isOldPeer = true;
                                }
                            }
                        }

                        PoolRecycle(packet);

                        //else peer really not found
                        if (!isOldPeer)
                        {
                            var secondResponse = PoolGetWithProperty(PacketProperty.PeerNotFound, 1);
                            secondResponse.RawData[1] = 1;
                            SendRawAndRecycle(secondResponse, remoteEndPoint);
                        }
                    }
                    break;
                case PacketProperty.InvalidProtocol:
                    if (peerFound && netPeer.ConnectionState == ConnectionState.Outgoing)
                        DisconnectPeerForce(netPeer, DisconnectReason.InvalidProtocol, 0, null);
                    break;
                case PacketProperty.Disconnect:
                    if (peerFound)
                    {
                        var disconnectResult = netPeer.ProcessDisconnect(packet);
                        if (disconnectResult == DisconnectResult.None)
                        {
                            PoolRecycle(packet);
                            return;
                        }
                        DisconnectPeerForce(
                            netPeer,
                            disconnectResult == DisconnectResult.Disconnect
                            ? DisconnectReason.RemoteConnectionClose
                            : DisconnectReason.ConnectionRejected,
                            0, packet);
                    }
                    else
                    {
                        PoolRecycle(packet);
                    }
                    //Send shutdown
                    SendRawAndRecycle(PoolGetWithProperty(PacketProperty.ShutdownOk), remoteEndPoint);
                    break;
                case PacketProperty.ConnectAccept:
                    if (!peerFound)
                        return;
                    var connAccept = NetConnectAcceptPacket.FromData(packet);
                    if (connAccept != null && netPeer.ProcessConnectAccept(connAccept))
                        CreateEvent(NetEvent.EType.Connect, netPeer);
                    break;
                default:
                    if (peerFound)
                        netPeer.ProcessPacket(packet);
                    else
                        SendRawAndRecycle(PoolGetWithProperty(PacketProperty.PeerNotFound), remoteEndPoint);
                    break;
            }
        }

        internal void CreateReceiveEvent(NetPacket packet, DeliveryMethod method, byte channelNumber, int headerSize, NetPeer fromPeer)
        {
            NetEvent evt;

            if (UnsyncedEvents || UnsyncedReceiveEvent || _manualMode)
            {
                lock (_eventLock)
                {
                    evt = _netEventPoolHead;
                    if (evt == null)
                        evt = new NetEvent(this);
                    else
                        _netEventPoolHead = evt.Next;
                }
                evt.Next = null;
                evt.Type = NetEvent.EType.Receive;
                evt.DataReader.SetSource(packet, headerSize);
                evt.Peer = fromPeer;
                evt.DeliveryMethod = method;
                evt.ChannelNumber = channelNumber;
                ProcessEvent(evt);
            }
            else
            {
                lock (_eventLock)
                {
                    evt = _netEventPoolHead;
                    if (evt == null)
                        evt = new NetEvent(this);
                    else
                        _netEventPoolHead = evt.Next;

                    evt.Next = null;
                    evt.Type = NetEvent.EType.Receive;
                    evt.DataReader.SetSource(packet, headerSize);
                    evt.Peer = fromPeer;
                    evt.DeliveryMethod = method;
                    evt.ChannelNumber = channelNumber;

                    if (_pendingEventTail == null)
                        _pendingEventHead = evt;
                    else
                        _pendingEventTail.Next = evt;
                    _pendingEventTail = evt;
                }
            }
        }

        /// <summary>
        /// Send data to all connected peers (channel - 0)
        /// </summary>
        /// <param name="writer">DataWriter with data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        public void SendToAll(NetDataWriter writer, DeliveryMethod options)
        {
            SendToAll(writer.Data, 0, writer.Length, options);
        }

        /// <summary>
        /// Send data to all connected peers (channel - 0)
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        public void SendToAll(byte[] data, DeliveryMethod options)
        {
            SendToAll(data, 0, data.Length, options);
        }

        /// <summary>
        /// Send data to all connected peers (channel - 0)
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        public void SendToAll(byte[] data, int start, int length, DeliveryMethod options)
        {
            SendToAll(data, start, length, 0, options);
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="writer">DataWriter with data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        public void SendToAll(NetDataWriter writer, byte channelNumber, DeliveryMethod options)
        {
            SendToAll(writer.Data, 0, writer.Length, channelNumber, options);
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        public void SendToAll(byte[] data, byte channelNumber, DeliveryMethod options)
        {
            SendToAll(data, 0, data.Length, channelNumber, options);
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        public void SendToAll(byte[] data, int start, int length, byte channelNumber, DeliveryMethod options)
        {
            _peersLock.EnterReadLock();
            try
            {
                for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
                    netPeer.Send(data, start, length, channelNumber, options);
            }
            finally
            {
                _peersLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Send data to all connected peers (channel - 0)
        /// </summary>
        /// <param name="writer">DataWriter with data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(NetDataWriter writer, DeliveryMethod options, NetPeer excludePeer)
        {
            SendToAll(writer.Data, 0, writer.Length, 0, options, excludePeer);
        }

        /// <summary>
        /// Send data to all connected peers (channel - 0)
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(byte[] data, DeliveryMethod options, NetPeer excludePeer)
        {
            SendToAll(data, 0, data.Length, 0, options, excludePeer);
        }

        /// <summary>
        /// Send data to all connected peers (channel - 0)
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(byte[] data, int start, int length, DeliveryMethod options, NetPeer excludePeer)
        {
            SendToAll(data, start, length, 0, options, excludePeer);
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="writer">DataWriter with data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(NetDataWriter writer, byte channelNumber, DeliveryMethod options, NetPeer excludePeer)
        {
            SendToAll(writer.Data, 0, writer.Length, channelNumber, options, excludePeer);
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(byte[] data, byte channelNumber, DeliveryMethod options, NetPeer excludePeer)
        {
            SendToAll(data, 0, data.Length, channelNumber, options, excludePeer);
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(byte[] data, int start, int length, byte channelNumber, DeliveryMethod options, NetPeer excludePeer)
        {
            _peersLock.EnterReadLock();
            try
            {
                for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
                {
                    if (netPeer != excludePeer)
                        netPeer.Send(data, start, length, channelNumber, options);
                }
            }
            finally
            {
                _peersLock.ExitReadLock();
            }
        }
        /// <summary>
        /// Send data to all connected peers (channel - 0)
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        public void SendToAll(ReadOnlySpan<byte> data, DeliveryMethod options)
        {
            SendToAll(data, 0, options, null);
        }

        /// <summary>
        /// Send data to all connected peers (channel - 0)
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(ReadOnlySpan<byte> data, DeliveryMethod options, NetPeer excludePeer)
        {
            SendToAll(data, 0, options, excludePeer);
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(ReadOnlySpan<byte> data, byte channelNumber, DeliveryMethod options, NetPeer excludePeer)
        {
            _peersLock.EnterReadLock();
            try
            {
                for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
                {
                    if (netPeer != excludePeer)
                        netPeer.Send(data, channelNumber, options);
                }
            }
            finally
            {
                _peersLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Send message without connection
        /// </summary>
        /// <param name="message">Raw data</param>
        /// <param name="remoteEndPoint">Packet destination</param>
        /// <returns>Operation result</returns>
        public bool SendUnconnectedMessage(ReadOnlySpan<byte> message, IPEndPoint remoteEndPoint)
        {
            int headerSize = NetPacket.GetHeaderSize(PacketProperty.UnconnectedMessage);
            var packet = PoolGetPacket(message.Length + headerSize);
            packet.Property = PacketProperty.UnconnectedMessage;
            message.CopyTo(new Span<byte>(packet.RawData, headerSize, message.Length));
            return SendRawAndRecycle(packet, remoteEndPoint) > 0;
        }

        /// <summary>
        /// Start logic thread and listening on available port
        /// </summary>
        public bool Start()
        {
            return Start(0);
        }

        /// <summary>
        /// Start logic thread and listening on selected port
        /// </summary>
        /// <param name="addressIPv4">bind to specific ipv4 address</param>
        /// <param name="addressIPv6">bind to specific ipv6 address</param>
        /// <param name="port">port to listen</param>
        public bool Start(IPAddress addressIPv4, IPAddress addressIPv6, int port)
        {
            return Start(addressIPv4, addressIPv6, port, false);
        }

        /// <summary>
        /// Start logic thread and listening on selected port
        /// </summary>
        /// <param name="addressIPv4">bind to specific ipv4 address</param>
        /// <param name="addressIPv6">bind to specific ipv6 address</param>
        /// <param name="port">port to listen</param>
        public bool Start(string addressIPv4, string addressIPv6, int port)
        {
            IPAddress ipv4 = NetUtils.ResolveAddress(addressIPv4);
            IPAddress ipv6 = NetUtils.ResolveAddress(addressIPv6);
            return Start(ipv4, ipv6, port);
        }

        /// <summary>
        /// Start logic thread and listening on selected port
        /// </summary>
        /// <param name="port">port to listen</param>
        public bool Start(int port)
        {
            return Start(IPAddress.Any, IPAddress.IPv6Any, port);
        }

        /// <summary>
        /// Start in manual mode and listening on selected port
        /// In this mode you should use ManualReceive (without PollEvents) for receive packets
        /// and ManualUpdate(...) for update and send packets
        /// This mode useful mostly for single-threaded servers
        /// </summary>
        /// <param name="addressIPv4">bind to specific ipv4 address</param>
        /// <param name="addressIPv6">bind to specific ipv6 address</param>
        /// <param name="port">port to listen</param>
        public bool StartInManualMode(IPAddress addressIPv4, IPAddress addressIPv6, int port)
        {
            return Start(addressIPv4, addressIPv6, port, true);
        }

        /// <summary>
        /// Start in manual mode and listening on selected port
        /// In this mode you should use ManualReceive (without PollEvents) for receive packets
        /// and ManualUpdate(...) for update and send packets
        /// This mode useful mostly for single-threaded servers
        /// </summary>
        /// <param name="addressIPv4">bind to specific ipv4 address</param>
        /// <param name="addressIPv6">bind to specific ipv6 address</param>
        /// <param name="port">port to listen</param>
        public bool StartInManualMode(string addressIPv4, string addressIPv6, int port)
        {
            IPAddress ipv4 = NetUtils.ResolveAddress(addressIPv4);
            IPAddress ipv6 = NetUtils.ResolveAddress(addressIPv6);
            return StartInManualMode(ipv4, ipv6, port);
        }

        /// <summary>
        /// Start in manual mode and listening on selected port
        /// In this mode you should use ManualReceive (without PollEvents) for receive packets
        /// and ManualUpdate(...) for update and send packets
        /// This mode useful mostly for single-threaded servers
        /// </summary>
        /// <param name="port">port to listen</param>
        public bool StartInManualMode(int port)
        {
            return StartInManualMode(IPAddress.Any, IPAddress.IPv6Any, port);
        }

        /// <summary>
        /// Send message without connection
        /// </summary>
        /// <param name="message">Raw data</param>
        /// <param name="remoteEndPoint">Packet destination</param>
        /// <returns>Operation result</returns>
        public bool SendUnconnectedMessage(byte[] message, IPEndPoint remoteEndPoint)
        {
            return SendUnconnectedMessage(message, 0, message.Length, remoteEndPoint);
        }

        /// <summary>
        /// Send message without connection. WARNING This method allocates a new IPEndPoint object and
        /// synchronously makes a DNS request. If you're calling this method every frame it will be
        /// much faster to just cache the IPEndPoint.
        /// </summary>
        /// <param name="writer">Data serializer</param>
        /// <param name="address">Packet destination IP or hostname</param>
        /// <param name="port">Packet destination port</param>
        /// <returns>Operation result</returns>
        public bool SendUnconnectedMessage(NetDataWriter writer, string address, int port)
        {
            IPEndPoint remoteEndPoint = NetUtils.MakeEndPoint(address, port);

            return SendUnconnectedMessage(writer.Data, 0, writer.Length, remoteEndPoint);
        }

        /// <summary>
        /// Send message without connection
        /// </summary>
        /// <param name="writer">Data serializer</param>
        /// <param name="remoteEndPoint">Packet destination</param>
        /// <returns>Operation result</returns>
        public bool SendUnconnectedMessage(NetDataWriter writer, IPEndPoint remoteEndPoint)
        {
            return SendUnconnectedMessage(writer.Data, 0, writer.Length, remoteEndPoint);
        }

        /// <summary>
        /// Send message without connection
        /// </summary>
        /// <param name="message">Raw data</param>
        /// <param name="start">data start</param>
        /// <param name="length">data length</param>
        /// <param name="remoteEndPoint">Packet destination</param>
        /// <returns>Operation result</returns>
        public bool SendUnconnectedMessage(byte[] message, int start, int length, IPEndPoint remoteEndPoint)
        {
            //No need for CRC here, SendRaw does that
            NetPacket packet = PoolGetWithData(PacketProperty.UnconnectedMessage, message, start, length);
            return SendRawAndRecycle(packet, remoteEndPoint) > 0;
        }

        /// <summary>
        /// Triggers update and send logic immediately (works asynchronously)
        /// </summary>
        public void TriggerUpdate()
        {
            _updateTriggerEvent.Set();
        }

        /// <summary>
        /// Receive "maxProcessedEvents" pending events. Call this in game update code
        /// In Manual mode it will call also socket Receive (which can be slow)
        /// 0 - receive all events
        /// </summary>
        /// <param name="maxProcessedEvents">Max events that will be processed (called INetEventListener Connect/Receive/Etc), 0 - receive all events</param>
        public void PollEvents(int maxProcessedEvents = 0)
        {
            if (_manualMode)
            {
                if (_udpSocketv4 != null)
                    ManualReceive(_udpSocketv4, _bufferEndPointv4, maxProcessedEvents);
                if (_udpSocketv6 != null && _udpSocketv6 != _udpSocketv4)
                    ManualReceive(_udpSocketv6, _bufferEndPointv6, maxProcessedEvents);
                ProcessDelayedPackets();
                return;
            }
            if (UnsyncedEvents)
                return;
            NetEvent pendingEvent;
            lock (_eventLock)
            {
                pendingEvent = _pendingEventHead;
                _pendingEventHead = null;
                _pendingEventTail = null;
            }

            int counter = 0;
            while (pendingEvent != null)
            {
                var next = pendingEvent.Next;
                ProcessEvent(pendingEvent);
                pendingEvent = next;
                counter++;
                if (maxProcessedEvents > 0 && counter == maxProcessedEvents)
                    break;
            }

            //re-attach unprocessed events so they are not lost
            if (pendingEvent != null)
            {
                lock (_eventLock)
                {
                    var remainingTail = pendingEvent;
                    while (remainingTail.Next != null)
                        remainingTail = remainingTail.Next;

                    remainingTail.Next = _pendingEventHead;
                    _pendingEventHead = pendingEvent;
                    if (_pendingEventTail == null)
                        _pendingEventTail = remainingTail;
                }
            }
        }

        /// <summary>
        /// Connect to remote host
        /// </summary>
        /// <param name="address">Server IP or hostname</param>
        /// <param name="port">Server Port</param>
        /// <param name="key">Connection key</param>
        /// <returns>New NetPeer if new connection, Old NetPeer if already connected, null peer if there is ConnectionRequest awaiting</returns>
        /// <exception cref="InvalidOperationException">Manager is not running. Call <see cref="Start()"/></exception>
        public NetPeer Connect(string address, int port, string key)
        {
            return Connect(address, port, NetDataWriter.FromString(key));
        }

        /// <summary>
        /// Connect to remote host
        /// </summary>
        /// <param name="address">Server IP or hostname</param>
        /// <param name="port">Server Port</param>
        /// <param name="connectionData">Additional data for remote peer</param>
        /// <returns>New NetPeer if new connection, Old NetPeer if already connected, null peer if there is ConnectionRequest awaiting</returns>
        /// <exception cref="InvalidOperationException">Manager is not running. Call <see cref="Start()"/></exception>
        public NetPeer Connect(string address, int port, NetDataWriter connectionData)
        {
            IPEndPoint ep;
            try
            {
                ep = NetUtils.MakeEndPoint(address, port);
            }
            catch
            {
                CreateEvent(NetEvent.EType.Disconnect, disconnectReason: DisconnectReason.UnknownHost);
                return null;
            }
            return Connect(ep, connectionData);
        }

        /// <summary>
        /// Connect to remote host
        /// </summary>
        /// <param name="target">Server end point (ip and port)</param>
        /// <param name="key">Connection key</param>
        /// <returns>New NetPeer if new connection, Old NetPeer if already connected, null peer if there is ConnectionRequest awaiting</returns>
        /// <exception cref="InvalidOperationException">Manager is not running. Call <see cref="Start()"/></exception>
        public NetPeer Connect(IPEndPoint target, string key)
        {
            return Connect(target, NetDataWriter.FromString(key));
        }

        /// <summary>
        /// Connect to remote host
        /// </summary>
        /// <param name="target">Server end point (ip and port)</param>
        /// <param name="connectionData">Additional data for remote peer</param>
        /// <returns>New NetPeer if new connection, Old NetPeer if already connected, null peer if there is ConnectionRequest awaiting</returns>
        /// <exception cref="InvalidOperationException">Manager is not running. Call <see cref="Start()"/></exception>
        public NetPeer Connect(IPEndPoint target, NetDataWriter connectionData)
        {
            if (!_isRunning)
                throw new InvalidOperationException("Client is not running");

            lock (_requestsDict)
            {
                if (_requestsDict.ContainsKey(target))
                    return null;

                byte connectionNumber = 0;
                if (TryGetPeer(target, out var peer))
                {
                    switch (peer.ConnectionState)
                    {
                        //just return already connected peer
                        case ConnectionState.Connected:
                        case ConnectionState.Outgoing:
                            return peer;
                    }
                    //else reconnect
                    connectionNumber = (byte)((peer.ConnectionNum + 1) % NetConstants.MaxConnectionNumber);
                    RemovePeer(peer, true);
                }

                //Create reliable connection
                //And send connection request
                peer = new NetPeer(this, target, GetNextPeerId(), connectionNumber, connectionData);
                AddPeer(peer);
                return peer;
            }
        }
        /// <summary>
        /// Connect to remote host
        /// </summary>
        /// <param name="target">Server end point (ip and port)</param>
        /// <param name="connectionData">Additional data for remote peer</param>
        /// <returns>New NetPeer if new connection, Old NetPeer if already connected, null peer if there is ConnectionRequest awaiting</returns>
        /// <exception cref="InvalidOperationException">Manager is not running. Call <see cref="Start()"/></exception>
        public NetPeer Connect(IPEndPoint target, ReadOnlySpan<byte> connectionData)
        {
            if (!_isRunning)
                throw new InvalidOperationException("Client is not running");

            lock (_requestsDict)
            {
                if (_requestsDict.ContainsKey(target))
                    return null;

                byte connectionNumber = 0;
                if (TryGetPeer(target, out var peer))
                {
                    switch (peer.ConnectionState)
                    {
                        //just return already connected peer
                        case ConnectionState.Connected:
                        case ConnectionState.Outgoing:
                            return peer;
                    }
                    //else reconnect
                    connectionNumber = (byte)((peer.ConnectionNum + 1) % NetConstants.MaxConnectionNumber);
                    RemovePeer(peer, true);
                }

                //Create reliable connection
                //And send connection request
                peer = new NetPeer(this, target, GetNextPeerId(), connectionNumber, connectionData);
                AddPeer(peer);
                return peer;
            }
        }
        /// <summary>
        /// Force closes connection and stop all threads.
        /// </summary>
        public void Stop()
        {
            Stop(true);
        }

        /// <summary>
        /// Force closes connection and stop all threads.
        /// </summary>
        /// <param name="sendDisconnectMessages">Send disconnect messages</param>
        public void Stop(bool sendDisconnectMessages)
        {
            if (!_isRunning)
                return;
            //NetDebug.Write("[NM] Stop");

            //Send last disconnect
            for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
                netPeer.Shutdown(null, 0, 0, !sendDisconnectMessages);

            //Stop
            CloseSocket();

#if UNITY_SOCKET_FIX
            if (_useSocketFix)
            {
                _pausedSocketFix.Deinitialize();
                _pausedSocketFix = null;
            }
#endif

            _updateTriggerEvent.Set();
            if (!_manualMode)
            {
                _logicThread.Join();
                _logicThread = null;
            }

            //clear peers
            ClearPeerSet();
            _peerIds = new ConcurrentQueue<int>();
            _lastPeerId = 0;

            ClearPingSimulationList();

            _connectedPeersCount = 0;
            RecomputePoolCap();
            _pendingEventHead = null;
            _pendingEventTail = null;
        }

        [Conditional("DEBUG")]
        private void ClearPingSimulationList()
        {
            lock (_pingSimulationList)
                _pingSimulationList.Clear();
        }

        /// <summary>
        /// Return peers count with connection state
        /// </summary>
        /// <param name="peerState">peer connection state (you can use as bit flags)</param>
        /// <returns>peers count</returns>
        public int GetPeersCount(ConnectionState peerState)
        {
            int count = 0;
            _peersLock.EnterReadLock();
            try
            {
                for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
                {
                    if ((netPeer.ConnectionState & peerState) != 0)
                        count++;
                }
            }
            finally
            {
                _peersLock.ExitReadLock();
            }
            return count;
        }

        /// <summary>
        /// Get copy of peers (without allocations)
        /// </summary>
        /// <param name="peers">List that will contain result</param>
        /// <param name="peerState">State of peers</param>
        public void GetPeersNonAlloc(List<NetPeer> peers, ConnectionState peerState)
        {
            peers.Clear();
            _peersLock.EnterReadLock();
            try
            {
                for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
                {
                    if ((netPeer.ConnectionState & peerState) != 0)
                        peers.Add(netPeer);
                }
            }
            finally
            {
                _peersLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Disconnect all peers without any additional data
        /// </summary>
        public void DisconnectAll()
        {
            DisconnectAll(null, 0, 0);
        }

        /// <summary>
        /// Disconnect all peers with shutdown message
        /// </summary>
        /// <param name="data">Data to send (must be less or equal MTU)</param>
        /// <param name="start">Data start</param>
        /// <param name="count">Data count</param>
        public void DisconnectAll(byte[] data, int start, int count)
        {
            //Send disconnect packets
            _peersLock.EnterReadLock();
            try
            {
                for (var netPeer = _headPeer; netPeer != null; netPeer = netPeer.NextPeer)
                {
                    DisconnectPeer(
                        netPeer,
                        DisconnectReason.DisconnectPeerCalled,
                        0,
                        false,
                        data,
                        start,
                        count,
                        null);
                }
            }
            finally
            {
                _peersLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Immediately disconnect peer from server without additional data
        /// </summary>
        /// <param name="peer">peer to disconnect</param>
        public void DisconnectPeerForce(NetPeer peer)
        {
            DisconnectPeerForce(peer, DisconnectReason.DisconnectPeerCalled, 0, null);
        }

        /// <summary>
        /// Disconnect peer from server
        /// </summary>
        /// <param name="peer">peer to disconnect</param>
        public void DisconnectPeer(NetPeer peer)
        {
            DisconnectPeer(peer, null, 0, 0);
        }

        /// <summary>
        /// Disconnect peer from server and send additional data (Size must be less or equal MTU - 8)
        /// </summary>
        /// <param name="peer">peer to disconnect</param>
        /// <param name="data">additional data</param>
        public void DisconnectPeer(NetPeer peer, byte[] data)
        {
            DisconnectPeer(peer, data, 0, data.Length);
        }

        /// <summary>
        /// Disconnect peer from server and send additional data (Size must be less or equal MTU - 8)
        /// </summary>
        /// <param name="peer">peer to disconnect</param>
        /// <param name="writer">additional data</param>
        public void DisconnectPeer(NetPeer peer, NetDataWriter writer)
        {
            DisconnectPeer(peer, writer.Data, 0, writer.Length);
        }

        /// <summary>
        /// Disconnect peer from server and send additional data (Size must be less or equal MTU - 8)
        /// </summary>
        /// <param name="peer">peer to disconnect</param>
        /// <param name="data">additional data</param>
        /// <param name="start">data start</param>
        /// <param name="count">data length</param>
        public void DisconnectPeer(NetPeer peer, byte[] data, int start, int count)
        {
            DisconnectPeer(
                peer,
                DisconnectReason.DisconnectPeerCalled,
                0,
                false,
                data,
                start,
                count,
                null);
        }

        /// <summary>
        /// Create the requests for NTP server
        /// </summary>
        /// <param name="endPoint">NTP Server address.</param>
        public void CreateNtpRequest(IPEndPoint endPoint)
        {
            _ntpRequests.TryAdd(endPoint, new NtpRequest(endPoint));
        }

        /// <summary>
        /// Create the requests for NTP server
        /// </summary>
        /// <param name="ntpServerAddress">NTP Server address.</param>
        /// <param name="port">port</param>
        public void CreateNtpRequest(string ntpServerAddress, int port)
        {
            IPEndPoint endPoint = NetUtils.MakeEndPoint(ntpServerAddress, port);
            _ntpRequests.TryAdd(endPoint, new NtpRequest(endPoint));
        }

        /// <summary>
        /// Create the requests for NTP server (default port)
        /// </summary>
        /// <param name="ntpServerAddress">NTP Server address.</param>
        public void CreateNtpRequest(string ntpServerAddress)
        {
            IPEndPoint endPoint = NetUtils.MakeEndPoint(ntpServerAddress, NtpRequest.DefaultPort);
            _ntpRequests.TryAdd(endPoint, new NtpRequest(endPoint));
        }

        public NetPeerEnumerator GetEnumerator()
        {
            return new NetPeerEnumerator(_headPeer);
        }

        IEnumerator<NetPeer> IEnumerable<NetPeer>.GetEnumerator()
        {
            return new NetPeerEnumerator(_headPeer);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new NetPeerEnumerator(_headPeer);
        }
    }
}
