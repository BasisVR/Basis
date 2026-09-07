#if UNITY_2018_3_OR_NEWER
#define UNITY_SOCKET_FIX
#endif
using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    public partial class NetManager
    {
        private Socket _udpSocketv4;
        private Socket _udpSocketv6;
        private Thread _receiveThread;
        /// <summary>Second receive thread, owning the IPv6 socket. Null when IPv6 is not bound.</summary>
        private Thread _receiveThreadV6;
        private IPEndPoint _bufferEndPointv4;
        private IPEndPoint _bufferEndPointv6;
        private EndPoint _receiveEndPoint4 = new IPEndPoint(IPAddress.Any, 0);
        private EndPoint _receiveEndPoint6 = new IPEndPoint(IPAddress.IPv6Any, 0);

        // Extra SO_REUSEPORT sockets + their receive threads, populated when MultiSocketCount > 1 on Linux.
        private readonly List<Socket> _extraSocketsV4 = new List<Socket>();
        private readonly List<Socket> _extraSocketsV6 = new List<Socket>();
        private readonly List<Thread> _extraReceiveThreads = new List<Thread>();
        private bool _useReusePort;

        /// <summary>
        /// Send paths actually bound — 1, plus however many extra SO_REUSEPORT sockets succeeded.
        /// The host sizes its send worker pool from this: concurrent send capacity is set by how
        /// many sockets there are, not by how many cores, and asking for eight while running on one
        /// would size the pool for capacity that is not there.
        /// </summary>
        public int BoundSendSocketCount { get; private set; } = 1;

        private IPAddress _reusePortAddressV4;
        private IPAddress _reusePortAddressV6;
        private readonly object _socketGrowLock = new object();

        /// <summary>
        /// The sockets a send may actually leave through: the primary, plus every extra
        /// SO_REUSEPORT socket that bound. Rebuilt copy-on-write under <see cref="_socketGrowLock"/>
        /// so a sender reads one stable array without taking a lock.
        ///
        /// Every socket in the group is bound to the same address and port, so which one a datagram
        /// leaves through is invisible to the peer - the source 4-tuple is identical either way.
        /// What differs is the kernel-side path: one socket is one send queue every worker
        /// serialises on, which is why the host derives its send worker ceiling from how many are
        /// bound. Sending only through the primary would make that ceiling a promise of capacity
        /// that was never wired up.
        /// </summary>
        private volatile Socket[] _sendSocketsV4 = Array.Empty<Socket>();
        private volatile Socket[] _sendSocketsV6 = Array.Empty<Socket>();

        /// <summary>
        /// Which send socket this thread uses, assigned on first send and kept for the thread's
        /// life. Stable rather than round-robin per datagram: a batch belongs to one socket, so a
        /// thread that moved between them mid-partition would flush a short batch on every switch
        /// and give back the syscall the batcher exists to save. Stored as slot+1 so the
        /// unassigned default (0) is distinguishable.
        /// </summary>
        [ThreadStatic] private static int _sendSlotPlusOne;
        private static int _sendSlotCounter = -1;

        private static int SendSlot
        {
            get
            {
                int slot = _sendSlotPlusOne;
                if (slot == 0)
                {
                    slot = (Interlocked.Increment(ref _sendSlotCounter) & int.MaxValue) + 1;
                    _sendSlotPlusOne = slot;
                }
                return slot - 1;
            }
        }

        /// <summary>Rebuilds the send snapshot. Called whenever the bound socket set changes.</summary>
        private void RebuildSendSockets()
        {
            _sendSocketsV4 = BuildSendSocketList(_udpSocketv4, _extraSocketsV4);
            _sendSocketsV6 = BuildSendSocketList(_udpSocketv6, _extraSocketsV6);
        }

        private static Socket[] BuildSendSocketList(Socket primary, List<Socket> extras)
        {
            if (primary == null) return Array.Empty<Socket>();
            var list = new Socket[1 + extras.Count];
            list[0] = primary;
            for (int i = 0; i < extras.Count; i++) list[i + 1] = extras[i];
            return list;
        }

        /// <summary>
        /// The socket this thread sends through for the given family, or null when none is bound.
        /// </summary>
        private Socket PickSendSocket(bool useIPv6)
        {
            Socket[] pool = useIPv6 ? _sendSocketsV6 : _sendSocketsV4;
            int count = pool.Length;
            if (count == 0) return useIPv6 ? _udpSocketv6 : _udpSocketv4;
            if (count == 1) return pool[0];
            return pool[SendSlot % count];
        }

        /// <summary>
        /// Whether more send paths can be added at runtime. Only true on Linux with SO_REUSEPORT
        /// working — everywhere else the port cannot be shared and the answer is fixed at startup.
        /// </summary>
        public bool CanAddSendSockets => _useReusePort && _isRunning;

        /// <summary>
        /// Binds one more SO_REUSEPORT socket on the listen port and starts a receive thread for it.
        ///
        /// This is the axis the send path actually scales on. Adding worker threads to one socket
        /// makes things worse, not better — measured at 1000 players, going 8 to 16 to 32 send
        /// workers took the update phase from 6.1 to 12.9 to 15.4 ms per tick while throughput fell
        /// from 497 to 393 MB/s, because the threads simply queue on the same socket. Another socket
        /// is another independent path, and the host's send worker ceiling is derived from how many
        /// are bound, so this widens both at once.
        ///
        /// Grow-only by design. Removing a socket means tearing down a live receive thread and
        /// dropping whatever is in its queue, and the sockets are cheap to keep; a server that
        /// needed the capacity once will very likely need it again.
        ///
        /// ⚠️ Adding a socket changes the size of the kernel's SO_REUSEPORT group, so which socket a
        /// given 4-tuple hashes to can change. Existing peers may see a brief reordering as their
        /// flow moves; the channel layer already tolerates that, but it is why this grows on
        /// sustained pressure rather than on a momentary spike.
        /// </summary>
        public bool TryAddSendSocket()
        {
            if (!_useReusePort || !_isRunning) return false;

            lock (_socketGrowLock)
            {
                int index = _extraSocketsV4.Count + 1;

                var extraV4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (!BindSocket(extraV4, new IPEndPoint(_reusePortAddressV4, LocalPort)))
                {
                    NetDebug.WriteError($"[NM] Extra REUSEPORT socket #{index} (v4) bind failed; staying at {BoundSendSocketCount} send sockets");
                    extraV4.Close();
                    return false;
                }
                _extraSocketsV4.Add(extraV4);

                Socket extraV6 = null;
                if (_udpSocketv6 != null && _reusePortAddressV6 != null)
                {
                    extraV6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                    if (!BindSocket(extraV6, new IPEndPoint(_reusePortAddressV6, LocalPort)))
                    {
                        // v6 bind can fail with AddressAlreadyInUse on dual-stack hosts even with
                        // REUSEPORT — degrade to v4-only for this slot rather than aborting.
                        extraV6.Close();
                        extraV6 = null;
                    }
                    else
                    {
                        _extraSocketsV6.Add(extraV6);
                    }
                }

                Socket capturedV4 = extraV4;
                Socket capturedV6 = extraV6;
                Thread extraThread = new Thread(() =>
                {
                    if (UseNativeSockets)
                        NativeReceiveLogicForSocketPair(capturedV4, capturedV6);
                    else
                        ReceiveLogicForSocketPair(capturedV4, capturedV6);
                })
                {
                    Name = $"ReceiveThread({LocalPort}#{index})",
                    IsBackground = true
                };
                _extraReceiveThreads.Add(extraThread);
                extraThread.Start();

                BoundSendSocketCount = 1 + _extraSocketsV4.Count;
                RebuildSendSockets();
                return true;
            }
        }
#if UNITY_SOCKET_FIX
        private PausedSocketFix _pausedSocketFix;
        private bool _useSocketFix;
#endif

#if NET8_0_OR_GREATER
        // ThreadStatic so each receive thread (primary + SO_REUSEPORT extras) gets its own
        // SocketAddress scratch — Socket.ReceiveFrom writes the source address into this buffer,
        // so a shared instance across threads would race.
        [ThreadStatic] private static SocketAddress _sockAddrCacheV4Ts;
        [ThreadStatic] private static SocketAddress _sockAddrCacheV6Ts;

        private static SocketAddress GetSockAddrCache(AddressFamily af)
        {
            if (af == AddressFamily.InterNetwork)
            {
                if (_sockAddrCacheV4Ts == null) _sockAddrCacheV4Ts = new SocketAddress(AddressFamily.InterNetwork);
                return _sockAddrCacheV4Ts;
            }
            if (_sockAddrCacheV6Ts == null) _sockAddrCacheV6Ts = new SocketAddress(AddressFamily.InterNetworkV6);
            return _sockAddrCacheV6Ts;
        }
#endif

        private const int SioUdpConnreset = -1744830452; //SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12
        private static readonly IPAddress MulticastAddressV6 = IPAddress.Parse("ff02::1");
        public static readonly bool IPv6Support;

        // special case in iOS (and possibly android that should be resolved in unity)
        internal bool NotConnected;

        /// <summary>
        /// Poll timeout in microseconds. Increasing can slightly increase performance in cost of slow NetManager.Stop(Socket.Close)
        /// </summary>
        public int ReceivePollingTime = 50000; //0.05 second

        public short Ttl
        {
            get
            {
#if UNITY_SWITCH
                return 0;
#else
                return _udpSocketv4.Ttl;
#endif
            }
            internal set
            {
#if !UNITY_SWITCH
                _udpSocketv4.Ttl = value;
#endif
            }
        }

        static NetManager()
        {
#if DISABLE_IPV6
            IPv6Support = false;
#elif !UNITY_2019_1_OR_NEWER && !UNITY_2018_4_OR_NEWER && (!UNITY_EDITOR && ENABLE_IL2CPP)
            string version = UnityEngine.Application.unityVersion;
            IPv6Support = Socket.OSSupportsIPv6 && int.Parse(version.Remove(version.IndexOf('f')).Split('.')[2]) >= 6;
#else
            IPv6Support = Socket.OSSupportsIPv6;
#endif
        }

        private bool ProcessError(SocketException ex)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.NotConnected:
                    NotConnected = true;
                    return true;
                case SocketError.Interrupted:
                case SocketError.NotSocket:
                case SocketError.OperationAborted:
                    return true;
                case SocketError.ConnectionReset:
                case SocketError.MessageSize:
                case SocketError.TimedOut:
                case SocketError.NetworkReset:
                case SocketError.WouldBlock:
                    ////NetDebug.Write($"[R]Ignored error: {(int)ex.SocketErrorCode} - {ex}");
                    break;
                default:
                    NetDebug.WriteError($"[R]Error code: {(int)ex.SocketErrorCode} - {ex}");
                    CreateEvent(NetEvent.EType.Error, errorCode: ex.SocketErrorCode);
                    break;
            }
            return false;
        }

        // Manual-mode native receive state. Manual mode is driven by a single caller per manager,
        // so these are plain fields rather than per-call allocations.
        private byte[] _manualAddrBuffer;
        private IPEndPoint _manualTempEndPoint;
        /// <summary>Buffer held across polls so an empty drain costs no pool traffic.</summary>
        private NetPacket _manualReceivePacket;

        /// <summary>
        /// Decodes a native sockaddr into <paramref name="endPoint"/>.
        ///
        /// Deliberately a copy of the decode inside NativeReceiveLogicForSocketPair rather than a
        /// refactor of it — that one is the server's hot receive loop and is not worth disturbing
        /// for this. Keep the two in step if the address layout ever changes.
        /// </summary>
        private static void DecodeNativeAddress(byte[] address, IPEndPoint endPoint)
        {
            short family = (short)((address[1] << 8) | address[0]);
            endPoint.Port = (ushort)((address[2] << 8) | address[3]);

            if ((NativeSocket.UnixMode && family == NativeSocket.AF_INET6) ||
                (!NativeSocket.UnixMode && (AddressFamily)family == AddressFamily.InterNetworkV6))
            {
                uint scope = unchecked((uint)(
                    (address[27] << 24) +
                    (address[26] << 16) +
                    (address[25] << 8) +
                    (address[24])));
#if NETCOREAPP || NETSTANDARD2_1 || NETSTANDARD2_1_OR_GREATER
                endPoint.Address = new IPAddress(new ReadOnlySpan<byte>(address, 8, 16), scope);
#else
                byte[] addrBuffer = new byte[16];
                Buffer.BlockCopy(address, 8, addrBuffer, 0, 16);
                endPoint.Address = new IPAddress(addrBuffer, scope);
#endif
            }
            else //IPv4
            {
                long ipv4Addr = unchecked((uint)((address[4] & 0x000000FF) |
                                                 (address[5] << 8 & 0x0000FF00) |
                                                 (address[6] << 16 & 0x00FF0000) |
                                                 (address[7] << 24)));
                endPoint.Address = new IPAddress(ipv4Addr);
            }
        }

        /// <summary>
        /// Drains a non-blocking socket until the kernel says there is nothing left.
        ///
        /// The blocking path has to ask Socket.Available before every receive, because a blocking
        /// receive on an empty socket would hang the caller — one extra syscall per datagram plus
        /// one per poll just to find out the socket was idle. Here the receive itself reports the
        /// end of the queue by returning WouldBlock, so a poll that reads N datagrams costs N+1
        /// syscalls instead of 2N+1.
        /// </summary>
        private void ManualReceiveNative(Socket socket, int maxReceive)
        {
            if (_manualAddrBuffer == null) _manualAddrBuffer = new byte[NativeSocket.IPv6AddrSize];
            if (_manualTempEndPoint == null) _manualTempEndPoint = new IPEndPoint(IPAddress.Any, 0);

            IntPtr handle = socket.Handle;
            int received = 0;

            // The buffer is rented once and kept across polls, and only replaced after one has
            // actually been handed to a consumer. Every drain ends with a receive that finds
            // nothing, so renting per attempt would mean a pool round-trip per socket per poll —
            // at 4000 sockets, a few hundred thousand pointless rent/recycle pairs a second.
            if (_manualReceivePacket == null) _manualReceivePacket = PoolGetPacket(NetConstants.MaxPacketSize);
            NetPacket packet = _manualReceivePacket;

            while (true)
            {
                int addrSize = _manualAddrBuffer.Length;
                int size = NativeSocket.RecvFrom(handle, packet.RawData, NetConstants.MaxPacketSize, _manualAddrBuffer, ref addrSize);

                if (size <= 0)
                {
                    if (size == 0) return; //socket closed or empty packet

                    SocketError error = NativeSocket.GetSocketError();
                    // The expected way to end a drain, not a failure.
                    if (error == SocketError.WouldBlock || error == SocketError.TimedOut) return;
                    ProcessError(new SocketException((int)error));
                    return;
                }

                packet.Size = size;
                DecodeNativeAddress(_manualAddrBuffer, _manualTempEndPoint);

                if (TryGetPeer(_manualTempEndPoint, out var peer))
                {
                    OnMessageReceived(packet, peer);
                }
                else
                {
                    OnMessageReceived(packet, _manualTempEndPoint);
                    // Handed off to a consumer that may retain it — start a fresh one.
                    _manualTempEndPoint = new IPEndPoint(IPAddress.Any, 0);
                }

                // The packet now belongs to whoever received it; take a fresh one for the next.
                packet = PoolGetPacket(NetConstants.MaxPacketSize);
                _manualReceivePacket = packet;

                if (++received == maxReceive) return;
            }
        }

        private void ManualReceive(Socket socket, EndPoint bufferEndPoint, int maxReceive)
        {
            //Reading data
            try
            {
                // Non-blocking (set in BindSocket for manual mode when the native path is usable):
                // drain by receiving until WouldBlock, with no Available calls at all.
                if (!socket.Blocking)
                {
                    ManualReceiveNative(socket, maxReceive);
                    return;
                }

                // Available is checked before every receive on purpose: the socket is blocking, so
                // receiving without a confirmed pending datagram would hang the caller. Spending a
                // single Available reading as a multi-datagram budget was tried and rejected — it
                // is only safe if Available reports the whole queue, and for datagram sockets some
                // platforms report just the first datagram, which would deadlock the driver.
                //
                // This does make Available the dominant cost of a manual-mode client at scale
                // (measured: 34% of a 4000-client load tester's CPU). The fix is not here — it is
                // to stop asking per socket. See Socket.Select, which answers for a whole batch.
                int packetsReceived = 0;
                while (socket.Available > 0)
                {
                    ReceiveFrom(socket, ref bufferEndPoint);
                    packetsReceived++;
                    if (packetsReceived == maxReceive)
                        break;
                }
            }
            catch (SocketException ex)
            {
                ProcessError(ex);
            }
            catch (ObjectDisposedException)
            {

            }
            catch (Exception e)
            {
                //protects socket receive thread
                NetDebug.WriteError("[NM] SocketReceiveThread error: " + e);
            }
        }

        private void NativeReceiveLogic() => NativeReceiveLogicForSocketPair(_udpSocketv4, _udpSocketv6);

        /// <summary>
        /// Starts one receive thread for <paramref name="socketA"/> (and <paramref name="socketB"/>
        /// if a pair is genuinely wanted). Passing null for the second socket puts the loop on its
        /// blocking single-socket path, which is the point: no Select, no Available.
        /// </summary>
        private Thread StartReceiveThread(Socket socketA, Socket socketB, string name)
        {
            Thread t = new Thread(() =>
            {
                if (UseNativeSockets)
                    NativeReceiveLogicForSocketPair(socketA, socketB);
                else
                    ReceiveLogicForSocketPair(socketA, socketB);
            })
            {
                Name = name,
                IsBackground = true
            };
            t.Start();
            return t;
        }

        private void NativeReceiveLogicForSocketPair(Socket socketv4, Socket socketV6)
        {
            IntPtr socketHandle4 = socketv4.Handle;
            IntPtr socketHandle6 = socketV6?.Handle ?? IntPtr.Zero;
            // Sized from the socket rather than assumed v4: with one thread per socket the first
            // argument is whichever family that thread owns, and RecvFrom writes a sockaddr of
            // that family into this buffer.
            byte[] addrBuffer4 = new byte[socketv4.AddressFamily == AddressFamily.InterNetworkV6
                ? NativeSocket.IPv6AddrSize : NativeSocket.IPv4AddrSize];
            byte[] addrBuffer6 = new byte[NativeSocket.IPv6AddrSize];
            var tempEndPoint = new IPEndPoint(IPAddress.Any, 0);
            var selectReadList = new List<Socket>(2);
            var packet = PoolGetPacket(NetConstants.MaxPacketSize);

            // One recvmmsg per drain instead of one recvfrom per datagram. This thread is one
            // core's worth of syscalls and nothing behind it can go faster than that, so when the
            // inbound rate spikes — everyone talking at once, a join storm — the syscall count is
            // the wall, and the kernel discards what does not fit behind it. Costs a memcpy out of
            // the arena per datagram, the same trade the send batcher makes. Null where the kernel
            // cannot take a vector, and then the per-datagram path below runs unchanged.
            var batch = NativeSocket.SupportsBatchRecv ? new RecvBatcher(NetConstants.MaxPacketSize) : null;

            while (_isRunning)
            {
                try
                {
                    if (socketV6 == null)
                    {
                        if (NativeReceiveFrom(socketHandle4, addrBuffer4) == false)
                            return;
                        continue;
                    }
                    bool messageReceived = false;
                    if (socketv4.Available != 0 || selectReadList.Contains(socketv4))
                    {
                        if (NativeReceiveFrom(socketHandle4, addrBuffer4) == false)
                            return;
                        messageReceived = true;
                    }
                    if (socketV6.Available != 0 || selectReadList.Contains(socketV6))
                    {
                        if (NativeReceiveFrom(socketHandle6, addrBuffer6) == false)
                            return;
                        messageReceived = true;
                    }

                    selectReadList.Clear();

                    if (messageReceived)
                        continue;

                    selectReadList.Add(socketv4);
                    selectReadList.Add(socketV6);

                    Socket.Select(selectReadList, null, null, ReceivePollingTime);
                }
                catch (SocketException ex)
                {
                    if (ProcessError(ex))
                        return;
                }
                catch (ObjectDisposedException)
                {
                    //socket closed
                    return;
                }
                catch (ThreadAbortException)
                {
                    //thread closed
                    return;
                }
                catch (Exception e)
                {
                    //protects socket receive thread
                    NetDebug.WriteError("[NM] SocketReceiveThread error: " + e);
                }
            }

            bool NativeReceiveFrom(IntPtr s, byte[] address)
            {
                if (batch != null)
                    return NativeReceiveBatch(s, address);

                int addrSize = address.Length;
                packet.Size = NativeSocket.RecvFrom(s, packet.RawData, NetConstants.MaxPacketSize, address, ref addrSize);
                if (packet.Size == 0)
                    return true; //socket closed or empty packet

                if (packet.Size == -1)
                {
                    //Linux timeout EAGAIN
                    return ProcessError(new SocketException((int)NativeSocket.GetSocketError())) == false;
                }

                ////NetDebug.WriteForce($"[R]Received data from {endPoint}, result: {packet.Size}");
                Dispatch(address);
                return true;
            }

            bool NativeReceiveBatch(IntPtr s, byte[] address)
            {
                int count = batch.Receive(s);
                if (count == 0)
                    return true; //nothing was waiting

                if (count < 0)
                {
                    //Linux timeout EAGAIN, same as the single-datagram path
                    return ProcessError(new SocketException((int)NativeSocket.GetSocketError())) == false;
                }

                for (int i = 0; i < count; i++)
                {
                    // The arena is reused by the next call, so each datagram is copied out before
                    // it is dispatched — OnMessageReceived hands the packet on to threads that
                    // outlive this loop.
                    int size = batch.CopyTo(i, packet.RawData, address);
                    if (size <= 0)
                        continue;

                    packet.Size = size;
                    Dispatch(address);
                }
                return true;
            }

            // Decodes the sockaddr the kernel wrote into 'address' onto the reusable temp
            // endpoint, hands the packet on, and leaves 'packet' pointing at a fresh one.
            void Dispatch(byte[] address)
            {
                short family = (short)((address[1] << 8) | address[0]);
                tempEndPoint.Port = (ushort)((address[2] << 8) | address[3]);
                if ((NativeSocket.UnixMode && family == NativeSocket.AF_INET6) || (!NativeSocket.UnixMode && (AddressFamily)family == AddressFamily.InterNetworkV6))
                {
                    uint scope = unchecked((uint)(
                        (address[27] << 24) +
                        (address[26] << 16) +
                        (address[25] << 8) +
                        (address[24])));
#if NETCOREAPP || NETSTANDARD2_1 || NETSTANDARD2_1_OR_GREATER
                    tempEndPoint.Address = new IPAddress(new ReadOnlySpan<byte>(address, 8, 16), scope);
#else
                    byte[] addrBuffer = new byte[16];
                    Buffer.BlockCopy(address, 8, addrBuffer, 0, 16);
                    tempEndPoint.Address = new IPAddress(addrBuffer, scope);
#endif
                }
                else //IPv4
                {
                    long ipv4Addr = unchecked((uint)((address[4] & 0x000000FF) |
                                                     (address[5] << 8 & 0x0000FF00) |
                                                     (address[6] << 16 & 0x00FF0000) |
                                                     (address[7] << 24)));
                    tempEndPoint.Address = new IPAddress(ipv4Addr);
                }

                if (TryGetPeer(tempEndPoint, out var peer))
                {
                    //use cached native ep
                    OnMessageReceived(packet, peer);
                }
                else
                {
                    OnMessageReceived(packet, tempEndPoint);
                    tempEndPoint = new IPEndPoint(IPAddress.Any, 0);
                }
                packet = PoolGetPacket(NetConstants.MaxPacketSize);
            }
        }

        /// <summary>Receives one datagram and returns its size, so callers can track a drain budget.</summary>
        private int ReceiveFrom(Socket s, ref EndPoint bufferEndPoint)
        {
            var packet = PoolGetPacket(NetConstants.MaxPacketSize);
#if NET8_0_OR_GREATER
            var sockAddr = GetSockAddrCache(s.AddressFamily);
            packet.Size = s.ReceiveFrom(packet, SocketFlags.None, sockAddr);
            int size = packet.Size;
            OnMessageReceived(packet, TryGetPeer(sockAddr, out var peer) ? peer : (IPEndPoint)bufferEndPoint.Create(sockAddr));
            return size;
#else
            packet.Size = s.ReceiveFrom(packet.RawData, 0, NetConstants.MaxPacketSize, SocketFlags.None, ref bufferEndPoint);
            int size = packet.Size;
            OnMessageReceived(packet, (IPEndPoint)bufferEndPoint);
            return size;
#endif
        }

        private void ReceiveLogic() => ReceiveLogicForSocketPair(_udpSocketv4, _udpSocketv6);

        private void ReceiveLogicForSocketPair(Socket socketv4, Socket socketV6)
        {
            // Per-thread endpoint instances so multi-socket extras don't alias the primary
            // socket's templates. Socket.ReceiveFrom replaces the ref rather than mutating
            // the original, but allocating per-thread keeps this fact local to each loop.
            EndPoint bufferEndPoint4 = new IPEndPoint(IPAddress.Any, 0);
            EndPoint bufferEndPoint6 = new IPEndPoint(IPAddress.IPv6Any, 0);
            var selectReadList = new List<Socket>(2);

            while (_isRunning)
            {
                //Reading data
                try
                {
                    if (socketV6 == null)
                    {
                        if (socketv4.Available == 0 && !socketv4.Poll(ReceivePollingTime, SelectMode.SelectRead))
                            continue;
                        if (socketv4.AddressFamily == AddressFamily.InterNetworkV6)
                            ReceiveFrom(socketv4, ref bufferEndPoint6);
                        else
                            ReceiveFrom(socketv4, ref bufferEndPoint4);
                    }
                    else
                    {
                        bool messageReceived = false;
                        if (socketv4.Available != 0 || selectReadList.Contains(socketv4))
                        {
                            ReceiveFrom(socketv4, ref bufferEndPoint4);
                            messageReceived = true;
                        }
                        if (socketV6.Available != 0 || selectReadList.Contains(socketV6))
                        {
                            ReceiveFrom(socketV6, ref bufferEndPoint6);
                            messageReceived = true;
                        }

                        selectReadList.Clear();

                        if (messageReceived)
                            continue;

                        selectReadList.Add(socketv4);
                        selectReadList.Add(socketV6);
                        Socket.Select(selectReadList, null, null, ReceivePollingTime);
                    }
                    ////NetDebug.Write(NetLogLevel.Trace, $"[R]Received data from {bufferEndPoint}, result: {packet.Size}");
                }
                catch (SocketException ex)
                {
                    if (ProcessError(ex))
                        return;
                }
                catch (ObjectDisposedException)
                {
                    //socket closed
                    return;
                }
                catch (ThreadAbortException)
                {
                    //thread closed
                    return;
                }
                catch (Exception e)
                {
                    //protects socket receive thread
                    NetDebug.WriteError("[NM] SocketReceiveThread error: " + e);
                }
            }
        }

        /// <summary>
        /// Start logic thread and listening on selected port
        /// </summary>
        /// <param name="addressIPv4">bind to specific ipv4 address</param>
        /// <param name="addressIPv6">bind to specific ipv6 address</param>
        /// <param name="port">port to listen</param>
        /// <param name="manualMode">mode of library</param>
        public bool Start(IPAddress addressIPv4, IPAddress addressIPv6, int port, bool manualMode)
        {
            if (IsRunning && NotConnected == false)
                return false;

            NotConnected = false;
            _manualMode = manualMode;
            UseNativeSockets = UseNativeSockets && NativeSocket.IsSupported;
            // Seed the cached pool ceiling now that the sizing knobs are set; peer connect and
            // disconnect refresh it from then on.
            RecomputePoolCap();

            // SO_REUSEPORT multi-socket ingress: Linux-only. Decide here so the primary socket
            // also opts in (SO_REUSEPORT must be set on every socket sharing the port).
            int extraSocketCount = 0;
            if (MultiSocketCount > 1 || AllowSendSocketGrowth)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // SO_REUSEPORT stops the port refusing a second bind, so a second instance of
                    // this server pointed at the same port would quietly take a share of the
                    // inbound flows instead of failing on startup. Ask the exclusive question
                    // first, while it can still be asked; if something is already there, leave
                    // REUSEPORT off and let the real bind below report AddressAlreadyInUse exactly
                    // as it always has.
                    if (port == 0 || PortLooksFree(addressIPv4, port))
                    {
                        _useReusePort = true;
                        extraSocketCount = MultiSocketCount > 1 ? MultiSocketCount - 1 : 0;
                    }
                    else
                    {
                        NetDebug.WriteError(
                            $"[NM] Port {port} is already in use; binding a single socket without " +
                            "SO_REUSEPORT so the conflict is reported rather than shared.");
                    }
                }
                else if (MultiSocketCount > 1)
                {
                    // Windows has no SO_REUSEPORT load balancing: sockets sharing a UDP port there
                    // do not receive a share each. The alternatives both cost more than they are
                    // worth — several threads on one socket would let two datagrams from the same
                    // peer be processed at once (SO_REUSEPORT avoids that only because RSS pins a
                    // 4-tuple to one socket), and giving each peer its own port is a protocol
                    // change.
                    //
                    // ⚠️ Do not read the "receive is only ~2% of CPU" measurement as meaning this
                    // does not matter. That was taken on a single socket with fast cores, where one
                    // receive thread kept up easily. A saturated receive thread does not show up as
                    // CPU at all — it shows up as the kernel discarding datagrams behind it — and
                    // on hosts with many weak cores one thread is the throughput wall. That is why
                    // Linux grows sockets on RcvbufErrors. Windows simply cannot.
                    NetDebug.WriteError($"[NM] MultiSocketCount={MultiSocketCount} requested but SO_REUSEPORT is Linux-only; falling back to single socket");
                }
            }

            _udpSocketv4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (!BindSocket(_udpSocketv4, new IPEndPoint(addressIPv4, port)))
                return false;

            LocalPort = ((IPEndPoint)_udpSocketv4.LocalEndPoint).Port;

#if UNITY_SOCKET_FIX
            if (_useSocketFix && _pausedSocketFix == null)
                _pausedSocketFix = new PausedSocketFix(this, addressIPv4, addressIPv6, port, manualMode);
#endif

            _isRunning = true;
            if (_manualMode)
            {
                _bufferEndPointv4 = new IPEndPoint(IPAddress.Any, 0);
            }

            //Check IPv6 support
            if (IPv6Support && IPv6Enabled)
            {
                _udpSocketv6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                //Use one port for two sockets
                if (BindSocket(_udpSocketv6, new IPEndPoint(addressIPv6, LocalPort)))
                {
                    if (_manualMode)
                        _bufferEndPointv6 = new IPEndPoint(IPAddress.IPv6Any, 0);
                }
                else
                {
                    _udpSocketv6 = null;
                }
            }

            RebuildSendSockets();

            if (!manualMode)
            {
                // One thread per socket, each blocking in recvfrom on its own, rather than one
                // thread multiplexing both with Socket.Select.
                //
                // Select was costing far more than the readiness answer is worth: it rebuilds
                // native fd structures from the List<Socket> on every call, and the loop reached it
                // once per received datagram, on top of an ioctlsocket per socket per iteration for
                // Socket.Available. Measured at 1000 players it was ~4% of server CPU in managed
                // marshalling plus ~1% in Available, to decide something a blocking recvfrom
                // answers for free.
                //
                // Safe because the sockets carry ReceiveTimeout = 500 ms: a blocked thread returns
                // with SocketError.TimedOut, which ProcessError treats as non-fatal, so the loop
                // re-tests _isRunning twice a second and CloseSocket's Join still completes. That is
                // the same mechanism the v6-disabled path has always relied on; shutdown latency
                // goes from the 50 ms poll interval to that 500 ms timeout.
                Socket primaryV4 = _udpSocketv4;
                Socket primaryV6 = _udpSocketv6;
                _receiveThread = StartReceiveThread(primaryV4, null, $"ReceiveThread({LocalPort})");
                if (primaryV6 != null)
                {
                    _receiveThreadV6 = StartReceiveThread(primaryV6, null, $"ReceiveThread({LocalPort}v6)");
                }
                if (_logicThread == null)
                {
                    _logicThread = new Thread(UpdateLogic) { Name = "LogicThread", IsBackground = true };
                    _logicThread.Start();
                }

                // Spawn extra SO_REUSEPORT sockets + receive threads. Each gets its own v4
                // (and v6 if enabled) socket bound to the same port. The Linux kernel hashes
                // inbound 4-tuples across them — per-peer order preserved, single-recv-thread
                // pps wall lifted.
                // Only if the primary actually got SO_REUSEPORT. Binding a second socket to a port
                // without it cannot succeed — it fails with AddressAlreadyInUse and prints a bind
                // exception that reads like a crash. The flag is cleared by BindSocket above, which
                // runs after extraSocketCount was decided, so it has to be re-checked here.
                if (!_useReusePort) extraSocketCount = 0;

                // How many send paths there actually are, as opposed to how many were asked for.
                // The host sizes its send worker pool from this, and a pool sized for sockets that
                // failed to bind would be sized for capacity that does not exist.
                BoundSendSocketCount = 1;

                _reusePortAddressV4 = addressIPv4;
                _reusePortAddressV6 = addressIPv6;

                for (int i = 0; i < extraSocketCount; i++)
                {
                    if (!TryAddSendSocket()) break;
                }
            }

            return true;
        }

        private static bool _socketBufferClampWarned;

        /// <summary>
        /// Says so when the OS did not hand over the buffer it was asked for.
        ///
        /// Linux clamps SO_RCVBUF and SO_SNDBUF to net.core.rmem_max / net.core.wmem_max — a couple
        /// of hundred KB by default, against the 32 MB asked for here — and setsockopt succeeds
        /// anyway. Nothing in the process can tell without reading the value back, so what a clamp
        /// looks like from in here is the kernel discarding datagrams under load for no visible
        /// reason. Read once, on the first socket bound; every socket gets the same treatment from
        /// the same sysctl.
        /// </summary>
        private static void WarnIfSocketBuffersWereClamped(Socket socket)
        {
            if (_socketBufferClampWarned) return;

            try
            {
                int receive = socket.ReceiveBufferSize;
                int send = socket.SendBufferSize;
                if (receive <= 0 || send <= 0) return;
                if (receive >= NetConstants.SocketBufferSize && send >= NetConstants.SocketBufferSize) return;

                _socketBufferClampWarned = true;
                NetDebug.WriteError(
                    $"[NM] The OS clamped the socket buffers: asked for {NetConstants.SocketBufferSize / (1024 * 1024)} MB, " +
                    $"got {receive / 1024} KB receive / {send / 1024} KB send. On Linux raise net.core.rmem_max and " +
                    "net.core.wmem_max in /etc/sysctl.d and restart; setsockopt reports success either way, so this " +
                    "line is the only place it shows up. Left alone, the kernel drops inbound datagrams under load.");
            }
            catch
            {
                // Reading the value back is a diagnostic. It is never a reason to fail a bind.
            }
        }

        /// <summary>
        /// Whether nothing is listening on this address and port yet.
        ///
        /// Binds and immediately closes a throwaway socket with no reuse options set. A fresh
        /// socket carries neither SO_REUSEADDR nor SO_REUSEPORT, and Linux refuses that bind if any
        /// socket holds the port — including one in a REUSEPORT group, since every member of a
        /// group has to set the option and this one does not. So a failure here means a listener is
        /// already there.
        ///
        /// Inherently a race: something could take the port in the gap before the real socket
        /// binds. The case worth catching is an instance that is already running, and that one it
        /// catches.
        ///
        /// v4 only. What this is for is a duplicate instance, and a duplicate binds both families;
        /// a v6 collision on its own is already handled by degrading to v4.
        /// </summary>
        private static bool PortLooksFree(IPAddress address, int port)
        {
            Socket probe = null;
            try
            {
                probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Bind(new IPEndPoint(address, port));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (Exception)
            {
                // Anything else — a sandbox that refuses the bind for its own reasons — is not
                // evidence the port is taken, and treating it as such would silently remove the
                // capability. Say free; the real bind is still the authority.
                return true;
            }
            finally
            {
                probe?.Dispose();
            }
        }

        private bool BindSocket(Socket socket, IPEndPoint ep)
        {
            //Setup socket
            socket.ReceiveTimeout = 500;
            socket.SendTimeout = 500;
            socket.ReceiveBufferSize = NetConstants.SocketBufferSize;
            socket.SendBufferSize = NetConstants.SocketBufferSize;
            WarnIfSocketBuffersWereClamped(socket);

            // Manual mode drains the socket from the caller's loop rather than a dedicated receive
            // thread, so it needs a way to learn "nothing left" that does not block. A blocking
            // socket can only answer that by being asked separately — Socket.Available, a syscall
            // per question — and at 4000 manual-mode clients that question was 34% of the process.
            // Non-blocking lets the receive itself say so by failing with WouldBlock, which the
            // native path reports as a return code instead of an exception.
            //
            // Only when the native path is actually available: the managed ReceiveFrom throws on
            // WouldBlock, and an exception per empty poll would be far worse than the syscall.
            socket.Blocking = !(_manualMode && UseNativeSockets && NativeSocket.IsSupported);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    socket.IOControl(SioUdpConnreset, new byte[] { 0 }, null);
                }
                catch
                {
                    //ignored
                }
            }

            try
            {
                // SO_REUSEPORT needs ReuseAddress too on most BSDs / Linux for port sharing.
                bool wantReuse = ReuseAddress || _useReusePort;
                socket.ExclusiveAddressUse = !wantReuse;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, wantReuse);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontRoute, DontRoute);
                if (_useReusePort && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Set MUST happen before bind. Enables the kernel to hash inbound 4-tuples
                    // across all sockets sharing the port, giving one receive thread per socket
                    // without app-level load balancing.
                    //
                    // Goes through libc rather than SetSocketOption: .NET's Unix layer maps option
                    // names through a table of ones it knows and rejects anything else with
                    // OperationNotSupported, so casting the raw constant could never work — it
                    // failed on every Linux host, silently falling back to a single socket.
                    if (!NativeSocket.TryEnableReusePort(socket.Handle, out int errno))
                    {
                        NetDebug.WriteError($"[NM] SO_REUSEPORT unavailable (errno {errno}); running a single socket.");
                        _useReusePort = false;
                    }
                }
            }
            catch
            {
                //Unity with IL2CPP throws an exception here, it doesn't matter in most cases so just ignore it
            }
            if (ep.AddressFamily == AddressFamily.InterNetwork)
            {
                Ttl = NetConstants.SocketTTL;

                if (BroadcastReceiveEnabled)
                {
                    try { socket.EnableBroadcast = true; }
                    catch (SocketException e)
                    {
                        NetDebug.WriteError($"[B]Broadcast error: {e.SocketErrorCode}");
                    }
                }

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    try { socket.DontFragment = true; }
                    catch (SocketException e)
                    {
                        NetDebug.WriteError($"[B]DontFragment error: {e.SocketErrorCode}");
                    }
                }
            }
            //Bind
            try
            {
                socket.Bind(ep);
                //NetDebug.Write(NetLogLevel.Trace, $"[B]Successfully binded to port: {((IPEndPoint)socket.LocalEndPoint).Port}, AF: {socket.AddressFamily}");

                //join multicast (only needed when broadcast receive is enabled)
                if (BroadcastReceiveEnabled && ep.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    try
                    {
#if !UNITY_SOCKET_FIX
                        socket.SetSocketOption(
                            SocketOptionLevel.IPv6,
                            SocketOptionName.AddMembership,
                            new IPv6MulticastOption(MulticastAddressV6));
#endif
                    }
                    catch (Exception)
                    {
                        // Unity3d throws exception - ignored
                    }
                }
            }
            catch (SocketException bindException)
            {
                switch (bindException.SocketErrorCode)
                {
                    //IPv6 bind fix
                    case SocketError.AddressAlreadyInUse:
                        if (socket.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            try
                            {
                                //Set IPv6Only
                                socket.DualMode = false;
                                socket.Bind(ep);
                            }
                            catch (SocketException ex)
                            {
                                //because its fixed in 2018_3
                                NetDebug.WriteError($"[B]Bind exception: {ex}, errorCode: {ex.SocketErrorCode}");
                                return false;
                            }
                            return true;
                        }
                        break;
                    //hack for iOS (Unity3D)
                    case SocketError.AddressFamilyNotSupported:
                        return true;
                }
                NetDebug.WriteError($"[B]Bind exception: {bindException}, errorCode: {bindException.SocketErrorCode}");
                return false;
            }
            return true;
        }

        internal int SendRawAndRecycle(NetPacket packet, IPEndPoint remoteEndPoint)
        {
            int result = SendRaw(packet.RawData, 0, packet.Size, remoteEndPoint);
            PoolRecycle(packet);
            return result;
        }

        internal int SendRaw(NetPacket packet, IPEndPoint remoteEndPoint)
        {
            return SendRaw(packet.RawData, 0, packet.Size, remoteEndPoint);
        }

        /// <summary>
        /// The batcher owned by the calling thread, or null when this thread is not inside a
        /// batching window. Only <see cref="UpdateLogic"/>'s parallel peer pass opens one, so
        /// every other send path is unaffected.
        /// </summary>
        [ThreadStatic] private static SendBatcher t_batcher;

        /// <summary>
        /// This thread's idle batcher, kept between windows. A batcher owns ~390 KB of unmanaged
        /// arenas; allocating and freeing that per window meant five AllocHGlobal/FreeHGlobal pairs
        /// hundreds of times a second per worker — native-heap churn the managed profiler never
        /// shows, plus one finalizable object per window. A retired thread's cached batcher is
        /// reclaimed by its finalizer.
        /// </summary>
        [ThreadStatic] private static SendBatcher t_batcherCache;

        /// <summary>True while the calling thread is inside a batching window.</summary>
        internal static bool IsBatchingOnThisThread => t_batcher != null;

        /// <summary>
        /// Whether opening a batch is worth it. Batching buys one syscall per batch instead of one
        /// per datagram — but only where the kernel can actually take a vector of datagrams.
        /// Without that the batch is a pure loss: every datagram pays an extra arena copy and the
        /// syscall count is unchanged. Measured at 500 players on Windows, batching cost +1.3
        /// cores for nothing, so there it stays off and the direct path is used unchanged.
        /// </summary>
        internal static bool BatchSendWorthwhile => NativeSocket.SupportsBatchSend;

        internal static SendBatcher BeginBatch(NetManager owner)
        {
            if (!BatchSendWorthwhile || !owner.UseNativeSockets) return null;

            var batcher = t_batcherCache ?? new SendBatcher();
            t_batcherCache = null;
            batcher.Owner = owner;
            t_batcher = batcher;
            return batcher;
        }

        internal static void EndBatch(SendBatcher batcher)
        {
            if (batcher == null) return;
            batcher.Flush();
            batcher.Owner = null;
            t_batcher = null;
            t_batcherCache = batcher;
        }

        /// <summary>
        /// Send that may be deferred into this thread's batch.
        ///
        /// Only for fire-and-forget payload traffic: the return value is optimistic (the datagram
        /// is queued, not yet on the wire), so callers that infer anything from it — MTU probing
        /// keys off a MessageSize failure, for instance — must keep using <see cref="SendRaw"/>.
        /// Falls through to the direct path whenever batching does not apply, so behaviour is
        /// identical when it is unavailable.
        /// </summary>
        internal int SendRawBatchable(byte[] message, int start, int length, NetPeer peer)
        {
            var batcher = t_batcher;
            if (batcher != null &&
                ReferenceEquals(batcher.Owner, this) &&
                _isRunning &&
                _extraPacketLayer == null &&   // CRC/encryption layers rewrite the buffer in place
                UseNativeSockets &&
                peer.NativeAddress != null)
            {
                var socket = PickSendSocket(peer.AddressFamily == AddressFamily.InterNetworkV6 && IPv6Support);

                if (socket != null && batcher.TryAdd(socket.Handle, message, start, length, peer.NativeAddress))
                {
                    return length;
                }
            }

            return SendRaw(message, start, length, peer);
        }

        internal int SendRaw(byte[] message, int start, int length, IPEndPoint remoteEndPoint)
        {
            if (!_isRunning)
                return 0;

            NetPacket expandedPacket = null;
            if (_extraPacketLayer != null)
            {
                expandedPacket = PoolGetPacket(length + _extraPacketLayer.ExtraPacketSizeForLayer);
                Buffer.BlockCopy(message, start, expandedPacket.RawData, 0, length);
                start = 0;
                _extraPacketLayer.ProcessOutBoundPacket(ref remoteEndPoint, ref expandedPacket.RawData, ref start, ref length);
                message = expandedPacket.RawData;
            }

            var socket = PickSendSocket(remoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6 && IPv6Support);
            if (socket == null)
                return 0;

            int result;
            try
            {
                if (UseNativeSockets && remoteEndPoint is NetPeer peer)
                {
                    unsafe
                    {
                        fixed (byte* dataWithOffset = &message[start])
                            result = NativeSocket.SendTo(socket.Handle, dataWithOffset, length, peer.NativeAddress, peer.NativeAddress.Length);
                    }
                    if (result == -1)
                        throw NativeSocket.GetSocketException();
                }
                else
                {
#if NET8_0_OR_GREATER
                    result = socket.SendTo(new ReadOnlySpan<byte>(message, start, length), SocketFlags.None, remoteEndPoint.Serialize());
#else
                    result = socket.SendTo(message, start, length, SocketFlags.None, remoteEndPoint);
#endif
                }
                ////NetDebug.WriteForce("[S]Send packet to {0}, result: {1}", remoteEndPoint, result);
            }
            catch (SocketException ex)
            {
                switch (ex.SocketErrorCode)
                {
                    case SocketError.NoBufferSpaceAvailable:
                    case SocketError.Interrupted:
                        return 0;
                    // Manual mode runs its socket non-blocking so receives can report the end of
                    // the queue; a full send buffer then surfaces here instead of blocking. Same
                    // outcome as NoBufferSpaceAvailable — the datagram is dropped, which is what
                    // unreliable delivery means. Logging it would spam under exactly the load that
                    // causes it.
                    case SocketError.WouldBlock:
                        return 0;
                    case SocketError.MessageSize:
                        //NetDebug.Write(NetLogLevel.Trace, $"[SRD] 10040, datalen: {length}");
                        return 0;

                    case SocketError.HostUnreachable:
                    case SocketError.NetworkUnreachable:
                        if (DisconnectOnUnreachable && remoteEndPoint is NetPeer peer)
                        {
                            DisconnectPeerForce(
                                peer,
                                ex.SocketErrorCode == SocketError.HostUnreachable
                                    ? DisconnectReason.HostUnreachable
                                    : DisconnectReason.NetworkUnreachable,
                                ex.SocketErrorCode,
                                null);
                        }

                        CreateEvent(NetEvent.EType.Error, remoteEndPoint: remoteEndPoint, errorCode: ex.SocketErrorCode);
                        return -1;

                    case SocketError.Shutdown:
                        CreateEvent(NetEvent.EType.Error, remoteEndPoint: remoteEndPoint, errorCode: ex.SocketErrorCode);
                        return -1;

                    default:
                        NetDebug.WriteError($"[S] {ex}");
                        return -1;
                }
            }
            catch (Exception ex)
            {
                NetDebug.WriteError($"[S] {ex}");
                return 0;
            }
            finally
            {
                if (expandedPacket != null)
                    PoolRecycle(expandedPacket);
            }

            if (result <= 0)
                return 0;

            if (EnableStatistics)
            {
                Statistics.IncrementPacketsSent();
                Statistics.AddBytesSent(length);
            }

            return result;
        }

        public bool SendBroadcast(NetDataWriter writer, int port)
        {
            return SendBroadcast(writer.Data, 0, writer.Length, port);
        }

        public bool SendBroadcast(byte[] data, int port)
        {
            return SendBroadcast(data, 0, data.Length, port);
        }

        public bool SendBroadcast(byte[] data, int start, int length, int port)
        {
            if (!IsRunning)
                return false;

            NetPacket packet;
            if (_extraPacketLayer != null)
            {
                var headerSize = NetPacket.GetHeaderSize(PacketProperty.Broadcast);
                packet = PoolGetPacket(headerSize + length + _extraPacketLayer.ExtraPacketSizeForLayer);
                packet.Property = PacketProperty.Broadcast;
                Buffer.BlockCopy(data, start, packet.RawData, headerSize, length);
                var checksumComputeStart = 0;
                int preCrcLength = length + headerSize;
                IPEndPoint emptyEp = null;
                _extraPacketLayer.ProcessOutBoundPacket(ref emptyEp, ref packet.RawData, ref checksumComputeStart, ref preCrcLength);
            }
            else
            {
                packet = PoolGetWithData(PacketProperty.Broadcast, data, start, length);
            }

            bool broadcastSuccess = false;
            bool multicastSuccess = false;
            try
            {
                broadcastSuccess = _udpSocketv4.SendTo(
                    packet.RawData,
                    0,
                    packet.Size,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Broadcast, port)) > 0;

                if (_udpSocketv6 != null)
                {
                    multicastSuccess = _udpSocketv6.SendTo(
                        packet.RawData,
                        0,
                        packet.Size,
                        SocketFlags.None,
                        new IPEndPoint(MulticastAddressV6, port)) > 0;
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.HostUnreachable)
                    return broadcastSuccess;
                NetDebug.WriteError($"[S][MCAST] {ex}");
                return broadcastSuccess;
            }
            catch (Exception ex)
            {
                NetDebug.WriteError($"[S][MCAST] {ex}");
                return broadcastSuccess;
            }
            finally
            {
                PoolRecycle(packet);
            }

            return broadcastSuccess || multicastSuccess;
        }

        private void CloseSocket()
        {
            _isRunning = false;
            if (_receiveThread != null && _receiveThread != Thread.CurrentThread)
                _receiveThread.Join();
            _receiveThread = null;
            if (_receiveThreadV6 != null && _receiveThreadV6 != Thread.CurrentThread)
                _receiveThreadV6.Join();
            _receiveThreadV6 = null;

            foreach (var t in _extraReceiveThreads)
            {
                if (t != null && t != Thread.CurrentThread) t.Join();
            }
            _extraReceiveThreads.Clear();
            foreach (var s in _extraSocketsV4) s?.Close();
            foreach (var s in _extraSocketsV6) s?.Close();
            _extraSocketsV4.Clear();
            _extraSocketsV6.Clear();
            _useReusePort = false;

            _udpSocketv4?.Close();
            _udpSocketv6?.Close();
            _udpSocketv4 = null;
            _udpSocketv6 = null;
            RebuildSendSockets();
        }
    }

    /// <summary>
    /// Takes inbound datagrams from the kernel a vector at a time for one receive thread.
    ///
    /// The mirror of <see cref="SendBatcher"/>, and it exists for the mirrored reason: a receive
    /// thread spends its life in a syscall, and one datagram per call is a hard pps ceiling that
    /// never appears as CPU — the thread is pinned either way and the kernel simply discards what
    /// backs up behind it. <c>recvmmsg</c> takes whatever is already queued in one call, so a burst
    /// costs one transition instead of one per datagram.
    ///
    /// One instance per receive thread, so no synchronisation. Memory is unmanaged for the same
    /// reason the send side's is: the kernel writes into these buffers, so they must not move.
    /// </summary>
    internal sealed unsafe class RecvBatcher : IDisposable
    {
        /// <summary>
        /// Datagrams per call. Deep enough that a burst is one syscall rather than dozens, shallow
        /// enough that the arena is under 100 KB per receive thread.
        /// </summary>
        public const int MaxEntries = 64;

        private const int MaxAddressSize = NativeSocket.IPv6AddrSize;

        private readonly int _slotSize;
        private readonly byte* _arena;
        private readonly byte* _addresses;
        private readonly NativeSocket.MmsgHdr* _headers;
        private readonly NativeSocket.IoVec* _iovecs;
        private bool _disposed;

        public RecvBatcher(int slotSize)
        {
            _slotSize = slotSize;
            _arena = (byte*)Marshal.AllocHGlobal(_slotSize * MaxEntries);
            _addresses = (byte*)Marshal.AllocHGlobal(MaxAddressSize * MaxEntries);
            _headers = (NativeSocket.MmsgHdr*)Marshal.AllocHGlobal(sizeof(NativeSocket.MmsgHdr) * MaxEntries);
            _iovecs = (NativeSocket.IoVec*)Marshal.AllocHGlobal(sizeof(NativeSocket.IoVec) * MaxEntries);
        }

        /// <summary>
        /// Blocks for the first datagram, then takes whatever else is already queued. Returns how
        /// many arrived, or -1 with errno set — including the socket's receive timeout, which is
        /// what lets a receive thread notice the manager has stopped.
        /// </summary>
        public int Receive(IntPtr socketHandle)
        {
            // Rebuilt every call: the kernel writes back into NameLen and Len, and a header left
            // holding the last call's lengths would report a datagram that did not arrive.
            for (int i = 0; i < MaxEntries; i++)
            {
                _iovecs[i].Base = _arena + (long)i * _slotSize;
                _iovecs[i].Length = (IntPtr)_slotSize;

                _headers[i] = default;
                _headers[i].Hdr.Name = _addresses + (long)i * MaxAddressSize;
                _headers[i].Hdr.NameLen = MaxAddressSize;
                _headers[i].Hdr.Iov = &_iovecs[i];
                _headers[i].Hdr.IovLen = (IntPtr)1;
            }

            return NativeSocket.RecvBatch(socketHandle, _headers, MaxEntries);
        }

        /// <summary>
        /// Copies one received datagram and its source address out of the arena, returning the
        /// datagram's length or 0 if there is nothing usable at that index. Copied rather than
        /// handed out by reference because the next call overwrites the arena while the packet is
        /// still travelling through the pipeline.
        /// </summary>
        public int CopyTo(int index, byte[] destination, byte[] address)
        {
            int length = (int)_headers[index].Len;
            if (length <= 0 || length > _slotSize || length > destination.Length)
                return 0;

            Marshal.Copy((IntPtr)(_arena + (long)index * _slotSize), destination, 0, length);
            Marshal.Copy((IntPtr)(_addresses + (long)index * MaxAddressSize), address, 0, address.Length);
            return length;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Marshal.FreeHGlobal((IntPtr)_arena);
            Marshal.FreeHGlobal((IntPtr)_addresses);
            Marshal.FreeHGlobal((IntPtr)_headers);
            Marshal.FreeHGlobal((IntPtr)_iovecs);
            GC.SuppressFinalize(this);
        }

        ~RecvBatcher() => Dispose();
    }

    /// <summary>
    /// Collects outbound datagrams for one worker thread and hands them to the kernel together.
    ///
    /// A broadcast server's send cost is dominated by syscall count, not bytes: at 500 players the
    /// send loop issues ~165K datagrams/s, and the per-call transition costs more than building
    /// the packet did. Copying each datagram into an arena and flushing the arena with one
    /// <c>sendmmsg</c> trades a ~700-byte memcpy for a syscall, which is a large win on Linux and
    /// a small one elsewhere (the loop still costs one call per datagram, but pinning, exception
    /// frames and endpoint dispatch are paid once per batch).
    ///
    /// One instance per worker thread, so no synchronisation. Memory is unmanaged: the pointers
    /// handed to the kernel must stay put, and pinning a GC array for the lifetime of the server
    /// would fragment the heap it lives on.
    /// </summary>
    internal sealed unsafe class SendBatcher : IDisposable
    {
        /// <summary>
        /// Datagrams per flush. Deep enough that a tick's worth of sends for one worker usually
        /// fits in one or two syscalls; shallow enough that the arena stays a few hundred KB and
        /// a flush never stalls the socket for long.
        /// </summary>
        public const int MaxEntries = 256;

        private readonly int _slotSize;
        private readonly byte* _arena;
        private readonly byte* _addresses;
        private readonly NativeSocket.BatchEntry* _entries;
        private readonly NativeSocket.MmsgHdr* _headers;
        private readonly NativeSocket.IoVec* _iovecs;

        // Largest sockaddr we can be asked to hold (sockaddr_in6).
        private const int MaxAddressSize = NativeSocket.IPv6AddrSize;

        private int _count;
        private long _bytes;
        private IntPtr _socket;
        private bool _disposed;

        /// <summary>The manager this batcher is bound to for the current flush window.</summary>
        public NetManager Owner;

        public int Count => _count;

        public SendBatcher()
        {
            _slotSize = NetConstants.MaxPacketSize + NetConstants.MaxUdpHeaderSize;
            _arena = (byte*)Marshal.AllocHGlobal(_slotSize * MaxEntries);
            _addresses = (byte*)Marshal.AllocHGlobal(MaxAddressSize * MaxEntries);
            _entries = (NativeSocket.BatchEntry*)Marshal.AllocHGlobal(sizeof(NativeSocket.BatchEntry) * MaxEntries);
            _headers = (NativeSocket.MmsgHdr*)Marshal.AllocHGlobal(sizeof(NativeSocket.MmsgHdr) * MaxEntries);
            _iovecs = (NativeSocket.IoVec*)Marshal.AllocHGlobal(sizeof(NativeSocket.IoVec) * MaxEntries);
        }

        /// <summary>
        /// Queues one datagram. Returns false when the caller must send it itself — an oversized
        /// payload, or an address that does not fit — so the caller always has a working path.
        /// Flushes automatically when the batch is full or the destination socket changes.
        /// </summary>
        public bool TryAdd(IntPtr socketHandle, byte[] data, int start, int length, byte[] address)
        {
            if (length <= 0 || length > _slotSize) return false;
            if (address == null || address.Length > MaxAddressSize) return false;

            // A batch belongs to exactly one socket; a peer on the other address family forces a
            // flush rather than being silently sent down the wrong one.
            if (_count > 0 && socketHandle != _socket) Flush();
            _socket = socketHandle;

            byte* slot = _arena + (long)_count * _slotSize;
            fixed (byte* src = &data[start]) Buffer.MemoryCopy(src, slot, _slotSize, length);

            byte* addrSlot = _addresses + (long)_count * MaxAddressSize;
            fixed (byte* srcAddr = address) Buffer.MemoryCopy(srcAddr, addrSlot, MaxAddressSize, address.Length);

            _entries[_count].Data = slot;
            _entries[_count].Length = length;
            _entries[_count].Address = addrSlot;
            _entries[_count].AddressLength = address.Length;
            _count++;
            _bytes += length;

            if (_count >= MaxEntries) Flush();
            return true;
        }

        /// <summary>Sends everything queued. Safe to call when empty.</summary>
        public void Flush()
        {
            if (_count == 0) return;

            int count = _count;
            long bytes = _bytes;
            _count = 0;
            _bytes = 0;

            int sent = NativeSocket.SendBatch(_socket, _entries, count, _headers, _iovecs);

            var owner = Owner;
            if (owner != null && owner.EnableStatistics && sent > 0)
            {
                // Whole batch in one pair of atomics rather than a pair per datagram. Bytes are
                // prorated on a short send: the exact split is not worth a second pass over the
                // entries for a counter that exists to be eyeballed.
                long countedBytes = sent == count ? bytes : bytes * sent / count;
                owner.Statistics.AddSentBatch(sent, countedBytes);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Marshal.FreeHGlobal((IntPtr)_arena);
            Marshal.FreeHGlobal((IntPtr)_addresses);
            Marshal.FreeHGlobal((IntPtr)_entries);
            Marshal.FreeHGlobal((IntPtr)_headers);
            Marshal.FreeHGlobal((IntPtr)_iovecs);
            GC.SuppressFinalize(this);
        }

        ~SendBatcher() => Dispose();
    }
}
