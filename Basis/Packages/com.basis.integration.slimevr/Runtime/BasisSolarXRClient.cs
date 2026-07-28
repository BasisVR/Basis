using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using Google.FlatBuffers;
using solarxr_protocol;
using solarxr_protocol.datatypes;
using solarxr_protocol.data_feed;
using solarxr_protocol.data_feed.device_data;
using solarxr_protocol.data_feed.tracker;
using solarxr_protocol.rpc;

namespace Basis.Integration.SlimeVR
{
    /// <summary>
    /// Body proportions as reported by SlimeVR's skeleton config.
    /// UserHeightMeters is SlimeVR's internal "user height": the standing floor-to-eye height
    /// (the sum of its NECK..LOWER_LEG height offsets), NOT the full head-to-floor height.
    /// </summary>
    public sealed class SlimeVRSkeletonConfig
    {
        public float UserHeightMeters;
        public readonly Dictionary<SkeletonBone, float> Parts = new Dictionary<SkeletonBone, float>();

        public bool TryGet(SkeletonBone bone, out float meters) => Parts.TryGetValue(bone, out meters);
    }

    /// <summary>
    /// A SlimeVR quaternion (x, y, z, w) in SlimeVR's own coordinate frame. Kept deliberately
    /// frame-agnostic: it is only ever compared against other SlimeVR quaternions (an angular
    /// delta), never mixed into Unity space, so no handedness conversion is needed or implied.
    /// </summary>
    public struct SlimeVRQuat
    {
        public float X, Y, Z, W;
    }

    /// <summary>A SlimeVR position (metres) in SlimeVR's own right-handed frame. Convert with
    /// BasisSlimeVRFrameAlign.FlipHandedness before using it in Unity space.</summary>
    public struct SlimeVRVec3
    {
        public float X, Y, Z;
    }

    /// <summary>One solved skeleton bone from SlimeVR's bones datafeed (only the head joint position is kept).</summary>
    public struct SlimeVRBone
    {
        public BodyPart BodyPart;
        public bool HasHeadPosition;
        public SlimeVRVec3 HeadPosition;
    }

    /// <summary>One SlimeVR tracker flattened together with its owning device's hardware status.</summary>
    public sealed class SlimeVRTrackerSnapshot
    {
        public string DeviceName;
        public string DisplayName;
        public string CustomName;
        public BodyPart BodyPart;
        public TrackerStatus Status;
        public bool IsHmd;
        public bool IsComputed;

        public bool HasBattery;
        public float BatteryPercent;
        public float BatteryVoltage;
        public bool HasRssi;
        public int RssiDbm;

        public bool HasDeviceId;
        public byte DeviceId;
        /// <summary>True for entries from the synthetic (solved/computed) tracker list.</summary>
        public bool IsSynthetic;

        /// <summary>True when this tracker is IMU-based — the trackers whose mounting orientation matters.</summary>
        public bool IsImu;

        /// <summary>
        /// SlimeVR's solved tracker position (metres, SlimeVR frame). Only populated while skeleton
        /// data is requested (<see cref="BasisSolarXRClient.EnableSkeletonData"/>); used as a frame
        /// correspondence against the same tracker's Basis pose.
        /// </summary>
        public bool HasPosition;
        public SlimeVRVec3 Position;

        /// <summary>SlimeVR's fused tracker rotation (SlimeVR frame). Populated only while skeleton/pose
        /// data is requested. Convert with BasisSlimeVRFrameAlign.FlipHandedness for Unity use.</summary>
        public bool HasRotation;
        public SlimeVRQuat Rotation;

        /// <summary>SlimeVR's persisted mounting orientation (the tracker's rotation as worn on the body).</summary>
        public bool HasMountingOrientation;
        public SlimeVRQuat MountingOrientation;

        /// <summary>
        /// Live mounting-reset orientation. SlimeVR recomputes this each run and it overrides
        /// <see cref="MountingOrientation"/> while present, so it is the value to trust.
        /// </summary>
        public bool HasMountingResetOrientation;
        public SlimeVRQuat MountingResetOrientation;

        /// <summary>
        /// The mounting orientation currently in effect (reset overrides persisted). Returns false
        /// when neither is known (non-IMU trackers, HMD).
        /// </summary>
        public bool TryGetEffectiveMounting(out SlimeVRQuat mounting)
        {
            if (HasMountingResetOrientation)
            {
                mounting = MountingResetOrientation;
                return true;
            }
            if (HasMountingOrientation)
            {
                mounting = MountingOrientation;
                return true;
            }
            mounting = default;
            return false;
        }
    }

    /// <summary>
    /// Which SolarXR transport <see cref="BasisSolarXRClient"/> uses. WebSocket works on every
    /// released server today; Pipe (Windows named pipe / unix socket elsewhere) is the transport
    /// SlimeVR keeps once websockets are deprecated, but needs a server whose pipe bridge
    /// actually responds (broken up to and including v20.1.0).
    /// </summary>
    public enum SolarXRTransportKind
    {
        WebSocket,
        Pipe
    }

    /// <summary>
    /// Talks to a local SlimeVR server over the SolarXR protocol, using the transport selected
    /// by <see cref="Transport"/>: the websocket on ws://127.0.0.1:21110 (all servers; one
    /// MessageBundle per binary message), or the RPC named pipe \\.\pipe\SlimeVRRpc on Windows /
    /// unix socket elsewhere (server v20.1+, framed as a 4-byte little-endian length prefix that
    /// includes itself + MessageBundle).
    /// Runs a background worker thread that connects, retries while the server is absent, and
    /// keeps a skeleton-config poll + datafeed alive. All callbacks fire ON THE WORKER THREAD.
    /// </summary>
    public sealed class BasisSolarXRClient : IDisposable
    {
        public const string DefaultEndpointName = "SlimeVRRpc";
        public const string DefaultWebSocketUrl = "ws://127.0.0.1:21110";

        public string EndpointName = DefaultEndpointName;
        public string WebSocketUrl = DefaultWebSocketUrl;
        public SolarXRTransportKind Transport = SolarXRTransportKind.WebSocket;
        public int ConnectTimeoutMs = 2000;
        public int ReconnectDelayMs = 5000;
        /// <summary>
        /// A healthy server answers the connect-time skeleton config request within milliseconds;
        /// if NOTHING arrives in this window the transport is considered mute and the connection
        /// is torn down and retried. On the pipe this catches the server-side bridge bug where
        /// \\.\pipe\SlimeVRRpc accepts connections and reads requests but every response is a
        /// zero-byte write (up to v20.1.0, WindowsNamedPipeRpcConnection.send sends
        /// bytes.remaining() == 0 after src.put(bytes) consumed it), and a log points the user
        /// at the WebSocket setting.
        /// </summary>
        public int FirstMessageTimeoutMs = 6000;
        /// <summary>Re-request the skeleton config at this cadence so SlimeVR-side recalibration (autobone, height calibration, manual edits) flows through.</summary>
        public int SkeletonConfigPollMs = 15000;
        public ushort DataFeedIntervalMs = 500;
        public bool EnableDataFeed = true;

        /// <summary>
        /// When true the datafeed also carries every tracker's solved position + rotation (and runs at
        /// <see cref="PoseFeedIntervalMs"/> instead of <see cref="DataFeedIntervalMs"/>), so Basis can
        /// source the trackers directly from the SlimeVR server. Off by default — pure status/metadata.
        /// </summary>
        public bool EnablePoseFeed;
        public ushort PoseFeedIntervalMs = 16;

        public Action<string> Log = _ => { };
        public Action<string> LogError = _ => { };

        // Worker-thread callbacks.
        public Action<bool> ConnectionChanged;
        public Action<SlimeVRSkeletonConfig> SkeletonConfigReceived;
        public Action<List<SlimeVRTrackerSnapshot>> TrackersReceived;

        public bool IsConnected { get; private set; }
        public bool IsRunning => _running;
        /// <summary>"pipe", "socket" or "websocket" while connected.</summary>
        public string TransportName { get; private set; }

        private volatile bool _running;
        private Thread _thread;
        private ISolarXRTransport _transport;
        private readonly object _writeLock = new object();
        private CancellationTokenSource _cts;
        private uint _txId;
        private readonly System.Diagnostics.Stopwatch _pollClock = System.Diagnostics.Stopwatch.StartNew();
        private long _lastSkeletonPollMs;
        private bool _warnedMutePipe;

        public void Start()
        {
            if (_running)
            {
                return;
            }
            _running = true;
            _cts = new CancellationTokenSource();
            _thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "BasisSlimeVR"
            };
            _thread.Start();
        }

        public void Stop()
        {
            if (!_running)
            {
                return;
            }
            _running = false;
            _cts?.Cancel();
            CloseTransport();
        }

        public void Dispose() => Stop();

        public void RequestSkeletonConfig()
        {
            TrySend(BuildRpc(b =>
            {
                SkeletonConfigRequest.StartSkeletonConfigRequest(b);
                return (RpcMessage.SkeletonConfigRequest, SkeletonConfigRequest.EndSkeletonConfigRequest(b).Value);
            }));
        }

        /// <summary>Ask SlimeVR to run one of its resets (Yaw / Full / Mounting), same as the tray or GUI buttons.</summary>
        public void RequestReset(ResetType type)
        {
            TrySend(BuildRpc(b =>
            {
                ResetRequest.StartResetRequest(b);
                ResetRequest.AddResetType(b, type);
                return (RpcMessage.ResetRequest, ResetRequest.EndResetRequest(b).Value);
            }));
        }

        private void WorkerLoop()
        {
            var token = _cts.Token;
            while (_running)
            {
                try
                {
                    _transport = Connect(token);
                    IsConnected = true;
                    Log($"Connected to SlimeVR over {TransportName}");
                    ConnectionChanged?.Invoke(true);

                    RequestSkeletonConfig();
                    _lastSkeletonPollMs = _pollClock.ElapsedMilliseconds;
                    if (EnableDataFeed)
                    {
                        TrySend(BuildStartDataFeed());
                    }

                    ReadLoop(token);
                }
                catch (Exception e)
                {
                    if (_running && IsConnected)
                    {
                        Log($"SlimeVR connection lost: {e.Message}");
                    }
                }
                finally
                {
                    CloseTransport();
                    if (IsConnected)
                    {
                        IsConnected = false;
                        try
                        {
                            ConnectionChanged?.Invoke(false);
                        }
                        catch (Exception e)
                        {
                            LogError($"SlimeVR ConnectionChanged handler failed: {e}");
                        }
                    }
                }

                if (_running)
                {
                    token.WaitHandle.WaitOne(ReconnectDelayMs);
                }
            }
        }

        private ISolarXRTransport Connect(CancellationToken token)
        {
            return Transport == SolarXRTransportKind.Pipe ? ConnectNative() : ConnectWebSocket(token);
        }

        private ISolarXRTransport ConnectNative()
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                var pipe = new NamedPipeClientStream(".", EndpointName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    pipe.Connect(ConnectTimeoutMs);
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
                TransportName = "pipe";
                return new LengthPrefixedStreamTransport(pipe);
            }

            string dir = Environment.GetEnvironmentVariable("SLIMEVR_SOCKET_DIR")
                ?? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
                ?? Path.GetTempPath();
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Connect(new UnixDomainSocketEndPoint(Path.Combine(dir, EndpointName)));
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            TransportName = "socket";
            return new LengthPrefixedStreamTransport(new NetworkStream(socket, ownsSocket: true));
        }

        private ISolarXRTransport ConnectWebSocket(CancellationToken token)
        {
            var webSocket = new ClientWebSocket();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ConnectTimeoutMs);
                webSocket.ConnectAsync(new Uri(WebSocketUrl), timeout.Token).GetAwaiter().GetResult();
            }
            catch
            {
                webSocket.Dispose();
                throw;
            }
            TransportName = "websocket";
            return new WebSocketTransport(webSocket);
        }

        private void ReadLoop(CancellationToken token)
        {
            bool receivedAnything = false;
            while (_running && !token.IsCancellationRequested)
            {
                ISolarXRTransport transport = _transport ?? throw new IOException("Not connected");
                byte[] frame;
                try
                {
                    frame = transport.ReadMessage(receivedAnything ? 0 : FirstMessageTimeoutMs);
                }
                catch (TimeoutException)
                {
                    if (!receivedAnything && TransportName != "websocket" && !_warnedMutePipe)
                    {
                        _warnedMutePipe = true;
                        Log($"SlimeVR accepted the {TransportName} connection but never responded — the server's pipe bridge is broken (all builds up to v20.1.0); update the server or set the SlimeVR connection method to WebSocket");
                    }
                    throw;
                }
                receivedAnything = true;
                try
                {
                    ParseBundle(frame);
                }
                catch (Exception e)
                {
                    // A malformed bundle should not kill the connection.
                    LogError($"SlimeVR message parse failed: {e}");
                }

                if (_pollClock.ElapsedMilliseconds - _lastSkeletonPollMs >= SkeletonConfigPollMs)
                {
                    _lastSkeletonPollMs = _pollClock.ElapsedMilliseconds;
                    RequestSkeletonConfig();
                }
            }
        }

        private void ParseBundle(byte[] frame)
        {
            var bundle = MessageBundle.GetRootAsMessageBundle(new ByteBuffer(frame));

            for (int i = 0; i < bundle.RpcMsgsLength; i++)
            {
                var header = bundle.RpcMsgs(i);
                if (!header.HasValue)
                {
                    continue;
                }
                HandleRpc(header.Value);
            }

            for (int i = 0; i < bundle.DataFeedMsgsLength; i++)
            {
                var header = bundle.DataFeedMsgs(i);
                if (!header.HasValue || header.Value.MessageType != DataFeedMessage.DataFeedUpdate)
                {
                    continue;
                }
                HandleDataFeedUpdate(header.Value.MessageAsDataFeedUpdate());
            }
        }

        private void HandleRpc(RpcMessageHeader header)
        {
            switch (header.MessageType)
            {
                case RpcMessage.SkeletonConfigResponse:
                {
                    var response = header.MessageAsSkeletonConfigResponse();
                    var config = new SlimeVRSkeletonConfig
                    {
                        UserHeightMeters = response.UserHeight
                    };
                    for (int j = 0; j < response.SkeletonPartsLength; j++)
                    {
                        var part = response.SkeletonParts(j);
                        if (part.HasValue)
                        {
                            config.Parts[part.Value.Bone] = part.Value.Value;
                        }
                    }
                    SkeletonConfigReceived?.Invoke(config);
                    break;
                }
                case RpcMessage.HeartbeatRequest:
                {
                    TrySend(BuildRpc(b =>
                    {
                        HeartbeatResponse.StartHeartbeatResponse(b);
                        return (RpcMessage.HeartbeatResponse, HeartbeatResponse.EndHeartbeatResponse(b).Value);
                    }, header.TxId?.Id));
                    break;
                }
            }
        }

        private void HandleDataFeedUpdate(DataFeedUpdate update)
        {
            var trackers = new List<SlimeVRTrackerSnapshot>();

            for (int i = 0; i < update.DevicesLength; i++)
            {
                var device = update.Devices(i);
                if (!device.HasValue)
                {
                    continue;
                }
                var hardwareInfo = device.Value.HardwareInfo;
                var hardwareStatus = device.Value.HardwareStatus;
                string deviceName = device.Value.CustomName;
                if (string.IsNullOrEmpty(deviceName) && hardwareInfo.HasValue)
                {
                    deviceName = hardwareInfo.Value.DisplayName ?? hardwareInfo.Value.Model;
                }

                for (int t = 0; t < device.Value.TrackersLength; t++)
                {
                    var tracker = device.Value.Trackers(t);
                    if (!tracker.HasValue)
                    {
                        continue;
                    }
                    var snapshot = BuildSnapshot(tracker.Value, isSynthetic: false);
                    snapshot.DeviceName = deviceName;
                    if (device.Value.Id.HasValue)
                    {
                        snapshot.HasDeviceId = true;
                        snapshot.DeviceId = device.Value.Id.Value.Id;
                    }
                    if (hardwareStatus.HasValue)
                    {
                        var status = hardwareStatus.Value;
                        if (status.BatteryPctEstimate.HasValue)
                        {
                            snapshot.HasBattery = true;
                            snapshot.BatteryPercent = status.BatteryPctEstimate.Value;
                        }
                        if (status.BatteryVoltage.HasValue)
                        {
                            snapshot.BatteryVoltage = status.BatteryVoltage.Value;
                        }
                        if (status.Rssi.HasValue)
                        {
                            snapshot.HasRssi = true;
                            snapshot.RssiDbm = status.Rssi.Value;
                        }
                    }
                    trackers.Add(snapshot);
                }
            }

            for (int i = 0; i < update.SyntheticTrackersLength; i++)
            {
                var tracker = update.SyntheticTrackers(i);
                if (tracker.HasValue)
                {
                    trackers.Add(BuildSnapshot(tracker.Value, isSynthetic: true));
                }
            }

            TrackersReceived?.Invoke(trackers);
        }

        private static SlimeVRTrackerSnapshot BuildSnapshot(TrackerData tracker, bool isSynthetic)
        {
            var snapshot = new SlimeVRTrackerSnapshot
            {
                Status = tracker.Status,
                IsSynthetic = isSynthetic
            };
            var info = tracker.Info;
            if (info.HasValue)
            {
                snapshot.BodyPart = info.Value.BodyPart;
                snapshot.DisplayName = info.Value.DisplayName;
                snapshot.CustomName = info.Value.CustomName;
                snapshot.IsHmd = info.Value.IsHmd;
                snapshot.IsComputed = info.Value.IsComputed;
                snapshot.IsImu = info.Value.IsImu;

                // Mounting orientation rides along in TrackerInfo (the `info` mask we already request),
                // so this needs no extra datafeed fields — it was simply being dropped before.
                var mounting = info.Value.MountingOrientation;
                if (mounting.HasValue)
                {
                    snapshot.HasMountingOrientation = true;
                    snapshot.MountingOrientation = new SlimeVRQuat
                    {
                        X = mounting.Value.X,
                        Y = mounting.Value.Y,
                        Z = mounting.Value.Z,
                        W = mounting.Value.W
                    };
                }
                var mountingReset = info.Value.MountingResetOrientation;
                if (mountingReset.HasValue)
                {
                    snapshot.HasMountingResetOrientation = true;
                    snapshot.MountingResetOrientation = new SlimeVRQuat
                    {
                        X = mountingReset.Value.X,
                        Y = mountingReset.Value.Y,
                        Z = mountingReset.Value.Z,
                        W = mountingReset.Value.W
                    };
                }
            }

            var position = tracker.Position;
            if (position.HasValue)
            {
                snapshot.HasPosition = true;
                snapshot.Position = new SlimeVRVec3 { X = position.Value.X, Y = position.Value.Y, Z = position.Value.Z };
            }
            var rotation = tracker.Rotation;
            if (rotation.HasValue)
            {
                snapshot.HasRotation = true;
                snapshot.Rotation = new SlimeVRQuat { X = rotation.Value.X, Y = rotation.Value.Y, Z = rotation.Value.Z, W = rotation.Value.W };
            }
            return snapshot;
        }

        private byte[] BuildRpc(Func<FlatBufferBuilder, (RpcMessage type, int offset)> build, uint? echoTxId = null)
        {
            var b = new FlatBufferBuilder(256);
            var (type, message) = build(b);

            RpcMessageHeader.StartRpcMessageHeader(b);
            RpcMessageHeader.AddTxId(b, TransactionId.CreateTransactionId(b, echoTxId ?? ++_txId));
            RpcMessageHeader.AddMessageType(b, type);
            RpcMessageHeader.AddMessage(b, message);
            var header = RpcMessageHeader.EndRpcMessageHeader(b);

            var rpcMsgs = MessageBundle.CreateRpcMsgsVector(b, new[] { header });
            MessageBundle.StartMessageBundle(b);
            MessageBundle.AddRpcMsgs(b, rpcMsgs);
            b.Finish(MessageBundle.EndMessageBundle(b).Value);
            return b.SizedByteArray();
        }

        private byte[] BuildStartDataFeed()
        {
            var b = new FlatBufferBuilder(256);

            // Poses ride the same feed as status/info; requesting them just adds the position+rotation
            // fields and speeds the cadence up. Off, this is exactly the original status-only feed.
            bool pose = EnablePoseFeed;
            ushort intervalMs = pose ? PoseFeedIntervalMs : DataFeedIntervalMs;

            var deviceTrackerMask = TrackerDataMask.CreateTrackerDataMask(b, info: true, status: true, rotation: pose, position: pose);
            var deviceMask = DeviceDataMask.CreateDeviceDataMask(b, deviceTrackerMask, device_data: true);
            var syntheticMask = TrackerDataMask.CreateTrackerDataMask(b, info: true, status: true, rotation: pose, position: pose);
            var config = DataFeedConfig.CreateDataFeedConfig(b,
                minimum_time_since_last: intervalMs,
                data_maskOffset: deviceMask,
                synthetic_trackers_maskOffset: syntheticMask,
                bone_mask: false,
                stay_aligned_pose_mask: false,
                server_guards_mask: false);

            var feeds = StartDataFeed.CreateDataFeedsVector(b, new[] { config });
            var start = StartDataFeed.CreateStartDataFeed(b, feeds);

            DataFeedMessageHeader.StartDataFeedMessageHeader(b);
            DataFeedMessageHeader.AddMessageType(b, DataFeedMessage.StartDataFeed);
            DataFeedMessageHeader.AddMessage(b, start.Value);
            var header = DataFeedMessageHeader.EndDataFeedMessageHeader(b);

            var messages = MessageBundle.CreateDataFeedMsgsVector(b, new[] { header });
            MessageBundle.StartMessageBundle(b);
            MessageBundle.AddDataFeedMsgs(b, messages);
            b.Finish(MessageBundle.EndMessageBundle(b).Value);
            return b.SizedByteArray();
        }

        private void TrySend(byte[] payload)
        {
            try
            {
                ISolarXRTransport transport = _transport;
                if (transport == null)
                {
                    return;
                }
                lock (_writeLock)
                {
                    transport.WriteMessage(payload);
                }
            }
            catch (Exception e)
            {
                if (_running)
                {
                    Log($"SlimeVR send failed: {e.Message}");
                }
            }
        }

        private void CloseTransport()
        {
            ISolarXRTransport transport = Interlocked.Exchange(ref _transport, null);
            if (transport != null)
            {
                try
                {
                    transport.Dispose();
                }
                catch
                {
                }
            }
        }

        // ---- Transports ----

        private interface ISolarXRTransport : IDisposable
        {
            /// <summary>
            /// Blocking read of one whole MessageBundle payload. Throws when the connection dies;
            /// throws TimeoutException (and kills the connection) when timeoutMs is positive and
            /// nothing arrived in time. timeoutMs &lt;= 0 waits forever.
            /// </summary>
            byte[] ReadMessage(int timeoutMs);

            /// <summary>Write one whole MessageBundle payload. Caller serializes writes.</summary>
            void WriteMessage(byte[] payload);
        }

        /// <summary>Pipe/socket framing: 4-byte little-endian length prefix, length includes the prefix itself.</summary>
        private sealed class LengthPrefixedStreamTransport : ISolarXRTransport
        {
            private readonly Stream _stream;
            private readonly byte[] _lengthBuffer = new byte[4];

            public LengthPrefixedStreamTransport(Stream stream)
            {
                _stream = stream;
            }

            public byte[] ReadMessage(int timeoutMs)
            {
                ReadExact(_lengthBuffer, 4, timeoutMs);
                int total = (_lengthBuffer[0] | (_lengthBuffer[1] << 8) | (_lengthBuffer[2] << 16) | (_lengthBuffer[3] << 24)) - 4;
                if (total < 0 || total > 16 * 1024 * 1024)
                {
                    throw new IOException($"Bad SolarXR frame length {total}");
                }
                var frame = new byte[total];
                ReadExact(frame, total, 0);
                return frame;
            }

            public void WriteMessage(byte[] payload)
            {
                int length = payload.Length + 4;
                var prefix = new byte[4]
                {
                    (byte)length,
                    (byte)(length >> 8),
                    (byte)(length >> 16),
                    (byte)(length >> 24)
                };
                _stream.Write(prefix, 0, 4);
                _stream.Write(payload, 0, payload.Length);
                _stream.Flush();
            }

            private void ReadExact(byte[] buffer, int count, int timeoutMs)
            {
                int offset = 0;
                while (offset < count)
                {
                    int read;
                    if (timeoutMs > 0)
                    {
                        var pending = _stream.ReadAsync(buffer, offset, count - offset);
                        if (!pending.Wait(timeoutMs))
                        {
                            // Abandon the connection: the pending read still owns the buffer.
                            _stream.Dispose();
                            throw new TimeoutException("No data received in time");
                        }
                        read = pending.GetAwaiter().GetResult();
                    }
                    else
                    {
                        read = _stream.Read(buffer, offset, count - offset);
                    }
                    if (read <= 0)
                    {
                        throw new IOException("Connection closed");
                    }
                    offset += read;
                }
            }

            public void Dispose() => _stream.Dispose();
        }

        /// <summary>Websocket framing: one MessageBundle per binary message, no length prefix.</summary>
        private sealed class WebSocketTransport : ISolarXRTransport
        {
            private readonly ClientWebSocket _webSocket;
            private readonly byte[] _receiveBuffer = new byte[64 * 1024];

            public WebSocketTransport(ClientWebSocket webSocket)
            {
                _webSocket = webSocket;
            }

            public byte[] ReadMessage(int timeoutMs)
            {
                using var timeout = timeoutMs > 0 ? new CancellationTokenSource(timeoutMs) : null;
                using var message = new MemoryStream();
                while (true)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = _webSocket
                            .ReceiveAsync(new ArraySegment<byte>(_receiveBuffer), timeout?.Token ?? CancellationToken.None)
                            .GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        _webSocket.Abort();
                        throw new TimeoutException("No data received in time");
                    }
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new IOException("Websocket closed");
                    }
                    message.Write(_receiveBuffer, 0, result.Count);
                    if (message.Length > 16 * 1024 * 1024)
                    {
                        throw new IOException("Websocket message too large");
                    }
                    if (result.EndOfMessage)
                    {
                        // Text messages are unused by the protocol; skip any that appear.
                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            return message.ToArray();
                        }
                        message.SetLength(0);
                    }
                }
            }

            public void WriteMessage(byte[] payload)
            {
                _webSocket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary,
                    endOfMessage: true, CancellationToken.None).GetAwaiter().GetResult();
            }

            public void Dispose()
            {
                // Abort unblocks a pending ReceiveAsync; a graceful close handshake would hang on it.
                _webSocket.Abort();
                _webSocket.Dispose();
            }
        }
    }
}
