using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Basis.BasisUI;
using Basis.EventDriver;
using Basis.Network.Core;
using Basis.Scripts.Common;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Per-client image pickup service. It shares a deterministic network identity across all clients, so any
    /// client can message the others. A sharer sends the picture only to the players it judges close enough;
    /// the server keeps a copy and offers it to everyone else, who measure the distance themselves and ask for
    /// it once they are near enough to want it. An image exists only while its spawner is connected, and
    /// anyone may delete any image for everyone.
    ///
    /// The animation scheduler lives here too. It advances image-pickup animations only while their front face
    /// is visible to a gameplay camera: a per-frame CPU facing/frustum broad phase rejects cards before depth,
    /// raycast, decode, or composition work. Desktop builds then prefer camera-depth visibility, while mobile
    /// and portable platforms use front-face physics samples. Hidden players retain their synchronized epoch and
    /// later resume at the correct frame.
    ///
    /// Image transfer work runs on the update tick; animation scheduling runs on the late-update tick, after
    /// camera and transform writes have settled and before the render.
    /// </summary>
    public static class BasisImagePickupManager
    {
        private const string FixedNetworkIdentifier = "BasisImagePickupManager";
        private const int MaxIgnoredOwnerNameBytes = 1024;

        /// <summary>
        /// Stands in for "no player" where an owner id is required but none exists. Cannot be 0:
        /// peer ids are handed out from zero up, so 0 is the net id of the first player to join.
        /// </summary>
        private const ushort UnownedPlayerId = ushort.MaxValue;
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Pickups;
        private const BasisDebug.LogTag RenderLogTag = BasisDebug.LogTag.Rendering;
        internal const int MaxOwnerNameUtf8Bytes = 256;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private const byte OpSpawn = 1;
        private const byte OpChunk = 2;
        private const byte OpTransform = 3;
        private const byte OpDespawn = 4;
        private const byte OpClaim = 5;
        private const byte OpAnimationSpawn = 6;
        private const byte OpAnimationChunk = 7;

        /// <summary>
        /// Server → owner only: whether the server is holding this image in its own buffer and will
        /// serve it to arrivals. Never sent by a client. See <see cref="HandleServerCacheState"/>.
        /// </summary>
        private const byte OpServerCacheState = 8;

        /// <summary>
        /// Server offering an image it holds: the sharer's own spawn header, opcode swapped. It tells
        /// us the picture exists and where it is standing, and nothing else moves until we ask.
        /// </summary>
        private const byte OpServerCacheOffer = 9;

        /// <summary>
        /// Us asking the server for an offered image. Addressed to ourselves so the relay observes it
        /// on the way past and delivers it to nobody; the server is the only intended reader.
        /// </summary>
        private const byte OpServerCacheRequest = 10;

        // Values 0 and 1 were used by pre-release animation transport experiments and remain
        // reserved so stale clients cannot misinterpret the production V2 native-LZ4 payload.
        private const byte AnimationFormatNativeLz4 = BasisNativeAnimationPayload.FormatNativeLz4;
        private const byte AnimationFormatGif = BasisNativeAnimationPayload.FormatGif;

        /// <summary>Opcode, image id, chunk index, and chunk length — see <see cref="EncodeChunk"/>.</summary>
        private const int ImageChunkHeaderBytes = 1 + BasisGuid128.SerializedSize + sizeof(int) * 2;

        private const string CompositorShaderName = "Hidden/Basis/ImageAnimationComposite";
        private const int FrontFaceSampleCount = 5;
        private const int RaycastHitBufferSize = 16;
        private const int MaximumCpuFacingCameraBits = 64;


        public static bool HasNetworkID;
        public static ushort NetworkID;

        private sealed class SpawnRateLimitState
        {
            public float Tokens;
            public float LastRefillTime;
        }

        private sealed class OwnedImage
        {
            public BasisImagePickupObject Object;
            public byte[] CleanPng;
            public int Width;
            public int Height;
            public string OwnerName;
            public BasisNativeAnimationPayload AnimationPayload;
            public long PlaybackEpochUtcTicks;
            public readonly HashSet<ushort> SentRecipients = new();
        }

        /// <summary>
        /// Distances for every outstanding offer at once. Scheduled early in the tick and collected at
        /// the end, so it runs underneath the transfer and animation work rather than in front of it.
        /// </summary>
        [BurstCompile]
        private struct OfferRangeJob : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<Vector3> Positions;

            public Vector3 Viewer;
            public float RangeSquared;

            [WriteOnly]
            public NativeArray<byte> InRange;

            public void Execute(int index)
            {
                if (RangeSquared <= 0f)
                {
                    InRange[index] = 1;
                    return;
                }
                Vector3 delta = Positions[index] - Viewer;
                float distanceSquared = (delta.x * delta.x) + (delta.y * delta.y) + (delta.z * delta.z);
                InRange[index] = distanceSquared <= RangeSquared ? (byte)1 : (byte)0;
            }
        }

        /// <summary>One player's replication anchor, sampled once per range pass.</summary>
        internal struct ReplicationCandidate
        {
            public ushort PlayerId;
            public Vector3 Position;
        }

        private sealed class InboundTransfer
        {
            public ushort Sender;
            public Guid Id;
            public byte[] Buffer;
            public long ReservedBytes;
            public bool[] Received;
            public int ReceivedCount;
            public int TotalChunks;
            public int Width;
            public int Height;
            public ushort OwnerId;
            public string OwnerName;
            public float Deadline;
            public Vector3 Position;
            public Quaternion Rotation;
            public TransferRate Rate;
            public float LastProgressTime;
            public bool RejectionLogged;
            public bool StallLogged;
        }

        /// <summary>
        /// Smoothed throughput for one transfer, sampled on an interval rather than per chunk so the
        /// readout does not swing with whichever frame a chunk happens to land on.
        /// </summary>
        private struct TransferRate
        {
            public long MovedBytes;
            public long SampleBytes;
            public float SampleTime;
            public float BytesPerSecond;

            public void Sample(float now)
            {
                if (SampleTime <= 0f)
                {
                    SampleTime = now;
                    SampleBytes = MovedBytes;
                    return;
                }

                float elapsed = now - SampleTime;
                if (elapsed < BasisImagePickupSettings.TransferRateSampleSeconds)
                    return;

                float instant = (MovedBytes - SampleBytes) / elapsed;
                BytesPerSecond =
                    BytesPerSecond <= 0f
                        ? instant
                        : Mathf.Lerp(
                            BytesPerSecond,
                            instant,
                            BasisImagePickupSettings.TransferRateSmoothing
                        );
                SampleTime = now;
                SampleBytes = MovedBytes;
            }

            public float Fraction(long totalBytes)
            {
                return totalBytes > 0 ? Mathf.Clamp01((float)MovedBytes / totalBytes) : 0f;
            }
        }

        private sealed class InboundAnimationTransfer
        {
            public ushort Sender;
            public Guid Id;
            public byte Format;
            public NativeArray<byte> Buffer;
            public long ReservedBytes;
            public NativeArray<byte> Received;
            public int ReceivedCount;
            public int TotalChunks;
            public long PlaybackEpochUtcTicks;
            public float Deadline;
            public TransferRate Rate;
        }

        private sealed class QueuedFileSpawn
        {
            public string Path;
            /// <summary>
            /// Image data for a spawn that never had a file — a clipboard paste. Null for a dropped
            /// file, which is what <see cref="Path"/> being set means.
            /// </summary>
            public byte[] Data;
            /// <summary>What rejection popups name this import; the path for a file, a description otherwise.</summary>
            public string Label;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private sealed class PendingGifSpawn
        {
            public string Path;
            /// <summary>GIF data for a pasted animation; null when <see cref="Path"/> supplies it.</summary>
            public byte[] Data;
            public string Label;
            public Vector3 Position;
            public Quaternion Rotation;
            public BasisGifDecodeJobRequest Job;
            public Guid Id;
            /// <summary>The placeholder card raised at drop time; the spawn fills it in once the decode lands.</summary>
            public BasisImagePickupObject Pickup;
        }

        private sealed class QueuedInboundAnimationDecode
        {
            public ushort Sender;
            public Guid Id;
            public BasisNativeAnimationPayload Payload;
            public long ReservedBytes;
            public int PayloadBytes;
            public int DecodedBytes;
            public long PlaybackEpochUtcTicks;
        }

        private sealed class PendingInboundAnimationDecode
        {
            public ushort Sender;
            public Guid Id;
            public BasisNativeAnimationPayload Payload;
            public long ReservedBytes;
            public int PayloadBytes;
            public int DecodedBytes;
            public long PlaybackEpochUtcTicks;
            public IBasisAnimationDecodeRequest Job;
        }

        private sealed class OutboundImageTransfer
        {
            public Guid Id;
            public ushort OwnerId;
            public string OwnerName;
            public int Width;
            public int Height;
            public byte[] Png;
            public int NextChunkIndex;
            public Vector3 Position;
            public Quaternion Rotation;
            public ushort[] Recipients;
            public bool HeaderSent;
            public TransferRate Rate;
            /// <summary>
            /// Reused chunk packet, mirroring what the animation path already does. A fresh array per chunk
            /// was affordable at eight chunks a frame; at the rates a fast link now allows it is not.
            /// </summary>
            public byte[] ChunkBuffer;
        }

        private sealed class OutboundAnimationTransfer
        {
            public Guid Id;
            public BasisNativeAnimationPayload Payload;
            public int NextChunkIndex;
            public long PlaybackEpochUtcTicks;
            public ushort[] Recipients;
            public BasisAnimationPacketJobRequest PacketJob;
            public BasisAnimationPacketBatch Packets;
            public byte[] HeaderBuffer;
            public byte[] FullChunkBuffer;
            public byte[] TailChunkBuffer;
            public bool HeaderSent;
            public long EnqueuedTimestamp;
            public long FirstPacketQueueTicks;
            public TransferRate Rate;
        }

        private static readonly Dictionary<Guid, BasisImagePickupObject> _images = new();
        private static readonly Dictionary<Guid, OwnedImage> _owned = new();
        private static readonly Dictionary<Guid, BasisNativeAnimationPayload> _remoteAnimationPayloads = new();
        private static readonly Dictionary<Guid, InboundTransfer> _inbound = new();
        private static readonly Dictionary<Guid, InboundAnimationTransfer> _inboundAnimations = new();
        private static readonly Queue<OutboundImageTransfer> _outboundImages = new();
        private static readonly Queue<OutboundAnimationTransfer> _outboundAnimations = new();
        private static readonly Queue<QueuedFileSpawn> _queuedFileSpawns = new();
        private static readonly Queue<PendingGifSpawn> _queuedGifSpawns = new();
        private static readonly List<PendingGifSpawn> _pendingGifSpawns = new();
        private static readonly List<QueuedInboundAnimationDecode> _queuedInboundAnimationDecodes = new();
        private static readonly List<PendingInboundAnimationDecode> _pendingInboundAnimationDecodes = new();
        private static readonly HashSet<Guid> _animationAttempted = new();
        private static long _reservedInboundTransferBytes;
        private static bool _gifDecodePausedForMemory;
        private static bool _backPanelsVisible;
        private static bool _backPanelSyncPending;
        private static bool _destroying;
        private static readonly Dictionary<ushort, SpawnRateLimitState> _spawnRateBySender = new();
        private static readonly List<Guid> _scratchIds = new();
        private static readonly List<ushort> _scratchRecipientIds = new(256);
        private static readonly List<ReplicationCandidate> _scratchCandidates = new(256);

        /// <summary>
        /// Images the server has told us it holds but which we have not asked for yet, and where each
        /// one is standing. The distance decision is ours alone: the server never learns where anybody
        /// is, and an offer costs a spawn header rather than a picture.
        /// </summary>
        private static readonly Dictionary<Guid, Vector3> _pendingOffers = new();

        private static readonly ushort[] _selfRecipient = new ushort[1];
        private static Guid[] _offerRangeIds = Array.Empty<Guid>();
        private static NativeArray<Vector3> _offerRangePositions;
        private static NativeArray<byte> _offerRangeResults;
        private static JobHandle _offerRangeHandle;
        private static int _offerRangeCount;
        private static bool _offerRangeScheduled;
        private static float _nextOfferRangeCheckTime;

        /// <summary>
        /// Images of ours the server is holding in its own buffer and offers to arrivals itself, who
        /// then apply their own range test and ask for what they want. It tells us the moment it stops
        /// holding one and we resume providing from the next pass.
        /// </summary>
        private static readonly HashSet<Guid> _serverHeldImages = new();
        private static bool _initialized;

        /// <summary>
        /// Bumped every time this client leaves an instance. The network id resolve is asynchronous, so a
        /// reply still in flight when the connection went away would otherwise arm the next server with the
        /// previous server's id; the resolve captures this and drops its answer if it moved.
        /// <see cref="_identityResolveGeneration"/> doubles as the in-flight token, so several joins arriving
        /// before the first answer do not each start their own resolve.
        /// </summary>
        private static int _connectionGeneration;
        private static int _identityResolveGeneration = -1;
        private static float _nextRecipientRangeRefreshTime;

        /// <summary>
        /// Last replication range we reported, so a server that never advertised one - the single most
        /// common reason the range appears not to work at all - says so once rather than every frame.
        /// NaN means nothing has been reported yet, so the first real read always prints.
        /// </summary>
        private static float _lastReportedRangeMeters = float.NaN;

        private static readonly List<BasisAnimatedImagePlayer> _players = new(64);
        private static readonly List<BasisAnimatedImagePlayer> _pendingRemoval = new(8);
        private static readonly List<BasisAnimatedImagePlayer> _pendingDecodedReleases = new(4);
        private static readonly List<BasisAnimatedImagePlayer> _pendingCompositorReleases = new(4);
        private static readonly List<BasisAnimatedImagePlayer> _pendingJobFlush = new(64);
        private static readonly List<BasisAnimatedImagePlayer> _cpuFrontFacingPlayers = new(64);
        private static readonly List<BasisAnimatedImagePlayer> _reloadDecodePlayers = new(2);
        private static readonly List<Camera> _visibilityCameras = new(8);
        private static readonly List<Vector3> _visibilityCameraPositions = new(8);
        private static readonly List<Vector3> _visibilityCameraForwards = new(8);
        private static readonly List<bool> _visibilityCameraOrthographic = new(8);
        private static readonly List<Camera> _registeredCameraScratch = new(8);
        private static readonly List<Plane[]> _visibilityFrustums = new(8);
        private static readonly RaycastHit[] _raycastHits = new RaycastHit[RaycastHitBufferSize];
        private static CommandBuffer _commands;
        private static Material _compositorMaterial;
        private static BasisAnimatedImagePlayer _compositorPriorityCandidate;
        private static int _localVisibilityCameraIndex = -1;
        private static int _activeReloadDecodes;
        private static int _visiblePassStartIndex;
        private static bool _cameraMaskLimitWarningLogged;
        private static bool _schedulerReady;

        internal static Material CompositorMaterial => _compositorMaterial;
        internal static bool HasGpuCompositor => _compositorMaterial != null;

        /// <summary>Arms the image pickup service. Safe to call more than once.</summary>
        public static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;
            _destroying = false;

            BasisImagePickupLinkProbe.Reset();
            BasisImagePickupBandwidth.Reset();
            _nextRecipientRangeRefreshTime = 0f;
            _nextOfferRangeCheckTime = 0f;
            _lastReportedRangeMeters = float.NaN;
            EnsureSchedulerResources();
            BasisEventDriver.OnUpdate += SimulateUpdate;
            BasisNetworkPlayer.OnLocalPlayerJoined += HandleLocalPlayerJoined;
            BasisNetworkPlayer.OnPlayerJoined += OnPlayerJoined;
            BasisNetworkPlayer.OnPlayerLeft += OnPlayerLeft;
            Application.quitting += Shutdown;

            if (BasisNetworkConnection.LocalPlayerIsConnected)
                HandleLocalPlayerJoined(null, null);
        }

        /// <summary>
        /// Releases every player-owned native buffer and unsubscribes the service. Runs on application quit so
        /// Unity's shutdown leak validation sees no retained allocations, and so a domain-reload-free editor
        /// play session re-arms from clean state. Idempotent, and safe to call without a prior
        /// <see cref="Initialize"/>, so tests can use it to reset the static state between cases.
        /// </summary>
        public static void Shutdown()
        {
            _initialized = false;
            _destroying = true;
            _backPanelsVisible = false;
            _backPanelSyncPending = false;

            BasisEventDriver.OnUpdate -= SimulateUpdate;
            BasisNetworkPlayer.OnLocalPlayerLeft -= HandleLocalPlayerLeft;
            BasisNetworkPlayer.OnLocalPlayerJoined -= HandleLocalPlayerJoined;
            BasisNetworkPlayer.OnPlayerJoined -= OnPlayerJoined;
            BasisNetworkPlayer.OnPlayerLeft -= OnPlayerLeft;
            Application.quitting -= Shutdown;

            ReleaseNetworkIdentity();

            _scratchIds.Clear();
            foreach (Guid id in _images.Keys)
                _scratchIds.Add(id);
            int trackedImageCount = _scratchIds.Count;
            for (int i = 0; i < trackedImageCount; i++)
                RemoveImage(_scratchIds[i]);
            _scratchIds.Clear();

            int pendingGifSpawnCount = _pendingGifSpawns.Count;
            for (int i = 0; i < pendingGifSpawnCount; i++)
                _pendingGifSpawns[i].Job?.Dispose();
            _pendingGifSpawns.Clear();
            _queuedGifSpawns.Clear();
            _queuedFileSpawns.Clear();

            foreach (InboundTransfer transfer in _inbound.Values)
                ReleaseInboundTransferBytes(transfer.ReservedBytes);
            _inbound.Clear();

            int queuedInboundDecodeCount = _queuedInboundAnimationDecodes.Count;
            for (int i = 0; i < queuedInboundDecodeCount; i++)
            {
                QueuedInboundAnimationDecode queued = _queuedInboundAnimationDecodes[i];
                queued.Payload?.Dispose();
                ReleaseInboundTransferBytes(queued.ReservedBytes);
            }
            _queuedInboundAnimationDecodes.Clear();

            int pendingInboundDecodeCount = _pendingInboundAnimationDecodes.Count;
            for (int i = 0; i < pendingInboundDecodeCount; i++)
            {
                PendingInboundAnimationDecode pending = _pendingInboundAnimationDecodes[i];
                pending.Job?.Dispose();
                pending.Payload?.Dispose();
                ReleaseInboundTransferBytes(pending.ReservedBytes);
            }
            _pendingInboundAnimationDecodes.Clear();

            foreach (InboundAnimationTransfer transfer in _inboundAnimations.Values)
            {
                if (transfer.Buffer.IsCreated)
                    transfer.Buffer.Dispose();
                if (transfer.Received.IsCreated)
                    transfer.Received.Dispose();
                ReleaseInboundTransferBytes(transfer.ReservedBytes);
            }
            _inboundAnimations.Clear();

            _outboundImages.Clear();
            while (_outboundAnimations.Count > 0)
                DisposeOutboundAnimationTransfer(_outboundAnimations.Dequeue());

            foreach (OwnedImage owned in _owned.Values)
            {
                if (owned.Object != null)
                    owned.Object.AnimatedImagePlayer?.ClearReloadPayload();
                owned.AnimationPayload?.Dispose();
            }
            _owned.Clear();
            _serverHeldImages.Clear();
            ReleaseOfferRangeResources();
            _pendingOffers.Clear();
            _nextOfferRangeCheckTime = 0f;
            _lastReportedRangeMeters = float.NaN;
            foreach (BasisNativeAnimationPayload payload in _remoteAnimationPayloads.Values)
                payload?.Dispose();
            _remoteAnimationPayloads.Clear();
            _animationAttempted.Clear();
            _spawnRateBySender.Clear();
            _reservedInboundTransferBytes = 0;
            _gifDecodePausedForMemory = false;
            _nextRecipientRangeRefreshTime = 0f;
            BasisImagePickupLinkProbe.Reset();
            BasisImagePickupBandwidth.Reset();
            BasisImagePickupProgressGizmos.Shutdown();

            ReleaseSchedulerResources();
            _destroying = false;
        }

        private static async void HandleLocalPlayerJoined(BasisNetworkPlayer networkPlayer, BasisLocalPlayer localPlayer)
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected)
            {
                BasisDebug.LogError("Image pickup manager cannot start; the local player is not connected.", LogTag);
                return;
            }
            int generation = _connectionGeneration;
            if (HasNetworkID || _identityResolveGeneration == generation)
                return;
            _identityResolveGeneration = generation;

            BasisIdResolutionResult resolution = await BasisNetworkIdResolver.ResolveAsync(FixedNetworkIdentifier);
            // The id belongs to whichever connection answered. If that connection is gone the answer is the
            // previous server's index, and arming with it leaves us listening on nothing.
            if (!_initialized || HasNetworkID || generation != _connectionGeneration)
                return;
            if (!resolution.Success)
            {
                _identityResolveGeneration = -1;
                BasisDebug.LogError(
                    $"Image pickup manager could not resolve the network identifier '{FixedNetworkIdentifier}'.",
                    LogTag
                );
                return;
            }

            NetworkID = resolution.Id;
            HasNetworkID = true;
            BasisNetworkGenericMessages.RegisterDirectHandler(NetworkID, OnDirectNetworkMessage);
            // Armed per join rather than once in Initialize so the leave handler only exists while there is a
            // connection to leave — the same pattern BasisServerProvidedItems uses. The -= keeps that idempotent.
            BasisNetworkPlayer.OnLocalPlayerLeft -= HandleLocalPlayerLeft;
            BasisNetworkPlayer.OnLocalPlayerLeft += HandleLocalPlayerLeft;
            BasisDebug.Log($"Image pickup manager ready (network id {NetworkID}).", LogTag);
        }

        /// <summary>
        /// Drops every shared image when this client leaves the instance. Cards outlive scene loads by design
        /// (see <see cref="BasisImagePickupObject.Build"/>), so nothing else would clear them and they would
        /// follow the player into the next world.
        /// </summary>
        private static void HandleLocalPlayerLeft(BasisNetworkPlayer networkPlayer, BasisLocalPlayer localPlayer)
        {
            // Ahead of the early out below, because the identity has to go even when there is nothing to clear.
            ReleaseNetworkIdentity();

            int trackedImageCount = _images.Count;
            if (trackedImageCount == 0 && _inbound.Count == 0 && _inboundAnimations.Count == 0)
                return;

            _scratchIds.Clear();
            foreach (Guid id in _images.Keys)
                _scratchIds.Add(id);
            int removalCount = _scratchIds.Count;
            for (int i = 0; i < removalCount; i++)
                RemoveImage(_scratchIds[i]);
            _scratchIds.Clear();

            _scratchIds.Clear();
            foreach (Guid id in _inbound.Keys)
                _scratchIds.Add(id);
            int inboundCount = _scratchIds.Count;
            for (int i = 0; i < inboundCount; i++)
                RemoveInboundTransfer(_scratchIds[i]);
            _scratchIds.Clear();

            foreach (Guid id in _inboundAnimations.Keys)
                _scratchIds.Add(id);
            int inboundAnimationCount = _scratchIds.Count;
            for (int i = 0; i < inboundAnimationCount; i++)
                RemoveInboundAnimationTransfer(_scratchIds[i]);
            _scratchIds.Clear();

            _outboundImages.Clear();
            while (_outboundAnimations.Count > 0)
                DisposeOutboundAnimationTransfer(_outboundAnimations.Dequeue());
            _spawnRateBySender.Clear();
            _pendingOffers.Clear();
            BasisImagePickupProgressGizmos.Shutdown();
            BasisImagePickupLinkProbe.Reset();
            BasisImagePickupBandwidth.Reset();

            BasisDebug.Log(
                $"Image pickup manager cleared {trackedImageCount:N0} shared image(s) on leaving the instance.",
                LogTag
            );
        }

        /// <summary>
        /// Drops the network id and its handler registration, and opens a new connection generation.
        ///
        /// The id is per server, not per client: the server hands ids out from a counter that starts at zero on
        /// an empty instance, so <see cref="FixedNetworkIdentifier"/> is a different ushort on every server a
        /// client visits. Keeping the previous server's id past a disconnect left the manager armed on an index
        /// the new server uses for something else, or for nothing, so every image message the new server
        /// delivered found no handler and sat in the deferred queue forever - images already in the instance
        /// never appeared, and ours never left.
        /// </summary>
        /// <summary>
        /// Confirms the id we hold was issued by the connection we are actually on, and resolves a new one if
        /// it was not.
        ///
        /// <see cref="HandleLocalPlayerLeft"/> is the ordinary place the id is released, but the teardown only
        /// raises that event while it can still find the local player, so a connection that died hard leaves us
        /// armed on the last server's index with nothing to say so. <c>BasisNetworkIdResolver.KnownIdMap</c> is
        /// emptied on every teardown and refilled by the server on join, so an id it does not confirm is one
        /// from a room we have already left.
        /// </summary>
        private static void EnsureNetworkIdentity()
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected)
                return;

            if (HasNetworkID)
            {
                if (BasisNetworkIdResolver.KnownIdMap.TryGetValue(FixedNetworkIdentifier, out ushort issued)
                    && issued == NetworkID)
                {
                    return;
                }
                ReleaseNetworkIdentity();
            }

            HandleLocalPlayerJoined(null, null);
        }

        private static void ReleaseNetworkIdentity()
        {
            _connectionGeneration++;
            _identityResolveGeneration = -1;
            if (HasNetworkID)
                BasisNetworkGenericMessages.UnregisterDirectHandler(NetworkID);
            HasNetworkID = false;
            NetworkID = 0;
            // Whether the next server holds any of our images is its own answer to give.
            _serverHeldImages.Clear();
        }

        /// <summary>
        /// Sends over direct peer-to-peer links, falling back to the server relay for recipients with no direct
        /// connection. Received via <see cref="OnDirectNetworkMessage"/>.
        /// </summary>
        private static void SendCustomNetworkEventDirect(
            byte[] buffer,
            DeliveryMethod deliveryMethod,
            ushort[] recipients
        )
        {
            if (!HasNetworkID)
            {
                BasisDebug.LogError("Image pickup manager has no network id assigned yet.", LogTag);
                return;
            }
            BasisNetworkGenericMessages.OnNetworkMessageSendDirect(NetworkID, buffer, deliveryMethod, recipients);
        }

        /// <summary>
        /// Validates a PNG, JPEG, or GIF file, spawns it locally, and broadcasts a sanitized poster PNG.
        /// Multi-frame GIFs additionally replicate normalized animation data and a synchronized playback epoch.
        /// </summary>
        public static bool SpawnFromFile(string path)
        {
            if (!CanStartLocalSpawn(path))
                return false;

            int currentCount = GetLocalReservedImageCount();
            if (currentCount >= BasisImagePickupSettings.MaxConcurrentImagesPerSender)
            {
                BasisImagePickupRejectionPopup.ShowImageLimit(currentCount, 1);
                BasisDebug.LogWarning(
                    $"Image pickup rejected: local image limit of "
                        + $"{BasisImagePickupSettings.MaxConcurrentImagesPerSender} reached.",
                    LogTag
                );
                return false;
            }

            GetSpawnPose(out Vector3 position, out Quaternion rotation);
            return SpawnFromFileAtPose(path, position, rotation);
        }

        /// <summary>
        /// Spawns an image supplied as data rather than as a file — what a clipboard paste produces,
        /// where the picture exists only in memory and no path can be handed around. From the import
        /// queue onward it is treated exactly like a dropped file: same pacing, same caps, same
        /// sanitized PNG on the wire, and the same animated path for a GIF.
        /// </summary>
        /// <param name="label">What rejection popups should call this image.</param>
        public static bool SpawnFromImageData(byte[] data, string label)
        {
            if (data == null || data.Length == 0)
                return false;
            if (!CanStartLocalSpawn(label))
                return false;

            int currentCount = GetLocalReservedImageCount();
            if (currentCount >= BasisImagePickupSettings.MaxConcurrentImagesPerSender)
            {
                BasisImagePickupRejectionPopup.ShowImageLimit(currentCount, 1);
                BasisDebug.LogWarning(
                    $"Image pickup rejected: local image limit of "
                        + $"{BasisImagePickupSettings.MaxConcurrentImagesPerSender} reached.",
                    LogTag
                );
                return false;
            }

            GetSpawnPose(out Vector3 position, out Quaternion rotation);
            _queuedFileSpawns.Enqueue(
                new QueuedFileSpawn
                {
                    Data = data,
                    Label = label,
                    Position = position,
                    Rotation = rotation,
                }
            );
            return true;
        }

        /// <summary>
        /// Spawns an image supplied as unpacked pixels, which is what a clipboard bitmap becomes once
        /// its header has been read. Immediate rather than queued: the caller has already paid the
        /// unpacking cost, and re-encoding straight away is what makes this a normal shared image with
        /// a sanitized PNG behind it, exactly like every other source.
        /// </summary>
        /// <param name="rgba">Top-down RGBA32, four bytes per pixel.</param>
        public static bool SpawnFromRgba32(byte[] rgba, int width, int height, string label)
        {
            if (rgba == null || rgba.Length == 0)
                return false;
            if (!CanStartLocalSpawn(label))
                return false;

            int currentCount = GetLocalReservedImageCount();
            if (currentCount >= BasisImagePickupSettings.MaxConcurrentImagesPerSender)
            {
                BasisImagePickupRejectionPopup.ShowImageLimit(currentCount, 1);
                BasisDebug.LogWarning(
                    $"Image pickup rejected: local image limit of "
                        + $"{BasisImagePickupSettings.MaxConcurrentImagesPerSender} reached.",
                    LogTag
                );
                return false;
            }

            GetSpawnPose(out Vector3 position, out Quaternion rotation);
            return SpawnValidatedFile(
                label,
                BasisImageSecurity.ValidateRgba32(rgba, width, height),
                position,
                rotation,
                Guid.NewGuid(),
                null
            );
        }

        /// <summary>
        /// Spawns one drag/drop batch in stable row-major slots. GIFs retain their assigned slots while
        /// waiting in the decode queue, so faster static images or shorter GIFs cannot scramble the layout.
        /// </summary>
        public static int SpawnFromFiles(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
                return 0;

            int pathCount = paths.Count;
            var supportedPaths = new List<string>(pathCount);
            for (int i = 0; i < pathCount; i++)
            {
                string path = paths[i];
                if (!string.IsNullOrWhiteSpace(path) && BasisImageSecurity.HasSupportedImageExtension(path))
                {
                    supportedPaths.Add(path);
                }
            }
            if (supportedPaths.Count == 0)
                return 0;
            if (!CanStartLocalSpawn(supportedPaths[0]))
                return 0;

            int currentCount = GetLocalReservedImageCount();
            int availableSlots = CalculateAvailableLocalImageSlots(
                _owned.Count,
                _queuedFileSpawns.Count + _queuedGifSpawns.Count,
                _pendingGifSpawns.Count
            );
            if (availableSlots <= 0)
            {
                BasisImagePickupRejectionPopup.ShowImageLimit(currentCount, supportedPaths.Count);
                BasisDebug.LogWarning(
                    $"Image pickup batch rejected: local image limit of "
                        + $"{BasisImagePickupSettings.MaxConcurrentImagesPerSender} reached.",
                    LogTag
                );
                return 0;
            }

            int attemptCount = Mathf.Min(supportedPaths.Count, availableSlots);
            GetSpawnPose(out Vector3 batchCenter, out Quaternion rotation);
            float minimumCenterY = GetMinimumBatchImageCenterY(batchCenter.y);
            int columns = CalculateBatchSpawnColumns(attemptCount, batchCenter.y, minimumCenterY);
            float minimumLocalY = minimumCenterY - batchCenter.y;
            Vector3 horizontalRight = rotation * Vector3.right;

            int accepted = 0;
            int animatedAccepted = 0;
            int supportedPathCount = supportedPaths.Count;
            for (int pathIndex = 0; pathIndex < supportedPathCount && accepted < attemptCount; pathIndex++)
            {
                Vector3 localOffset = CalculateBatchSpawnLocalOffset(accepted, attemptCount, columns, minimumLocalY);
                Vector3 position =
                    batchCenter
                    + horizontalRight * localOffset.x
                    + Vector3.up * localOffset.y;
                string path = supportedPaths[pathIndex];
                if (!SpawnFromFileAtPose(path, position, rotation))
                    continue;

                accepted++;
                if (BasisAnimatedImageJobs.IsGifPath(path))
                    animatedAccepted++;
            }

            BasisImagePickupRejectionPopup.ShowBatchNotice(
                currentCount,
                supportedPaths.Count,
                accepted,
                animatedAccepted
            );
            return accepted;
        }

        private static int GetLocalReservedImageCount()
        {
            return _owned.Count
                + _queuedFileSpawns.Count
                + _queuedGifSpawns.Count
                + _pendingGifSpawns.Count;
        }

        internal static int CalculateAvailableLocalImageSlots(int ownedCount, int queuedSpawnCount, int activeGifCount)
        {
            long reserved =
                Math.Max(0, ownedCount)
                + (long)Math.Max(0, queuedSpawnCount)
                + Math.Max(0, activeGifCount);
            long available =
                BasisImagePickupSettings.MaxConcurrentImagesPerSender - reserved;
            return available <= 0 ? 0 : (int)Math.Min(int.MaxValue, available);
        }

        private static bool CanStartLocalSpawn(string label)
        {
            if (!BasisNetworkModeration.GlobalImagesLocked || BasisNetworkModeration.LocalPlayerHasGlobalLockBypass())
            {
                return true;
            }

            string reason = BasisLocalization.Get("imagePickup.popup.reason.adminLocked");
            BasisImagePickupRejectionPopup.Show(label, reason);
            BasisDebug.LogWarning($"Image pickup rejected: {reason}", LogTag);
            return false;
        }

        /// <summary>
        /// Admits one dropped file to the paced import queue. The batch's grid slot is captured here, so a
        /// drop keeps the layout it was given no matter what order the imports finish in.
        /// </summary>
        private static bool SpawnFromFileAtPose(string path, Vector3 position, Quaternion rotation)
        {
            _queuedFileSpawns.Enqueue(
                new QueuedFileSpawn
                {
                    Path = path,
                    Label = path,
                    Position = position,
                    Rotation = rotation,
                }
            );
            return true;
        }

        /// <summary>
        /// Imports dropped files a few per frame. Importing a static image decodes it, downscales it, and
        /// re-encodes it to PNG, all on the main thread — tens of milliseconds for a large one — so importing
        /// a whole drag-and-drop batch in the frame it lands stalls for as long as the entire batch takes.
        /// A multi-second main-thread stall is bad on desktop and worse in VR, where it is long enough for the
        /// compositor to take the headset over. Pacing turns that freeze into a progressive fill.
        /// </summary>
        private static void ProcessQueuedFileSpawns()
        {
            if (_queuedFileSpawns.Count == 0)
                return;

            if (
                BasisNetworkModeration.GlobalImagesLocked
                && !BasisNetworkModeration.LocalPlayerHasGlobalLockBypass()
            )
            {
                // Locked after the drop was admitted but before it drained — the same window the GIF decode
                // path already guards. One notice for the whole batch rather than one popup per queued file.
                string lockedReason = BasisLocalization.Get(
                    "imagePickup.popup.reason.adminLockedDuringDecode"
                );
                string firstLabel = _queuedFileSpawns.Peek().Label;
                _queuedFileSpawns.Clear();
                BasisImagePickupRejectionPopup.Show(firstLabel, lockedReason);
                BasisDebug.LogWarning($"Image pickup rejected: {lockedReason}", LogTag);
                return;
            }

            int budget = BasisImagePickupSettings.MaxFileImportsPerFrame;
            while (budget > 0 && _queuedFileSpawns.Count > 0)
            {
                QueuedFileSpawn queued = _queuedFileSpawns.Dequeue();
                budget--;

                // Pasted data carries no name, so what it is has to come from the bytes themselves.
                bool animated = queued.Data != null
                    ? BasisImageSecurity.IsGifData(queued.Data)
                    : BasisAnimatedImageJobs.IsGifPath(queued.Path);
                if (animated)
                {
                    QueueGifSpawn(queued, queued.Position, queued.Rotation);
                    continue;
                }

                SpawnValidatedFileAsync(queued);
            }
        }

        /// <summary>
        /// Runs one queued static-image import through the async validation pipeline — decode on
        /// the main thread, downscale/re-encode/alpha scan on a worker — then spawns the card.
        /// The admin lock is re-checked when the result lands, the same window the GIF path guards.
        /// </summary>
        private static async void SpawnValidatedFileAsync(QueuedFileSpawn queued)
        {
            BasisImageValidationResult result = queued.Data != null
                ? await BasisImageSecurity.ValidateSourceBytesAsync(queued.Data)
                : await BasisImageSecurity.ValidateFileAsync(queued.Path);

            if (
                BasisNetworkModeration.GlobalImagesLocked
                && !BasisNetworkModeration.LocalPlayerHasGlobalLockBypass()
            )
            {
                DisposeRejectedValidationResult(ref result);
                string lockedReason = BasisLocalization.Get(
                    "imagePickup.popup.reason.adminLockedDuringDecode"
                );
                BasisImagePickupRejectionPopup.Show(queued.Label, lockedReason);
                BasisDebug.LogWarning($"Image pickup rejected: {lockedReason}", LogTag);
                return;
            }

            SpawnValidatedFile(
                queued.Label,
                result,
                queued.Position,
                queued.Rotation,
                Guid.NewGuid(),
                null
            );
        }

        /// <summary>
        /// Raises the card before the Burst decode runs, so a dropped GIF occupies its batch slot immediately
        /// instead of appearing seconds later behind the decode queue. The 10-byte logical screen descriptor
        /// gives the decoder's exact poster size, so the placeholder already has the GIF's true aspect and the
        /// card never resizes when <see cref="ProcessCompletedGifSpawns"/> swaps the poster in.
        /// </summary>
        private static bool QueueGifSpawn(QueuedFileSpawn queued, Vector3 position, Quaternion rotation)
        {
            // Pasted data is already in hand, so its logical screen descriptor is read straight out of
            // it; a dropped file reads only the first ten bytes off disk for the same answer.
            int width;
            int height;
            string headerError;
            bool headerOk = queued.Data != null
                ? BasisGifDecoder.TryReadDimensions(queued.Data, out width, out height, out headerError)
                : BasisImageSecurity.TryReadGifFileDimensions(queued.Path, out width, out height, out headerError);

            if (!headerOk || !BasisImageSecurity.AnimationDimensionsWithinCaps(width, height, out headerError))
            {
                BasisImagePickupRejectionPopup.Show(queued.Label, headerError);
                BasisDebug.LogWarning($"Image pickup rejected: {headerError}", LogTag);
                return false;
            }

            Guid id = Guid.NewGuid();
            BasisImagePickupObject placeholder = BuildLoadingPickup(
                id,
                LocalPlayerId(),
                LocalOwnerName(),
                true,
                width,
                height,
                position,
                rotation
            );

            _queuedGifSpawns.Enqueue(
                new PendingGifSpawn
                {
                    Path = queued.Path,
                    Data = queued.Data,
                    Label = queued.Label,
                    Position = position,
                    Rotation = rotation,
                    Id = id,
                    Pickup = placeholder,
                }
            );
            BasisDebug.Log(
                $"Image pickup: queued GIF '{DescribeSpawn(queued.Path, queued.Label)}' "
                    + $"({_queuedGifSpawns.Count:N0} waiting, "
                    + $"{_pendingGifSpawns.Count:N0} active).",
                LogTag
            );
            StartQueuedGifSpawns();
            return true;
        }

        /// <summary>
        /// Creates a card that is live in the world — grabbable, movable, deletable — while its poster is
        /// still decoding or still arriving over the network, and tracks it in <c>_images</c> so transform,
        /// claim, and despawn messages for it apply from the moment it exists rather than being dropped.
        /// </summary>
        private static BasisImagePickupObject BuildLoadingPickup(
            Guid id,
            ushort ownerId,
            string ownerName,
            bool isOwner,
            int width,
            int height,
            Vector3 position,
            Quaternion rotation
        )
        {
            BasisImagePickupObject pickup = BasisImagePickupObject.Build(
                id,
                ownerId,
                ownerName,
                isOwner,
                width,
                height,
                null,
                null,
                false,
                position,
                rotation
            );
            TrackImage(id, pickup);
            RegisterShareable(id, width, height, ownerName);
            return pickup;
        }

        /// <summary>
        /// Whether a placeholder raised earlier is still the card this spawn is filling. Tracks
        /// <c>_images</c> rather than the Unity-null of <paramref name="placeholder"/> because
        /// <see cref="UnityEngine.Object.Destroy"/> only takes effect at end of frame: a user who deletes a
        /// GIF in the same frame its decode lands would otherwise still read as alive here, and the spawn
        /// would replicate an image that is about to vanish.
        /// </summary>
        private static bool IsPlaceholderAlive(Guid id, BasisImagePickupObject placeholder)
        {
            return placeholder != null
                && _images.TryGetValue(id, out BasisImagePickupObject tracked)
                && ReferenceEquals(tracked, placeholder);
        }

        private static void RegisterShareable(Guid id, int width, int height, string ownerName)
        {
            BasisShareableRegistry.Register(
                new BasisShareableEntry
                {
                    Id = id.ToString(),
                    Kind = BasisShareableKind.Image,
                    Title = $"{width}x{height}",
                    SharerName = ownerName,
                    Actions = new List<BasisShareableAction>
                    {
                        new BasisShareableAction
                        {
                            Style = BasisShareableActionStyle.Destructive,
                            Invoke = () => RequestDespawn(id),
                        },
                    },
                });
        }

        /// <summary>
        /// The local player's net id, or <see cref="UnownedPlayerId"/> when there is no local
        /// player yet. Falling back to 0 stamped the pickup as belonging to whoever joined the
        /// instance first, and OnPlayerLeft destroys every image whose OwnerId matches the leaver.
        /// </summary>
        private static ushort LocalPlayerId()
        {
            return BasisNetworkPlayer.LocalPlayer != null
                ? BasisNetworkPlayer.LocalPlayer.playerId
                : UnownedPlayerId;
        }

        private static string LocalOwnerName()
        {
            return NormalizeOwnerNameForNetwork(
                BasisLocalPlayer.Instance != null
                    ? BasisLocalPlayer.Instance.SafeDisplayName
                    : "Unknown"
            );
        }

        private static void StartQueuedGifSpawns()
        {
            if (_queuedGifSpawns.Count == 0)
            {
                _gifDecodePausedForMemory = false;
                return;
            }
            if (BasisAnimatedImageData.ShouldPauseNewDecode())
            {
                LogGifDecodePausedForMemory();
                return;
            }

            while (
                _pendingGifSpawns.Count
                    < BasisImagePickupSettings.MaxConcurrentAnimationDecodeJobs
                && _queuedGifSpawns.Count > 0
            )
            {
                PendingGifSpawn pending = _queuedGifSpawns.Peek();
                if (!IsPlaceholderAlive(pending.Id, pending.Pickup))
                {
                    _queuedGifSpawns.Dequeue();
                    continue;
                }
                if (ShouldDeferGifDecodeForMemory(pending))
                {
                    LogGifDecodePausedForMemory();
                    return;
                }
                _queuedGifSpawns.Dequeue();
                try
                {
                    pending.Job = pending.Data != null
                        ? BasisAnimatedImageJobs.ScheduleGifDecode(pending.Data)
                        : BasisAnimatedImageJobs.ScheduleGifDecode(pending.Path);
                    _pendingGifSpawns.Add(pending);
                    _gifDecodePausedForMemory = false;
                    BasisDebug.Log(
                        $"Image pickup: started GIF Burst pipeline for "
                            + $"'{DescribeSpawn(pending.Path, pending.Label)}' "
                            + $"({_pendingGifSpawns.Count:N0}/"
                            + $"{BasisImagePickupSettings.MaxConcurrentAnimationDecodeJobs:N0} active, "
                            + $"{_queuedGifSpawns.Count:N0} waiting).",
                        LogTag
                    );
                }
                catch (BasisAnimationMemoryBudgetException)
                {
                    _queuedGifSpawns.Enqueue(pending);
                    LogGifDecodePausedForMemory();
                    return;
                }
                catch (Exception exception)
                {
                    RemoveImage(pending.Id);
                    string reason = BasisLocalization.Get(
                        "imagePickup.popup.reason.animationStartFailed",
                        exception.Message
                    );
                    BasisImagePickupRejectionPopup.Show(pending.Label, reason);
                    BasisDebug.LogWarning($"Image pickup rejected: {reason}", LogTag);
                }
            }
        }

        /// <summary>Names an import for logging: the file it came from, or what it was pasted as.</summary>
        private static string DescribeSpawn(string path, string label)
        {
            return string.IsNullOrEmpty(path) ? label : Path.GetFileName(path);
        }

        private static bool ShouldDeferGifDecodeForMemory(PendingGifSpawn pending)
        {
            try
            {
                long length = pending.Data != null
                    ? pending.Data.Length
                    : new FileInfo(pending.Path).Length;
                if (length <= 0 || length > BasisImagePickupSettings.MaxAnimationSourceBytes)
                {
                    return false;
                }

                long estimate = BasisAnimatedImageData.EstimateGifDecodeWorkingBytes((int)length);
                return !BasisAnimatedImageData.CanReserveWorkingBytes(estimate);
            }
            catch
            {
                // File validation reports missing, inaccessible, or changing files with the
                // normal import error instead of converting them into a memory deferral.
                return false;
            }
        }

        private static void LogGifDecodePausedForMemory()
        {
            if (_gifDecodePausedForMemory)
                return;
            _gifDecodePausedForMemory = true;
            long residentBytes =
                BasisAnimatedImageData.TotalResidentNativeBytes
                + BasisAnimatedImageData.TotalResidentCompositorBytes;
            BasisDebug.LogWarning(
                $"Image pickup paused queued GIF decoding at "
                    + $"{residentBytes / (1024L * 1024L):N0} MiB of resident "
                    + "animation and compositor data; reloadable offscreen animations "
                    + "will release resources until memory is available.",
                LogTag
            );
        }

        private static void ProcessCompletedGifSpawns()
        {
            int pendingGifSpawnCount = _pendingGifSpawns.Count;
            for (int index = pendingGifSpawnCount - 1; index >= 0; index--)
            {
                PendingGifSpawn pending = _pendingGifSpawns[index];
                BasisGifDecodeJobResult workerResult;
                try
                {
                    if (!pending.Job.TryComplete(out workerResult))
                        continue;
                }
                catch (Exception exception)
                {
                    _pendingGifSpawns.RemoveAt(index);
                    DisposeRequest(pending.Job, "GIF decode request");
                    RemoveImage(pending.Id);
                    string failureReason = BasisLocalization.Get(
                        "imagePickup.popup.reason.animationStartFailed",
                        exception.GetBaseException().Message
                    );
                    BasisImagePickupRejectionPopup.Show(pending.Path, failureReason);
                    BasisDebug.LogWarning(
                        $"Image pickup rejected after GIF completion failed: {failureReason}",
                        LogTag
                    );
                    continue;
                }
                _pendingGifSpawns.RemoveAt(index);

                try
                {
                    if (
                        BasisNetworkModeration.GlobalImagesLocked
                        && !BasisNetworkModeration.LocalPlayerHasGlobalLockBypass()
                    )
                    {
                        RemoveImage(pending.Id);
                        string lockedReason = BasisLocalization.Get(
                            "imagePickup.popup.reason.adminLockedDuringDecode"
                        );
                        BasisImagePickupRejectionPopup.Show(pending.Label, lockedReason);
                        BasisDebug.LogWarning($"Image pickup rejected: {lockedReason}", LogTag);
                        continue;
                    }

                    BasisImageValidationResult result =
                        BasisAnimatedImageJobs.FinalizeGifDecode(workerResult);

                    // Deleted while it decoded: the poster has nowhere to land, so drop it and stay silent.
                    if (!IsPlaceholderAlive(pending.Id, pending.Pickup))
                    {
                        DisposeRejectedValidationResult(ref result);
                        continue;
                    }

                    if (result.Ok)
                    {
                        double workerMilliseconds =
                            workerResult.WorkerElapsedTicks
                            * 1000d
                            / Stopwatch.Frequency;
                        BasisDebug.Log(
                            $"Image pickup: Burst GIF pipeline finished in {workerMilliseconds:0.###} ms "
                                + $"({result.Animation?.FrameCount ?? 1} frames).",
                            LogTag
                        );
                    }
                    SpawnValidatedFile(
                        pending.Label,
                        result,
                        pending.Position,
                        pending.Rotation,
                        pending.Id,
                        pending.Pickup
                    );
                }
                finally
                {
                    DisposeRequest(pending.Job, "GIF decode request");
                }
            }

            StartQueuedGifSpawns();
        }

        /// <summary>
        /// Completes a local spawn once its poster is decoded. <paramref name="placeholder"/> is the card the
        /// GIF path already raised at drop time — it is adopted in place so the pickup keeps the identity,
        /// pose, and grab state it accumulated while decoding; a null placeholder (the synchronous static-image
        /// path) builds the card here instead. Rejections tear the placeholder back down.
        /// </summary>
        private static bool SpawnValidatedFile(
            string label,
            BasisImageValidationResult result,
            Vector3 position,
            Quaternion rotation,
            Guid id,
            BasisImagePickupObject placeholder
        )
        {
            if (!result.Ok)
            {
                RemoveImage(id);
                BasisImagePickupRejectionPopup.Show(label, result.Error);
                BasisDebug.LogWarning($"Image pickup rejected: {result.Error}", LogTag);
                return false;
            }
            if (
                !CanAcceptLocalImage(
                    result.CleanPng?.Length ?? 0,
                    result.Width,
                    result.Height,
                    out string imageBudgetReason
                )
            )
            {
                DisposeRejectedValidationResult(ref result);
                RemoveImage(id);
                BasisImagePickupRejectionPopup.Show(label, imageBudgetReason);
                BasisDebug.LogWarning($"Image pickup rejected: {imageBudgetReason}", LogTag);
                return false;
            }

            ushort ownerId = LocalPlayerId();
            string ownerName = LocalOwnerName();

            BasisImagePickupObject pickup;
            if (placeholder != null)
            {
                pickup = placeholder;
                pickup.OwnerId = ownerId;
                pickup.OwnerName = ownerName;
                // The card was live while it decoded, so its current pose — not the drop pose — is the one
                // peers must spawn it at.
                pickup.transform.GetPositionAndRotation(out position, out rotation);
                pickup.ApplyLoadedImage(
                    result.Texture,
                    result.CleanPng,
                    result.HasAlpha,
                    result.Width,
                    result.Height
                );
            }
            else
            {
                pickup = BasisImagePickupObject.Build(
                    id,
                    ownerId,
                    ownerName,
                    true,
                    result.Width,
                    result.Height,
                    result.Texture,
                    result.CleanPng,
                    result.HasAlpha,
                    position,
                    rotation
                );
            }
            BasisNativeAnimationPayload animationPayload = null;
            long playbackEpochUtcTicks = 0;
            if (result.Animation != null && result.Animation.FrameCount <= 1)
            {
                result.TakeAnimation()?.Dispose();
                result.TakeAnimationPayload()?.Dispose();
            }

            BasisAnimatedImageData candidateAnimation = result.TakeAnimation();
            BasisNativeAnimationPayload candidatePayload =
                result.TakeAnimationPayload();
            if (candidateAnimation != null)
            {
                playbackEpochUtcTicks = BasisNetworkManagement.RemoteUtcTime().Ticks;
                int frameCount = candidateAnimation.FrameCount;
                if (candidatePayload == null)
                {
                    playbackEpochUtcTicks = 0;
                    BasisDebug.LogWarning(
                        $"Image pickup: GIF animation encoding failed; showing the poster frame only so decoded frame memory remains reclaimable: {result.AnimationNetworkError ?? "unknown error"}",
                        LogTag
                    );
                }
                else if (!CanAttachLocalAnimation(candidateAnimation, true, out string animationBudgetReason))
                {
                    playbackEpochUtcTicks = 0;
                    BasisDebug.LogWarning(
                        $"Image pickup animation omitted: {animationBudgetReason}; "
                            + "the sanitized poster remains available.",
                        LogTag
                    );
                }
                else if (pickup.TrySetAnimation(candidateAnimation, playbackEpochUtcTicks, candidatePayload))
                {
                    animationPayload = candidatePayload;
                    candidateAnimation = null;
                    candidatePayload = null;
                    bool decodedDataDeferred = pickup.AnimatedImagePlayer?.Data == null;
                    string attachmentMessage = decodedDataDeferred
                        ? "Image pickup: GIF animation retained as a compact payload and deferred to "
                            + $"the closest-animation decoded-data budget ({frameCount} frames, "
                            + $"{animationPayload.Length} payload bytes)."
                        : "Image pickup: GIF animation attached locally; Burst packet batches will use "
                            + $"the GIF source as the persistent native payload ({frameCount} frames, "
                            + $"{animationPayload.Length} payload bytes).";
                    BasisDebug.Log(attachmentMessage, LogTag);
                }
                else
                {
                    playbackEpochUtcTicks = 0;
                    BasisDebug.LogWarning(
                        "Image pickup: GIF decoded, but animated playback could not be attached; showing and replicating the poster frame only.",
                        LogTag
                    );
                }
            }
            candidateAnimation?.Dispose();
            candidatePayload?.Dispose();
            TrackImage(id, pickup);
            var owned = new OwnedImage
            {
                Object = pickup,
                CleanPng = result.CleanPng,
                Width = result.Width,
                Height = result.Height,
                OwnerName = ownerName,
                AnimationPayload = animationPayload,
                PlaybackEpochUtcTicks = playbackEpochUtcTicks,
            };
            _owned[id] = owned;

            RegisterShareable(id, result.Width, result.Height, ownerName);

            if (HasNetworkID)
            {
                GatherReplicationCandidates(ownerId);
                ushort[] recipients = SnapshotEligibleRecipients(
                    position,
                    ServerImagePickupRangeMeters(),
                    owned.SentRecipients
                );
                bool queued = QueueOwnedImageForRecipients(id, owned, ownerId, recipients);
                BasisDebug.Log(
                    queued
                        ? $"Image pickup spawned and queued for {recipients.Length:N0} in-range recipient(s) ({result.Width}x{result.Height}, {result.CleanPng.Length} poster bytes, {animationPayload?.Length ?? 0} animation bytes)."
                        : $"Image pickup spawned; no recipient queued yet, so it will replicate on the next range pass ({result.Width}x{result.Height}, {result.CleanPng.Length} poster bytes, {animationPayload?.Length ?? 0} animation bytes).",
                    LogTag
                );
            }
            else
            {
                BasisDebug.Log(
                    $"Image pickup spawned locally; not connected, so it will not replicate yet ({result.Width}x{result.Height}).",
                    LogTag
                );
            }
            return true;
        }

        /// <summary>
        /// Attaches validated decoded animation data to an existing image pickup.
        /// Decoder/network layers can call this after the static poster has spawned.
        /// </summary>
        public static bool TrySetAnimation(Guid id, BasisAnimatedImageData data, long playbackEpochUtcTicks = 0)
        {
            if (data == null)
                return false;
            if (
                !_images.TryGetValue(id, out BasisImagePickupObject pickup)
                || pickup == null
                || pickup.AnimatedImagePlayer != null
            )
                return false;

            string reason;
            bool withinBudget = _owned.ContainsKey(id)
                ? CanAttachLocalAnimation(data, false, out reason)
                : CanAttachRemoteAnimation(pickup.OwnerId, data, false, out reason);
            if (!withinBudget)
            {
                BasisDebug.LogWarning($"Image pickup animation could not be attached: {reason}.", LogTag);
                return false;
            }
            return pickup.TrySetAnimation(data, playbackEpochUtcTicks);
        }

        /// <summary>Removes an image for everyone. Any client may call this for any image.</summary>
        public static void RequestDespawn(Guid id)
        {
            if (HasNetworkID)
            {
                SendCustomNetworkEventDirect(EncodeDespawn(id), DeliveryMethod.ReliableOrdered, null);
            }
            RemoveImage(id);
        }

        /// <summary>Takes movement authority when this client grabs an image, demoting other clients to followers.</summary>
        public static void ClaimControl(Guid id)
        {
            if (!_images.TryGetValue(id, out BasisImagePickupObject pickup) || pickup == null)
                return;
            if (pickup.IsController)
                return;
            pickup.SetController(true);
            if (HasNetworkID)
            {
                SendCustomNetworkEventDirect(EncodeClaim(id), DeliveryMethod.ReliableOrdered, null);
            }
        }

        private static void SimulateUpdate()
        {
            try
            {
                SimulateUpdateBody();
            }
            catch (Exception exception)
            {
                BasisDebug.LogErrorOnce(
                    $"Image pickup manager simulation failed with {_images.Count:N0} images, "
                        + $"{_pendingGifSpawns.Count:N0} active GIF decodes, "
                        + $"{_pendingInboundAnimationDecodes.Count:N0} active inbound animation decodes, "
                        + $"and {_reservedInboundTransferBytes / (1024L * 1024L):N0} MiB reserved: "
                        + $"{exception}",
                    LogTag
                );
            }
        }

        /// <summary>
        /// Tracks a card and flags its back panel for the paced sync below. Cards are always born without a
        /// panel — raising one builds a world-space canvas, so that cost is queued like any other rather than
        /// paid inline on whatever frame the card happens to spawn.
        /// </summary>
        private static void TrackImage(Guid id, BasisImagePickupObject pickup)
        {
            _images[id] = pickup;
            _backPanelSyncPending = true;
        }

        /// <summary>
        /// Follows the main menu and shows the pickups' back-panel controls only while it is open. Polls
        /// rather than hooking an open/close event because the menu exposes none — it simply assigns and nulls
        /// its static instance — and because polling stays correct for every teardown path, including ones
        /// that never route through <c>BasisMainMenu.Close</c>. This mirrors how the nameplate driver gates
        /// its own menu-only work.
        ///
        /// Cards converge on the menu's state a few per frame, in both directions: toggling the menu in a busy
        /// instance would otherwise build or tear down every card's canvas at once, which is the very stall
        /// this gating exists to avoid. The scan stops as soon as every card agrees with the menu, so the
        /// steady-state cost is one static null check per frame.
        /// </summary>
        private static void UpdateBackPanelVisibility()
        {
            bool menuOpen = BasisMainMenu.Instance != null;
            if (menuOpen != _backPanelsVisible)
            {
                _backPanelsVisible = menuOpen;
                _backPanelSyncPending = true;
            }

            if (!_backPanelSyncPending)
                return;

            int budget = BasisImagePickupSettings.MaxBackPanelUpdatesPerFrame;
            foreach (BasisImagePickupObject pickup in _images.Values)
            {
                if (pickup == null || pickup.BackPanelVisible == _backPanelsVisible)
                    continue;
                pickup.SetBackPanelVisible(_backPanelsVisible);
                if (--budget <= 0)
                    return;
            }
            _backPanelSyncPending = false;
        }

        private static void SimulateUpdateBody()
        {
            RefreshServerRelayBudget();
            BasisImagePickupLinkProbe.Tick(Time.unscaledTime);
            BasisImagePickupBandwidth.Refill(Time.unscaledDeltaTime);

            if (
                _images.Count == 0
                && _inbound.Count == 0
                && _inboundAnimations.Count == 0
                && _outboundImages.Count == 0
                && _outboundAnimations.Count == 0
                && _queuedFileSpawns.Count == 0
                && _queuedGifSpawns.Count == 0
                && _pendingGifSpawns.Count == 0
                && _queuedInboundAnimationDecodes.Count == 0
                && _pendingInboundAnimationDecodes.Count == 0
                && _pendingOffers.Count == 0
            )
            {
                BasisImagePickupProgressGizmos.Shutdown();
                return;
            }

            UpdateBackPanelVisibility();
            ProcessQueuedFileSpawns();
            ProcessCompletedGifSpawns();
            ProcessCompletedInboundAnimationDecodes();
            StartQueuedInboundAnimationDecodes();

            float deltaTime = Time.deltaTime;
            bool transmit = HasNetworkID;
            float now = transmit ? Time.unscaledTime : 0f;
            float interval = 1f / BasisImagePickupSettings.TransmitTransformHz;

            if (transmit)
                ScheduleOfferRangeCheck(now);

            // One pass over the tracked cards handles destroyed-entry sweep, remote interpolation, and
            // controller transform transmission together. Externally destroyed pickups (scene unloads that
            // bypass OnPickupDestroyed) are collected here and removed after the enumeration; every earlier
            // step this frame already skips Unity-null entries.
            _scratchIds.Clear();
            foreach (KeyValuePair<Guid, BasisImagePickupObject> entry in _images)
            {
                BasisImagePickupObject pickup = entry.Value;
                if (pickup == null)
                {
                    _scratchIds.Add(entry.Key);
                    continue;
                }
                pickup.SimulateRemoteTransform(deltaTime);

                if (!transmit || !pickup.IsController)
                    continue;
                // A local card that is still decoding has not been announced to anyone yet, so peers have no
                // image to move. Its spawn header carries the pose it ends up at, and the first post-decode
                // tick sends a transform anyway because LastSent* still hold their defaults.
                if (pickup.IsOwner && pickup.IsLoading)
                    continue;
                if (now - pickup.LastSendTime < interval)
                    continue;

                pickup.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                float scale = pickup.transform.localScale.x;
                bool moved =
                    (position - pickup.LastSentPosition).sqrMagnitude
                        > BasisImagePickupSettings.MovedPositionEpsilon
                            * BasisImagePickupSettings.MovedPositionEpsilon
                    || Quaternion.Angle(rotation, pickup.LastSentRotation)
                        > BasisImagePickupSettings.MovedRotationEpsilonDegrees
                    || Mathf.Abs(scale - pickup.LastSentScale)
                        > BasisImagePickupSettings.MovedScaleEpsilon;
                if (!moved)
                    continue;

                pickup.LastSendTime = now;
                pickup.LastSentPosition = position;
                pickup.LastSentRotation = rotation;
                pickup.LastSentScale = scale;
                SendCustomNetworkEventDirect(
                    EncodeTransform(entry.Key, position, rotation, scale),
                    DeliveryMethod.ReliableOrdered,
                    null
                );
            }
            int destroyedImageCount = _scratchIds.Count;
            if (destroyedImageCount > 0)
            {
                BasisDebug.LogWarning(
                    $"Image pickup dropped {destroyedImageCount:N0} card(s) destroyed from outside the "
                        + "manager. Cards are DontDestroyOnLoad, so a scene unload should no longer be able "
                        + "to take them; anything reaching here is another owner of their lifetime.",
                    LogTag
                );
            }
            for (int i = 0; i < destroyedImageCount; i++)
                RemoveImage(_scratchIds[i], false);
            _scratchIds.Clear();

            if (!transmit)
            {
                BasisImagePickupProgressGizmos.Shutdown();
                return;
            }

            RefreshRangeRecipients(now);

            ProcessOutboundImageTransfers();
            ProcessOutboundAnimationTransfers();
            CleanupExpiredTransfers(now);
            CompleteOfferRangeCheck();
#if !UNITY_SERVER
            UpdateTransferProgressGizmos(now);
#endif
        }

        /// <summary>
        /// Picks up the egress budget the server advertised in the join handshake, in whatever units the
        /// operator configured it: megabits per second, because that is how a server's line is sold, rather
        /// than the bytes per second the bucket meters in.
        ///
        /// Read every tick instead of once on join so a budget that arrives after this manager arms — or is
        /// re-sent when permissions change — is picked up without needing an event of its own. Zero means no
        /// server has said anything and the fallback in <see cref="BasisImagePickupSettings"/> stands.
        /// </summary>
        private static void RefreshServerRelayBudget()
        {
            int advertisedMegabits = BasisNetworkManagement
                .ServerMetaDataMessage
                .ImageShareEgressMegabitsPerSecond;
            BasisImagePickupBandwidth.ServerRelayBudgetBytesPerSecond =
                advertisedMegabits > 0 ? advertisedMegabits * 125_000L : 0L;
        }

        /// <summary>
        /// Splits one cohort between direct links and the relay. Peers on a connected P2P session are
        /// reached directly and cost the server nothing; everyone else is forwarded, and the server pays
        /// for each of them separately. Every queued transfer carries a non-empty cohort, so there is no
        /// broadcast case to fall back to.
        /// </summary>
        private static void CountRecipients(ushort[] recipients, out int directCount, out int relayCount)
        {
            directCount = 0;
            relayCount = 0;
            int recipientCount = recipients.Length;
            for (int i = 0; i < recipientCount; i++)
            {
                if (
                    BasisP2PManager.GetSessionState(recipients[i])
                    == BasisP2PManager.P2PSessionState.Connected
                )
                    directCount++;
                else
                    relayCount++;
            }
        }

        /// <summary>
        /// Books one bulk packet against the share budget. Headers, transforms, claims, and despawns are
        /// not metered: they are a few dozen bytes and hold up the interactive feel of a card.
        /// </summary>
        private static bool TryReserveSendBandwidth(int payloadBytes, ushort[] recipients)
        {
            CountRecipients(recipients, out int directCount, out int relayCount);
            return BasisImagePickupBandwidth.TryConsume(
                payloadBytes + BasisNetworkGenericMessages.SceneDataFramingBytes(recipients),
                directCount,
                relayCount
            );
        }

        private static void UpdateTransferProgressGizmos(float now)
        {
            if (
                _inbound.Count == 0
                && _inboundAnimations.Count == 0
                && _outboundImages.Count == 0
                && _outboundAnimations.Count == 0
            )
            {
                BasisImagePickupProgressGizmos.Shutdown();
                return;
            }

            BasisImagePickupProgressGizmos.BeginFrame();

            foreach (KeyValuePair<Guid, InboundTransfer> entry in _inbound)
            {
                InboundTransfer transfer = entry.Value;
                transfer.Rate.Sample(now);
                ReportTransferProgress(
                    entry.Key,
                    transfer.Rate.Fraction(transfer.Buffer != null ? transfer.Buffer.Length : 0),
                    transfer.Rate.BytesPerSecond,
                    false
                );
            }

            foreach (KeyValuePair<Guid, InboundAnimationTransfer> entry in _inboundAnimations)
            {
                // The poster lands first and owns the card's readout while it is still arriving.
                if (_inbound.ContainsKey(entry.Key))
                    continue;
                InboundAnimationTransfer transfer = entry.Value;
                transfer.Rate.Sample(now);
                ReportTransferProgress(
                    entry.Key,
                    transfer.Rate.Fraction(transfer.Buffer.IsCreated ? transfer.Buffer.Length : 0),
                    transfer.Rate.BytesPerSecond,
                    false
                );
            }

            foreach (OutboundImageTransfer transfer in _outboundImages)
            {
                transfer.Rate.Sample(now);
                ReportTransferProgress(
                    transfer.Id,
                    transfer.Rate.Fraction(transfer.Png != null ? transfer.Png.Length : 0),
                    transfer.Rate.BytesPerSecond,
                    true
                );
            }

            foreach (OutboundAnimationTransfer transfer in _outboundAnimations)
            {
                if (HasPendingOutboundImageTransfer(transfer.Id, transfer.Recipients))
                    continue;
                transfer.Rate.Sample(now);
                ReportTransferProgress(
                    transfer.Id,
                    transfer.Rate.Fraction(
                        transfer.Payload != null && transfer.Payload.IsCreated
                            ? transfer.Payload.Length
                            : 0
                    ),
                    transfer.Rate.BytesPerSecond,
                    true
                );
            }

            BasisImagePickupProgressGizmos.EndFrame();
        }

        private static void ReportTransferProgress(
            Guid id,
            float progress,
            float bytesPerSecond,
            bool outbound
        )
        {
            if (
                !_images.TryGetValue(id, out BasisImagePickupObject pickup)
                || pickup == null
                || pickup.IsHidden
            )
                return;
            BasisImagePickupProgressGizmos.Report(
                id,
                pickup.TransferLabelAnchor,
                progress,
                bytesPerSecond,
                outbound
            );
        }

        internal static bool IsWithinReplicationRange(
            Vector3 imagePosition,
            Vector3 playerPosition,
            float rangeMeters
        )
        {
            if (rangeMeters <= 0f)
                return true;
            float rangeSq = rangeMeters * rangeMeters;
            return (imagePosition - playerPosition).sqrMagnitude <= rangeSq;
        }

        internal static bool RecipientSnapshotsMatch(ushort[] left, ushort[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }
            return true;
        }

        internal static ushort[] RemoveRecipientFromSnapshot(ushort[] recipients, ushort recipient)
        {
            if (recipients == null)
                return Array.Empty<ushort>();
            if (recipients.Length == 0)
                return recipients;
            int index = Array.IndexOf(recipients, recipient);
            if (index < 0)
                return recipients;
            if (recipients.Length == 1)
                return Array.Empty<ushort>();

            ushort[] reduced = new ushort[recipients.Length - 1];
            if (index > 0)
                Array.Copy(recipients, 0, reduced, 0, index);
            if (index < recipients.Length - 1)
                Array.Copy(recipients, index + 1, reduced, index, recipients.Length - index - 1);
            return reduced;
        }

        private static bool TryGetReplicationPlayerPosition(IBasisPlayer basisPlayer, out Vector3 position)
        {
            position = default;
            if (basisPlayer == null || basisPlayer.IsDestroyed)
                return false;

            // Unity's null is an overloaded operator rather than a reference test, so ?? hands back a
            // destroyed transform and skips the fallbacks that exist for exactly that case.
            Transform anchor = basisPlayer.Transform;
            if (anchor == null)
                anchor = basisPlayer.AvatarTransform;
            if (anchor == null)
                anchor = basisPlayer.PlayerSelf;
            if (anchor == null)
                return false;

            position = anchor.position;
            return true;
        }

        private static float ServerImagePickupRangeMeters()
        {
            float rangeMeters = Mathf.Max(0f, BasisNetworkManagement.ServerMetaDataMessage.ImagePickupRangeMeters);
            ReportReplicationRange(rangeMeters);
            return rangeMeters;
        }

        /// <summary>
        /// Says out loud what range is actually in force, once per change. The range is a number the
        /// server advertises in the join handshake, so every way this feature fails silently - an older
        /// server, a server with the setting at 0, a handshake that never landed - looks identical from
        /// in the world: every image loads at every distance. Printing the figure separates "the range
        /// is not being applied" from "the range is unlimited because nobody set one".
        /// </summary>
        private static void ReportReplicationRange(float rangeMeters)
        {
            if (rangeMeters == _lastReportedRangeMeters)
                return;
            _lastReportedRangeMeters = rangeMeters;

            if (rangeMeters <= 0f)
            {
                BasisDebug.LogWarning(
                    "Image pickup replication range is UNLIMITED - every image replicates and loads at any "
                        + "distance. The server advertised ImagePickupRangeMeters=0, or it is old enough not to "
                        + "send the field at all. Set ImagePickupRangeMeters in the server config to enable "
                        + "distance-based loading.",
                    LogTag
                );
                return;
            }

            BasisDebug.Log(
                $"Image pickup replication range is {rangeMeters:0.##}m (advertised by the server).",
                LogTag
            );
        }

        /// <summary>
        /// Reads every other player's anchor once per pass rather than once per image: eight owned cards
        /// in a forty-player instance is forty transform reads this way and three hundred and twenty
        /// without.
        /// </summary>
        private static void GatherReplicationCandidates(ushort localId)
        {
            _scratchCandidates.Clear();
            foreach (KeyValuePair<ushort, BasisNetworkPlayer> entry in BasisNetworkPlayers.Players)
            {
                if (entry.Key == localId)
                    continue;
                BasisNetworkPlayer networkPlayer = entry.Value;
                if (networkPlayer == null || !networkPlayer.TryGetPlayer(out IBasisPlayer basisPlayer))
                    continue;
                if (!TryGetReplicationPlayerPosition(basisPlayer, out Vector3 playerPosition))
                    continue;
                _scratchCandidates.Add(
                    new ReplicationCandidate { PlayerId = entry.Key, Position = playerPosition }
                );
            }
        }

        /// <summary>
        /// Picks the players a card still owes a copy to, sorted so two cohorts with the same membership
        /// compare equal. Reads <paramref name="alreadySent"/> and never writes it, so a cohort that
        /// fails to queue leaves nothing behind claiming those players already hold the image.
        /// </summary>
        internal static void SelectEligibleRecipients(
            List<ReplicationCandidate> candidates,
            Vector3 imagePosition,
            float rangeMeters,
            HashSet<ushort> alreadySent,
            List<ushort> results
        )
        {
            results.Clear();
            if (candidates == null)
                return;
            int candidateCount = candidates.Count;
            for (int i = 0; i < candidateCount; i++)
            {
                ReplicationCandidate candidate = candidates[i];
                if (!IsWithinReplicationRange(imagePosition, candidate.Position, rangeMeters))
                    continue;
                if (alreadySent != null && alreadySent.Contains(candidate.PlayerId))
                    continue;
                results.Add(candidate.PlayerId);
            }
            results.Sort();
        }

        private static ushort[] SnapshotEligibleRecipients(
            Vector3 imagePosition,
            float rangeMeters,
            HashSet<ushort> alreadySent
        )
        {
            SelectEligibleRecipients(
                _scratchCandidates,
                imagePosition,
                rangeMeters,
                alreadySent,
                _scratchRecipientIds
            );
            return _scratchRecipientIds.ToArray();
        }

        private static void MarkRecipientsSent(HashSet<ushort> sentRecipients, ushort[] recipients)
        {
            if (sentRecipients == null || recipients == null)
                return;
            int recipientCount = recipients.Length;
            for (int i = 0; i < recipientCount; i++)
                sentRecipients.Add(recipients[i]);
        }

        /// <summary>
        /// Queues one cohort and records it as served only once it is on the queue, so a send that never
        /// leaves - no owner id yet, no poster bytes - is retried on the next pass instead of stranding
        /// those players with an image marked delivered and never sent.
        /// </summary>
        private static bool QueueOwnedImageForRecipients(
            Guid id,
            OwnedImage owned,
            ushort ownerId,
            ushort[] recipients
        )
        {
            if (owned?.Object == null || recipients == null || recipients.Length == 0)
                return false;
            if (ownerId == UnownedPlayerId)
                return false;

            owned.Object.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            if (
                !SendSpawn(
                    id,
                    ownerId,
                    owned.OwnerName,
                    owned.Width,
                    owned.Height,
                    owned.CleanPng,
                    position,
                    rotation,
                    recipients
                )
            )
                return false;

            MarkRecipientsSent(owned.SentRecipients, recipients);
            if (owned.AnimationPayload != null && owned.PlaybackEpochUtcTicks > 0)
                SendAnimation(id, owned, recipients);
            return true;
        }

        private static void RefreshRangeRecipients(float now)
        {
            if (now < _nextRecipientRangeRefreshTime)
                return;
            _nextRecipientRangeRefreshTime = now + BasisImagePickupSettings.RecipientRangeRefreshSeconds;
            if (_owned.Count == 0)
                return;

            // Recipients key cleanup off this owner id. Do not use 0 as a sentinel because player 0 is valid.
            ushort ownerId = LocalPlayerId();
            if (ownerId == UnownedPlayerId)
                return;

            GatherReplicationCandidates(ownerId);
            if (_scratchCandidates.Count == 0)
                return;

            float rangeMeters = ServerImagePickupRangeMeters();
            foreach (KeyValuePair<Guid, OwnedImage> entry in _owned)
            {
                OwnedImage owned = entry.Value;
                if (owned?.Object == null)
                    continue;

                // The server holds this one and offers it to everyone else itself, so re-uploading it
                // is pure waste. It tells us the moment it stops holding it and we start providing again.
                if (_serverHeldImages.Contains(entry.Key))
                    continue;

                ushort[] recipients = SnapshotEligibleRecipients(
                    owned.Object.transform.position,
                    rangeMeters,
                    owned.SentRecipients
                );
                QueueOwnedImageForRecipients(entry.Key, owned, ownerId, recipients);
            }
        }

        private static void OnPlayerJoined(BasisNetworkPlayer player)
        {
            if (player == null)
                return;

            // HandleLocalPlayerJoined is the ordinary way in, but it runs off events a connection that dropped
            // hard never raises. OnPlayerJoined is raised for the local player too, so checking here confirms the
            // id we hold was issued by the server we are actually on and re-resolves it when it was not.
            EnsureNetworkIdentity();

            if (_owned.Count == 0)
                return;
            // Let the next range pass batch every newly eligible player into one cohort rather than
            // queueing a separate one-player transfer per arrival.
            _nextRecipientRangeRefreshTime = 0f;
        }

        private static void OnPlayerLeft(BasisNetworkPlayer player)
        {
            if (player == null)
                return;
            ushort left = player.playerId;

            _scratchIds.Clear();
            foreach (KeyValuePair<Guid, BasisImagePickupObject> entry in _images)
            {
                if (entry.Value != null && entry.Value.OwnerId == left)
                    _scratchIds.Add(entry.Key);
            }
            int ownedImageCount = _scratchIds.Count;
            if (ownedImageCount > 0)
            {
                BasisDebug.Log(
                    $"Image pickup removed {ownedImageCount:N0} image(s) because their owner "
                        + $"({left}) left.",
                    LogTag
                );
            }
            for (int i = 0; i < ownedImageCount; i++)
                RemoveImage(_scratchIds[i]);

            _scratchIds.Clear();
            foreach (KeyValuePair<Guid, InboundTransfer> entry in _inbound)
            {
                if (entry.Value.Sender == left)
                    _scratchIds.Add(entry.Key);
            }
            int inboundTransferCount = _scratchIds.Count;
            for (int i = 0; i < inboundTransferCount; i++)
                RemoveInboundTransfer(_scratchIds[i]);

            _scratchIds.Clear();
            foreach (KeyValuePair<Guid, InboundAnimationTransfer> entry in _inboundAnimations)
            {
                if (entry.Value.Sender == left)
                    _scratchIds.Add(entry.Key);
            }
            int inboundAnimationTransferCount = _scratchIds.Count;
            for (int i = 0; i < inboundAnimationTransferCount; i++)
                RemoveInboundAnimationTransfer(_scratchIds[i]);

            int queuedInboundDecodeCount = _queuedInboundAnimationDecodes.Count;
            for (int i = queuedInboundDecodeCount - 1; i >= 0; i--)
            {
                QueuedInboundAnimationDecode queued = _queuedInboundAnimationDecodes[i];
                if (queued.Sender != left)
                    continue;
                queued.Payload?.Dispose();
                ReleaseInboundTransferBytes(queued.ReservedBytes);
                queued.ReservedBytes = 0;
                _animationAttempted.Remove(queued.Id);
                _queuedInboundAnimationDecodes.RemoveAt(i);
            }

            int pendingInboundDecodeCount = _pendingInboundAnimationDecodes.Count;
            for (int i = pendingInboundDecodeCount - 1; i >= 0; i--)
            {
                PendingInboundAnimationDecode pending = _pendingInboundAnimationDecodes[i];
                if (pending.Sender != left)
                    continue;
                pending.Job?.Dispose();
                pending.Payload?.Dispose();
                ReleaseInboundTransferBytes(pending.ReservedBytes);
                pending.ReservedBytes = 0;
                _animationAttempted.Remove(pending.Id);
                _pendingInboundAnimationDecodes.RemoveAt(i);
            }

            foreach (OwnedImage owned in _owned.Values)
                owned?.SentRecipients.Remove(left);

            RemoveOutboundImageTransfersForRecipient(left);
            RemoveOutboundAnimationTransfersForRecipient(left);
            _spawnRateBySender.Remove(left);
        }

        public static void OnDirectNetworkMessage(ushort senderId, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (buffer == null || buffer.Length < 1)
                return;

            using var stream = new MemoryStream(buffer, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            byte opcode = reader.ReadByte();
            try
            {
                switch (opcode)
                {
                    case OpSpawn:
                        HandleSpawn(senderId, reader);
                        break;
                    case OpChunk:
                        HandleChunk(senderId, reader);
                        break;
                    case OpTransform:
                        HandleTransform(senderId, reader);
                        break;
                    case OpClaim:
                        HandleClaim(senderId, reader);
                        break;
                    case OpDespawn:
                        HandleDespawn(senderId, reader);
                        break;
                    case OpAnimationSpawn:
                        HandleAnimationSpawn(senderId, reader);
                        break;
                    case OpAnimationChunk:
                        HandleAnimationChunk(senderId, reader);
                        break;
                    case OpServerCacheState:
                        HandleServerCacheState(senderId, reader);
                        break;
                    case OpServerCacheOffer:
                        HandleServerCacheOffer(senderId, reader);
                        break;
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"Image pickup: malformed message from {senderId} ({e.Message}).", LogTag);
            }
        }

        private static void HandleSpawn(ushort senderId, BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            reader.ReadUInt16();
            if (!TrySkipWireString(reader, MaxIgnoredOwnerNameBytes)) return;
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            int totalBytes = reader.ReadInt32();
            int totalChunks = reader.ReadInt32();
            Vector3 position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Quaternion rotation = new Quaternion(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );

            if (_images.ContainsKey(id) || _inbound.ContainsKey(id))
                return;
            _pendingOffers.Remove(id);

            if (!CanAcceptSpawn(senderId, totalBytes, width, height, totalChunks, out string reason))
            {
                BasisDebug.LogWarning($"Image pickup from {senderId} dropped: {reason}.", LogTag);
                return;
            }

            if (!TryReserveInboundTransferBytes(totalBytes, out reason))
            {
                BasisDebug.LogWarning($"Image pickup from {senderId} dropped: {reason}.", LogTag);
                return;
            }

            string ownerName = ResolveOwnerName(senderId);
            try
            {
                _inbound[id] = new InboundTransfer
                {
                    Sender = senderId,
                    Id = id,
                    Buffer = new byte[totalBytes],
                    ReservedBytes = totalBytes,
                    Received = new bool[totalChunks],
                    ReceivedCount = 0,
                    TotalChunks = totalChunks,
                    Width = width,
                    Height = height,
                    OwnerId = senderId,
                    OwnerName = ownerName,
                    Deadline =
                        Time.unscaledTime
                        + BasisImagePickupSettings.InboundTransferTimeoutSeconds,
                    Position = position,
                    Rotation = rotation,
                    LastProgressTime = Time.unscaledTime,
                };
            }
            catch
            {
                ReleaseInboundTransferBytes(totalBytes);
                throw;
            }

            // Raise the card now rather than when the last chunk lands. The header already carries the pose
            // and the poster's dimensions, so the receiver can place a correctly shaped card immediately and
            // then apply transform, claim, and despawn messages for it throughout the transfer, instead of
            // discarding them and finally popping the image in at a pose the sender has since moved away from.
            try
            {
                BuildLoadingPickup(id, senderId, ownerName, false, width, height, position, rotation);
            }
            catch
            {
                RemoveInboundTransfer(id);
                throw;
            }
        }

        private static void HandleChunk(ushort senderId, BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            int chunkIndex = reader.ReadInt32();
            int length = reader.ReadInt32();

            if (!_inbound.TryGetValue(id, out InboundTransfer transfer))
            {
                // Late chunks for a transfer that already finished are normal; ones for a transfer that was
                // torn down are how a card silently stops loading, so say it at least once.
                BasisDebug.LogWarningOnce(
                    "BasisImagePickup.ChunkWithoutTransfer",
                    $"Image pickup received chunk {chunkIndex} from {senderId} for an image it is not "
                        + "receiving. Expected right after a transfer completes; otherwise the transfer was "
                        + "dropped while the sender was still sending.",
                    LogTag
                );
                return;
            }
            if (transfer.Sender != senderId)
            {
                LogChunkRejected(transfer, chunkIndex, $"it came from {senderId}, not the owner");
                return;
            }
            if (chunkIndex < 0 || chunkIndex >= transfer.TotalChunks)
            {
                LogChunkRejected(
                    transfer,
                    chunkIndex,
                    $"the index is outside 0..{transfer.TotalChunks - 1}"
                );
                return;
            }
            if (length <= 0 || length > BasisImagePickupSettings.ChunkPayloadBytes)
            {
                LogChunkRejected(transfer, chunkIndex, $"it claims an impossible length of {length}");
                return;
            }

            int offset = chunkIndex * BasisImagePickupSettings.ChunkPayloadBytes;
            if (offset < 0 || offset >= transfer.Buffer.Length)
            {
                LogChunkRejected(
                    transfer,
                    chunkIndex,
                    $"offset {offset} falls outside the {transfer.Buffer.Length}-byte image"
                );
                return;
            }
            int expectedLength = Mathf.Min(BasisImagePickupSettings.ChunkPayloadBytes, transfer.Buffer.Length - offset);
            if (length != expectedLength)
            {
                LogChunkRejected(
                    transfer,
                    chunkIndex,
                    $"it claims {length} bytes where {expectedLength} were expected"
                );
                return;
            }

            long remainingBytes = reader.BaseStream.Length - reader.BaseStream.Position;
            if (remainingBytes < length)
            {
                LogChunkRejected(
                    transfer,
                    chunkIndex,
                    $"the packet carries {remainingBytes} of the {length} bytes it claims"
                );
                return;
            }

            byte[] data = reader.ReadBytes(length);
            if (data.Length != length)
            {
                LogChunkRejected(
                    transfer,
                    chunkIndex,
                    $"only {data.Length} of {length} bytes could be read"
                );
                return;
            }

            if (!transfer.Received[chunkIndex])
            {
                transfer.Deadline =
                    Time.unscaledTime
                    + BasisImagePickupSettings.InboundTransferTimeoutSeconds;
                transfer.LastProgressTime = Time.unscaledTime;
                Buffer.BlockCopy(data, 0, transfer.Buffer, offset, length);
                transfer.Received[chunkIndex] = true;
                transfer.ReceivedCount++;
                transfer.Rate.MovedBytes += length;
            }

            if (transfer.ReceivedCount >= transfer.TotalChunks)
                FinalizeTransfer(transfer);
        }

        /// <summary>
        /// Reports the first chunk a transfer refuses, and only the first: every rejection path here leaves
        /// the transfer's deadline unrefreshed, so a systematic one ends with the card being removed 30
        /// seconds later having explained nothing.
        /// </summary>
        private static void LogChunkRejected(InboundTransfer transfer, int chunkIndex, string reason)
        {
            if (transfer.RejectionLogged)
                return;
            transfer.RejectionLogged = true;
            BasisDebug.LogWarning(
                $"Image pickup rejected chunk {chunkIndex} of {transfer.TotalChunks:N0} from "
                    + $"{transfer.Sender} because {reason}. The transfer has "
                    + $"{transfer.ReceivedCount:N0} chunks and will be dropped if it stops making progress.",
                LogTag
            );
        }

        private static async void FinalizeTransfer(InboundTransfer transfer)
        {
            _inbound.Remove(transfer.Id);
            ReleaseInboundTransferBytes(transfer.ReservedBytes);
            transfer.ReservedBytes = 0;

            BasisImageValidationResult result = await BasisImageSecurity.ValidateBytesAsync(transfer.Buffer);
            if (!result.Ok)
            {
                RemoveImage(transfer.Id);
                BasisDebug.LogWarning(
                    $"Image pickup from {transfer.Sender} failed validation: {result.Error}.",
                    LogTag
                );
                return;
            }
            if (
                !MatchesClaimedDimensions(
                    transfer.Width,
                    transfer.Height,
                    result.Width,
                    result.Height
                )
            )
            {
                DisposeRejectedValidationResult(ref result);
                RemoveImage(transfer.Id);
                BasisDebug.LogWarning(
                    $"Image pickup from {transfer.Sender} failed validation: "
                        + $"claimed {transfer.Width}x{transfer.Height}, decoded "
                        + $"{result.Width}x{result.Height}.",
                    LogTag
                );
                return;
            }

            // The card was deleted, or its sender left, while the bytes were still arriving.
            if (
                !_images.TryGetValue(transfer.Id, out BasisImagePickupObject pickup)
                || pickup == null
            )
            {
                DisposeRejectedValidationResult(ref result);
                return;
            }

            pickup.ApplyLoadedImage(
                result.Texture,
                result.CleanPng,
                result.HasAlpha,
                result.Width,
                result.Height
            );
        }

        private static void HandleAnimationSpawn(ushort senderId, BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            byte format = reader.ReadByte();
            int totalBytes = reader.ReadInt32();
            int totalChunks = reader.ReadInt32();
            long playbackEpochUtcTicks = reader.ReadInt64();

            if (format != AnimationFormatNativeLz4 && format != AnimationFormatGif)
            {
                BasisDebug.LogWarningOnce(
                    "BasisImagePickup.UnsupportedAnimationFormat",
                    $"Image pickup ignored unsupported animation format {format} from "
                        + $"player {senderId}.",
                    LogTag
                );
                return;
            }
            if (_inboundAnimations.ContainsKey(id) || _animationAttempted.Contains(id))
                return;
            if (!CanAcceptAnimation(senderId, id, totalBytes, totalChunks, playbackEpochUtcTicks, out string reason))
            {
                BasisDebug.LogWarning($"Image pickup animation from {senderId} dropped: {reason}.", LogTag);
                return;
            }

            if (!TryReserveInboundTransferBytes(totalBytes, out reason))
            {
                BasisDebug.LogWarning($"Image pickup animation from {senderId} dropped: {reason}.", LogTag);
                return;
            }

            NativeArray<byte> buffer = default;
            NativeArray<byte> received = default;
            try
            {
                buffer = new NativeArray<byte>(
                    totalBytes,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                received = new NativeArray<byte>(totalChunks, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _inboundAnimations[id] = new InboundAnimationTransfer
                {
                    Sender = senderId,
                    Id = id,
                    Format = format,
                    Buffer = buffer,
                    ReservedBytes = totalBytes,
                    Received = received,
                    ReceivedCount = 0,
                    TotalChunks = totalChunks,
                    PlaybackEpochUtcTicks = playbackEpochUtcTicks,
                    Deadline =
                        Time.unscaledTime
                        + BasisImagePickupSettings.InboundTransferTimeoutSeconds,
                };
                buffer = default;
                received = default;
                _animationAttempted.Add(id);
            }
            catch (Exception exception)
            {
                if (buffer.IsCreated)
                    buffer.Dispose();
                if (received.IsCreated)
                    received.Dispose();
                ReleaseInboundTransferBytes(totalBytes);
                BasisDebug.LogWarning(
                    $"Image pickup animation from {senderId} could not allocate its native transfer "
                        + $"buffers ({exception.Message}).",
                    LogTag
                );
            }
        }

        private static void HandleAnimationChunk(ushort senderId, BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            int chunkIndex = reader.ReadInt32();
            int length = reader.ReadInt32();

            if (!_inboundAnimations.TryGetValue(id, out InboundAnimationTransfer transfer))
                return;
            if (transfer.Sender != senderId)
                return;
            if (chunkIndex < 0 || chunkIndex >= transfer.TotalChunks)
                return;
            if (length <= 0 || length > BasisImagePickupSettings.ChunkPayloadBytes)
                return;

            int offset = chunkIndex * BasisImagePickupSettings.ChunkPayloadBytes;
            if (offset < 0 || offset >= transfer.Buffer.Length)
                return;
            int expectedLength = Mathf.Min(BasisImagePickupSettings.ChunkPayloadBytes, transfer.Buffer.Length - offset);
            if (length != expectedLength)
                return;

            byte[] data = reader.ReadBytes(length);
            if (data.Length != length)
                return;

            if (transfer.Received[chunkIndex] == 0)
            {
                transfer.Deadline =
                    Time.unscaledTime
                    + BasisImagePickupSettings.InboundTransferTimeoutSeconds;
                NativeArray<byte>.Copy(data, 0, transfer.Buffer, offset, length);
                transfer.Received[chunkIndex] = 1;
                transfer.ReceivedCount++;
                transfer.Rate.MovedBytes += length;
            }

            if (transfer.ReceivedCount >= transfer.TotalChunks)
                FinalizeAnimationTransfer(transfer);
        }

        private static void FinalizeAnimationTransfer(InboundAnimationTransfer transfer)
        {
            _inboundAnimations.Remove(transfer.Id);
            if (transfer.Received.IsCreated)
                transfer.Received.Dispose();

            if (
                !_images.TryGetValue(transfer.Id, out BasisImagePickupObject pickup)
                || pickup == null
                || pickup.OwnerId != transfer.Sender
                || pickup.AnimatedImagePlayer != null
            )
            {
                if (transfer.Buffer.IsCreated)
                    transfer.Buffer.Dispose();
                ReleaseInboundTransferBytes(transfer.ReservedBytes);
                transfer.ReservedBytes = 0;
                _animationAttempted.Remove(transfer.Id);
                return;
            }

            if (!TryReadInboundAnimationDecodedBytes(transfer, out int decodedBytes, out string headerError))
            {
                if (transfer.Buffer.IsCreated)
                    transfer.Buffer.Dispose();
                ReleaseInboundTransferBytes(transfer.ReservedBytes);
                transfer.ReservedBytes = 0;
                _animationAttempted.Remove(transfer.Id);
                BasisDebug.LogWarning($"Image pickup animation from {transfer.Sender} dropped: {headerError}", LogTag);
                return;
            }
            if (!FitsInboundAnimationDecodeBudget(0, 0, decodedBytes))
            {
                if (transfer.Buffer.IsCreated)
                    transfer.Buffer.Dispose();
                ReleaseInboundTransferBytes(transfer.ReservedBytes);
                transfer.ReservedBytes = 0;
                _animationAttempted.Remove(transfer.Id);
                BasisDebug.LogWarning(
                    $"Image pickup animation from {transfer.Sender} dropped: decoded "
                        + "payload exceeds the per-sender native decode limit.",
                    LogTag
                );
                return;
            }

            int payloadBytes = transfer.Buffer.Length;
            if (!BasisNativeAnimationPayload.TryReserveBytes(payloadBytes, out string payloadError))
            {
                transfer.Buffer.Dispose();
                ReleaseInboundTransferBytes(transfer.ReservedBytes);
                transfer.ReservedBytes = 0;
                _animationAttempted.Remove(transfer.Id);
                BasisDebug.LogWarning(
                    $"Image pickup animation from {transfer.Sender} dropped: {payloadError}",
                    LogTag
                );
                return;
            }

            BasisNativeAnimationPayload payload = null;
            bool payloadReservationHeld = true;
            try
            {
                payload = new BasisNativeAnimationPayload(transfer.Buffer, payloadBytes, true, transfer.Format);
                payloadReservationHeld = false;
                transfer.Buffer = default;
                _queuedInboundAnimationDecodes.Add(
                    new QueuedInboundAnimationDecode
                    {
                        Sender = transfer.Sender,
                        Id = transfer.Id,
                        Payload = payload,
                        ReservedBytes = transfer.ReservedBytes,
                        PayloadBytes = payloadBytes,
                        DecodedBytes = decodedBytes,
                        PlaybackEpochUtcTicks = transfer.PlaybackEpochUtcTicks,
                    }
                );
                payload = null;
                transfer.ReservedBytes = 0;
            }
            catch (Exception exception)
            {
                payload?.Dispose();
                if (payloadReservationHeld)
                    BasisNativeAnimationPayload.ReleaseReservation(payloadBytes);
                if (transfer.Buffer.IsCreated)
                    transfer.Buffer.Dispose();
                ReleaseInboundTransferBytes(transfer.ReservedBytes);
                transfer.ReservedBytes = 0;
                _animationAttempted.Remove(transfer.Id);
                BasisDebug.LogWarning(
                    $"Image pickup animation from {transfer.Sender} could not enter "
                        + $"the decode queue ({exception.Message}).",
                    LogTag
                );
                return;
            }

            BasisDebug.Log(
                $"Image pickup animation from {transfer.Sender} queued for Burst decode "
                    + $"({_queuedInboundAnimationDecodes.Count:N0} waiting, "
                    + $"{_pendingInboundAnimationDecodes.Count:N0} active).",
                LogTag
            );
            StartQueuedInboundAnimationDecodes();
        }

        private static bool TryReadInboundAnimationDecodedBytes(InboundAnimationTransfer transfer, out int decodedBytes, out string error)
        {
            if (transfer.Format != AnimationFormatGif)
            {
                return BasisBurstAnimationCodec.TryReadOuterHeader(
                    transfer.Buffer,
                    transfer.Buffer.Length,
                    out decodedBytes,
                    out error
                );
            }

            decodedBytes = 0;
            if (
                !BasisBurstGifDecoder.TryScan(
                    transfer.Buffer,
                    transfer.Buffer.Length,
                    BasisBurstGifDecoder.ResolveDecodedPixelLimit(BasisAnimationDecodeTrust.UntrustedRemote),
                    out int frameCount,
                    out int pixelCount,
                    out error
                )
            )
            {
                return false;
            }

            long nativeBytes = BasisAnimatedImageData.CalculateNativeByteCount(frameCount, pixelCount, frameCount);
            if (nativeBytes <= 0 || nativeBytes > int.MaxValue)
            {
                error = "GIF decoded size exceeds the configured limit.";
                return false;
            }
            decodedBytes = (int)nativeBytes;
            return true;
        }

        private static void StartQueuedInboundAnimationDecodes()
        {
            int queuedDecodeCount = _queuedInboundAnimationDecodes.Count;
            for (int index = 0; index < queuedDecodeCount; )
            {
                if (_pendingInboundAnimationDecodes.Count >= BasisImagePickupSettings.MaxConcurrentAnimationDecodeJobs)
                {
                    return;
                }

                QueuedInboundAnimationDecode queued = _queuedInboundAnimationDecodes[index];
                if (!TryGetAcceptedInboundAnimationPickup(queued.Sender, queued.Id, out BasisImagePickupObject pickup))
                {
                    RemoveQueuedInboundAnimationDecode(index);
                    queuedDecodeCount--;
                    continue;
                }

                // Attaching an animation matches its canvas against the poster, so this cannot start until the
                // poster lands. Hold the payload instead of dropping it: a sender offers an animation once, so
                // a drop here would strand the GIF on its first frame for the rest of the session.
                if (pickup.IsLoading)
                {
                    index++;
                    continue;
                }

                if (!CanStartInboundAnimationDecode(queued.Sender, queued.DecodedBytes))
                {
                    index++;
                    continue;
                }

                _queuedInboundAnimationDecodes.RemoveAt(index);
                queuedDecodeCount--;
                IBasisAnimationDecodeRequest job = null;
                try
                {
                    job = BasisAnimatedImageJobs.ScheduleAnimationDecode(
                        queued.Payload,
                        BasisAnimationDecodeTrust.UntrustedRemote
                    );
                    _pendingInboundAnimationDecodes.Add(
                        new PendingInboundAnimationDecode
                        {
                            Sender = queued.Sender,
                            Id = queued.Id,
                            Payload = queued.Payload,
                            ReservedBytes = queued.ReservedBytes,
                            PayloadBytes = queued.PayloadBytes,
                            DecodedBytes = queued.DecodedBytes,
                            PlaybackEpochUtcTicks = queued.PlaybackEpochUtcTicks,
                            Job = job,
                        }
                    );
                    queued.Payload = null;
                    job = null;
                }
                catch (BasisAnimationMemoryBudgetException)
                {
                    job?.Dispose();
                    _queuedInboundAnimationDecodes.Insert(index, queued);
                    return;
                }
                catch (Exception exception)
                {
                    job?.Dispose();
                    queued.Payload?.Dispose();
                    ReleaseInboundTransferBytes(queued.ReservedBytes);
                    queued.ReservedBytes = 0;
                    _animationAttempted.Remove(queued.Id);
                    BasisDebug.LogWarning(
                        $"Image pickup animation from {queued.Sender} could not schedule "
                            + $"a Burst decode ({exception.Message}).",
                        LogTag
                    );
                }
            }
        }

        private static bool TryGetAcceptedInboundAnimationPickup(
            ushort sender,
            Guid id,
            out BasisImagePickupObject pickup
        )
        {
            // The administrator lock blocks new image/animation headers. It does not remove
            // existing pickups, so work accepted before the lock must be allowed to finish.
            bool imageExists = _images.TryGetValue(id, out pickup) && pickup != null;
            return ShouldContinueAcceptedInboundAnimation(
                BasisImagePickupSettings.ReceiveEnabled,
                imageExists,
                imageExists && pickup.OwnerId == sender,
                imageExists && pickup.AnimatedImagePlayer != null
            );
        }

        internal static bool ShouldContinueAcceptedInboundAnimation(
            bool receiveEnabled,
            bool imageExists,
            bool ownerMatches,
            bool animationAlreadyAttached
        )
        {
            return receiveEnabled
                && imageExists
                && ownerMatches
                && !animationAlreadyAttached;
        }

        private static bool CanStartInboundAnimationDecode(ushort sender, int decodedBytes)
        {
            int pendingForSender = 0;
            long pendingDecodedBytes = 0;
            int pendingDecodeCount = _pendingInboundAnimationDecodes.Count;
            for (int i = 0; i < pendingDecodeCount; i++)
            {
                PendingInboundAnimationDecode pending = _pendingInboundAnimationDecodes[i];
                if (pending.Sender != sender)
                    continue;
                pendingForSender++;
                pendingDecodedBytes += pending.DecodedBytes;
            }

            return FitsInboundAnimationDecodeBudget(pendingForSender, pendingDecodedBytes, decodedBytes);
        }

        internal static bool FitsInboundAnimationDecodeBudget(
            int pendingJobs,
            long pendingDecodedBytes,
            int candidateDecodedBytes
        )
        {
            if (pendingJobs < 0 || pendingDecodedBytes < 0 || candidateDecodedBytes < 0)
            {
                return false;
            }

            long decodedByteLimit =
                BasisImagePickupSettings.MaxPendingInboundAnimationDecodedBytesPerSender;
            return pendingJobs
                    < BasisImagePickupSettings.MaxPendingInboundAnimationDecodeJobsPerSender
                && candidateDecodedBytes <= decodedByteLimit
                && pendingDecodedBytes <= decodedByteLimit - candidateDecodedBytes;
        }

        private static void RemoveQueuedInboundAnimationDecode(int index)
        {
            QueuedInboundAnimationDecode queued = _queuedInboundAnimationDecodes[index];
            _queuedInboundAnimationDecodes.RemoveAt(index);
            queued.Payload?.Dispose();
            ReleaseInboundTransferBytes(queued.ReservedBytes);
            queued.ReservedBytes = 0;
            _animationAttempted.Remove(queued.Id);
        }

        private static void ProcessCompletedInboundAnimationDecodes()
        {
            int pendingDecodeCount = _pendingInboundAnimationDecodes.Count;
            for (int index = pendingDecodeCount - 1; index >= 0; index--)
            {
                PendingInboundAnimationDecode pending = _pendingInboundAnimationDecodes[index];
                BasisBurstAnimationDecodeResult workerResult;
                try
                {
                    if (!pending.Job.TryComplete(out workerResult))
                        continue;
                }
                catch (Exception exception)
                {
                    _pendingInboundAnimationDecodes.RemoveAt(index);
                    _animationAttempted.Remove(pending.Id);
                    DisposeRequest(pending.Job, "inbound animation decode request");
                    pending.Job = null;
                    pending.Payload?.Dispose();
                    pending.Payload = null;
                    ReleaseInboundTransferBytes(pending.ReservedBytes);
                    pending.ReservedBytes = 0;
                    BasisDebug.LogWarning(
                        $"Image pickup animation from {pending.Sender} failed while completing "
                            + $"Burst validation: {exception.GetBaseException().Message}.",
                        LogTag
                    );
                    continue;
                }
                _pendingInboundAnimationDecodes.RemoveAt(index);
                _animationAttempted.Remove(pending.Id);

                try
                {
                    if (
                        !TryGetAcceptedInboundAnimationPickup(
                            pending.Sender,
                            pending.Id,
                            out BasisImagePickupObject pickup
                        )
                    )
                    {
                        continue;
                    }

                    if (workerResult == null || !workerResult.Ok || workerResult.Animation == null)
                    {
                        BasisDebug.LogWarning(
                            $"Image pickup animation from {pending.Sender} failed Burst validation: "
                                + $"{workerResult?.Error ?? "no result"}.",
                            LogTag
                        );
                        continue;
                    }

                    BasisAnimatedImageData animation = workerResult.Animation;
                    int frameCount = animation.FrameCount;
                    if (!CanAttachRemoteAnimation(pending.Sender, animation, true, out string budgetReason))
                    {
                        BasisDebug.LogWarning(
                            $"Image pickup animation from {pending.Sender} dropped: "
                                + $"{budgetReason}.",
                            LogTag
                        );
                        continue;
                    }

                    _remoteAnimationPayloads.Add(pending.Id, pending.Payload);
                    try
                    {
                        if (
                            !pickup.TrySetAnimation(
                                animation,
                                pending.PlaybackEpochUtcTicks,
                                pending.Payload
                            )
                        )
                        {
                            _remoteAnimationPayloads.Remove(pending.Id);
                            BasisDebug.LogWarning(
                                $"Image pickup animation from {pending.Sender} could not be "
                                    + "attached to its poster.",
                                LogTag
                            );
                            continue;
                        }
                    }
                    catch
                    {
                        _remoteAnimationPayloads.Remove(pending.Id);
                        throw;
                    }

                    pending.Payload = null;
                    workerResult.TakeAnimation();
                    double workerMilliseconds =
                        workerResult.WorkerElapsedTicks * 1000d / Stopwatch.Frequency;
                    bool decodedDataDeferred = pickup.AnimatedImagePlayer?.Data == null;
                    string replicationMessage = decodedDataDeferred
                        ? $"Image pickup animation from {pending.Sender} retained as a compact payload "
                            + "and deferred to the closest-animation decoded-data budget "
                            + $"({frameCount} frames, {pending.PayloadBytes} bytes, decoded by "
                            + $"Burst in {workerMilliseconds:0.###} ms)."
                        : $"Image pickup animation replicated from {pending.Sender} "
                            + $"({frameCount} frames, {pending.PayloadBytes} bytes, decoded by "
                            + $"Burst in {workerMilliseconds:0.###} ms).";
                    BasisDebug.Log(replicationMessage, LogTag);
                }
                finally
                {
                    workerResult?.TakeAnimation()?.Dispose();
                    DisposeRequest(pending.Job, "inbound animation decode request");
                    pending.Job = null;
                    pending.Payload?.Dispose();
                    pending.Payload = null;
                    ReleaseInboundTransferBytes(pending.ReservedBytes);
                    pending.ReservedBytes = 0;
                }
            }
        }

        private static void HandleTransform(ushort senderId, BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            Vector3 position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Quaternion rotation = new Quaternion(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
            float scale = reader.ReadSingle();

            // An offer we have not asked for yet is judged against the position it carried, and the
            // server can only have sent us the one it had. Transforms are broadcast to the whole
            // room whether or not you hold the picture, so following them here keeps a card that was
            // carried over to us from staying out of range forever.
            if (_pendingOffers.ContainsKey(id))
                _pendingOffers[id] = position;

            if (_images.TryGetValue(id, out BasisImagePickupObject pickup) && pickup != null && !pickup.IsController)
            {
                pickup.SetRemoteTarget(position, rotation, scale);
            }
        }

        private static void HandleClaim(ushort senderId, BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            if (_images.TryGetValue(id, out BasisImagePickupObject pickup) && pickup != null)
            {
                pickup.SetController(false);
            }
        }

        /// <summary>
        /// The server telling us whether it is holding one of our images. Only the server can cause
        /// a message to arrive stamped with our own player id — the relay never echoes a sender back
        /// to itself — so that check is what makes this unforgeable by another client.
        /// </summary>
        private static void HandleServerCacheState(ushort senderId, BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            bool held = reader.ReadBoolean();

            if (!BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId) || senderId != localId)
                return;

            if (held)
                _serverHeldImages.Add(id);
            else
                _serverHeldImages.Remove(id);
        }

        /// <summary>
        /// The server telling us it is holding an image we have not got. Same unforgeable stamp as the
        /// cache-state notice: only the server can make a message arrive under our own player id.
        ///
        /// The body is a spawn header, so this reads exactly like <see cref="HandleSpawn"/> up to the
        /// pose - and then stops, because an offer is a claim about an image rather than the image.
        /// </summary>
        private static void HandleServerCacheOffer(ushort senderId, BinaryReader reader)
        {
            if (!BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId) || senderId != localId)
                return;

            Guid id = new Guid(reader.ReadBytes(16));
            reader.ReadUInt16();
            if (!TrySkipWireString(reader, MaxIgnoredOwnerNameBytes))
                return;
            reader.ReadInt32();
            reader.ReadInt32();
            reader.ReadInt32();
            reader.ReadInt32();
            Vector3 position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            if (_images.ContainsKey(id) || _inbound.ContainsKey(id) || _owned.ContainsKey(id))
                return;

            _pendingOffers[id] = position;
        }

        private static void SendServerCacheRequest(Guid id)
        {
            if (!BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId))
                return;

            byte[] payload = new byte[1 + 16];
            payload[0] = OpServerCacheRequest;
            Buffer.BlockCopy(id.ToByteArray(), 0, payload, 1, 16);

            // Addressed to ourselves: the relay observes every image message on its way past, which is
            // how the cache hears this, and a one-entry recipient list keeps it off everybody else's
            // wire. The echo lands back here and falls through the dispatch switch unhandled.
            _selfRecipient[0] = localId;
            SendCustomNetworkEventDirect(payload, DeliveryMethod.ReliableOrdered, _selfRecipient);
        }

        private static bool TryGetLocalReplicationPosition(out Vector3 position)
        {
            position = default;
            BasisLocalPlayer local = BasisLocalPlayer.Instance;
            if (local == null)
                return false;
            position = local.transform.position;
            return true;
        }

        private static void EnsureOfferRangeCapacity(int count)
        {
            if (_offerRangeIds.Length >= count && _offerRangePositions.IsCreated && _offerRangePositions.Length >= count)
                return;

            int capacity = Mathf.NextPowerOfTwo(Mathf.Max(count, 8));
            _offerRangeIds = new Guid[capacity];
            if (_offerRangePositions.IsCreated)
                _offerRangePositions.Dispose();
            if (_offerRangeResults.IsCreated)
                _offerRangeResults.Dispose();
            _offerRangePositions = new NativeArray<Vector3>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _offerRangeResults = new NativeArray<byte>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>
        /// Starts the distance pass over everything the server has offered us. Buffers are persistent
        /// and the ids are copied out, so nothing here allocates on a steady state and an offer that
        /// arrives mid-tick cannot shift the indices the job is reading.
        /// </summary>
        private static void ScheduleOfferRangeCheck(float now)
        {
            if (_offerRangeScheduled || _pendingOffers.Count == 0)
                return;
            if (now < _nextOfferRangeCheckTime)
                return;
            _nextOfferRangeCheckTime = now + BasisImagePickupSettings.OfferRangeCheckSeconds;

            if (!TryGetLocalReplicationPosition(out Vector3 viewer))
                return;

            int count = _pendingOffers.Count;
            EnsureOfferRangeCapacity(count);

            int index = 0;
            foreach (KeyValuePair<Guid, Vector3> entry in _pendingOffers)
            {
                _offerRangeIds[index] = entry.Key;
                _offerRangePositions[index] = entry.Value;
                index++;
            }

            float rangeMeters = ServerImagePickupRangeMeters();
            _offerRangeCount = count;
            _offerRangeScheduled = true;
            _offerRangeHandle = new OfferRangeJob
            {
                Positions = _offerRangePositions,
                Viewer = viewer,
                RangeSquared = rangeMeters * rangeMeters,
                InRange = _offerRangeResults,
            }.Schedule(count, 32);
        }

        /// <summary>
        /// Collects the pass and asks for whatever came out in range. Offers that did not are left
        /// queued: the player may still walk toward them.
        /// </summary>
        private static void CompleteOfferRangeCheck()
        {
            if (!_offerRangeScheduled)
                return;
            _offerRangeHandle.Complete();
            _offerRangeScheduled = false;

            int requested = 0;
            int deferred = 0;
            float nearestDeferredSq = float.MaxValue;
            bool haveViewer = TryGetLocalReplicationPosition(out Vector3 viewer);

            for (int index = 0; index < _offerRangeCount; index++)
            {
                if (_offerRangeResults[index] == 0)
                {
                    deferred++;
                    if (haveViewer)
                    {
                        float distanceSq = (_offerRangePositions[index] - viewer).sqrMagnitude;
                        if (distanceSq < nearestDeferredSq)
                            nearestDeferredSq = distanceSq;
                    }
                    continue;
                }
                Guid id = _offerRangeIds[index];
                if (!_pendingOffers.Remove(id))
                    continue;
                requested++;
                SendServerCacheRequest(id);
            }

            if (requested > 0)
            {
                // Only the passes that actually do something say so; a player standing still next to a
                // wall of pictures they have already asked for stays quiet.
                string held = deferred > 0 && nearestDeferredSq < float.MaxValue
                    ? $", still holding {deferred:N0} out of range (nearest {Mathf.Sqrt(nearestDeferredSq):0.#}m)"
                    : deferred > 0
                        ? $", still holding {deferred:N0} out of range"
                        : string.Empty;
                BasisDebug.Log(
                    $"Image pickup requested {requested:N0} offered image(s) within "
                        + $"{ServerImagePickupRangeMeters():0.##}m{held}.",
                    LogTag
                );
            }

            _offerRangeCount = 0;
        }

        private static void ReleaseOfferRangeResources()
        {
            if (_offerRangeScheduled)
            {
                _offerRangeHandle.Complete();
                _offerRangeScheduled = false;
            }
            _offerRangeCount = 0;
            _offerRangeIds = Array.Empty<Guid>();
            if (_offerRangePositions.IsCreated)
                _offerRangePositions.Dispose();
            if (_offerRangeResults.IsCreated)
                _offerRangeResults.Dispose();
        }

        private static void HandleDespawn(ushort senderId, BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            if (_images.ContainsKey(id) || _inbound.ContainsKey(id))
            {
                BasisDebug.Log($"Image pickup: player {senderId} despawned image {id}.", LogTag);
            }
            RemoveImage(id);
        }

        private static bool CanAcceptSpawn(
            ushort sender,
            int totalBytes,
            int width,
            int height,
            int totalChunks,
            out string reason
        )
        {
            reason = null;
            if (BasisNetworkModeration.GlobalImagesLocked && !BasisNetworkModeration.LocalPlayerHasGlobalLockBypass())
            {
                reason = "shared images locked by admin";
                return false;
            }
            if (!BasisImagePickupSettings.ReceiveEnabled)
            {
                reason = "receiving disabled";
                return false;
            }
            if (totalBytes <= 0 || totalBytes > BasisImagePickupSettings.MaxImageBytes)
            {
                reason = "size";
                return false;
            }
            if (
                width <= 0
                || height <= 0
                || width > BasisImagePickupSettings.MaxDimension
                || height > BasisImagePickupSettings.MaxDimension
            )
            {
                reason = "dimensions";
                return false;
            }
            if ((long)width * height > BasisImagePickupSettings.MaxTotalPixels)
            {
                reason = "pixel budget";
                return false;
            }

            int expectedChunks =
                (totalBytes + BasisImagePickupSettings.ChunkPayloadBytes - 1)
                / BasisImagePickupSettings.ChunkPayloadBytes;
            if (totalChunks != expectedChunks)
            {
                reason = "chunk count";
                return false;
            }

            int imageCount = 1;
            int activeTransfers = 0;
            long aggregatePixels = (long)width * height;
            long aggregateBytes = totalBytes;

            foreach (BasisImagePickupObject pickup in _images.Values)
            {
                // A loading card has a live transfer backing it, and that transfer carries the authoritative
                // claimed size while the card itself still has no poster. Counting it here too would charge
                // the sender twice for one image and halve their effective limit.
                if (pickup == null || pickup.OwnerId != sender || pickup.IsLoading)
                    continue;
                imageCount++;
                aggregatePixels += pickup.PosterPixelCount;
                aggregateBytes += pickup.CleanPng?.Length ?? 0;
            }
            foreach (InboundTransfer transfer in _inbound.Values)
            {
                if (transfer.Sender != sender)
                    continue;
                imageCount++;
                activeTransfers++;
                aggregatePixels += (long)transfer.Width * transfer.Height;
                aggregateBytes += transfer.Buffer?.Length ?? 0;
            }
            foreach (InboundAnimationTransfer transfer in _inboundAnimations.Values)
            {
                if (transfer.Sender == sender)
                    activeTransfers++;
            }

            if (!IsWithinRemoteImageBudget(imageCount, aggregatePixels, aggregateBytes, out reason))
            {
                return false;
            }
            if (activeTransfers >= BasisImagePickupSettings.MaxInboundTransfersPerSender)
            {
                reason = "too many transfers";
                return false;
            }

            if (!TryConsumeSpawnRateToken(sender, Time.unscaledTime))
            {
                reason = "rate limit";
                return false;
            }
            return true;
        }

        private static bool CanAcceptLocalImage(int totalBytes, int width, int height, out string reason)
        {
            int imageCount = 1;
            long aggregatePixels = (long)width * height;
            long aggregateBytes = totalBytes;
            foreach (OwnedImage owned in _owned.Values)
            {
                if (owned?.Object == null)
                    continue;
                imageCount++;
                aggregatePixels += owned.Object.PosterPixelCount;
                aggregateBytes += owned.CleanPng?.Length ?? 0;
            }

            return IsWithinRemoteImageBudget(imageCount, aggregatePixels, aggregateBytes, out reason);
        }

        internal static bool MatchesClaimedDimensions(
            int claimedWidth,
            int claimedHeight,
            int decodedWidth,
            int decodedHeight
        )
        {
            return claimedWidth == decodedWidth && claimedHeight == decodedHeight;
        }

        internal static bool IsWithinRemoteImageBudget(
            int imageCount,
            long aggregatePixels,
            long aggregateBytes,
            out string reason
        )
        {
            if (imageCount > BasisImagePickupSettings.MaxConcurrentImagesPerSender)
            {
                reason =
                    $"image count limit ({BasisImagePickupSettings.MaxConcurrentImagesPerSender})";
                return false;
            }
            if (aggregatePixels > BasisImagePickupSettings.MaxRemoteImagePixelsPerSender)
            {
                reason = "aggregate image pixel budget";
                return false;
            }
            if (aggregateBytes > BasisImagePickupSettings.MaxRemoteImageBytesPerSender)
            {
                reason = "aggregate image byte budget";
                return false;
            }

            reason = null;
            return true;
        }

        internal static bool FitsInboundTransferBudget(long reservedBytes, long candidateBytes)
        {
            return reservedBytes >= 0
                && candidateBytes > 0
                && reservedBytes <= BasisImagePickupSettings.MaxInboundTransferBytes
                && candidateBytes
                    <= BasisImagePickupSettings.MaxInboundTransferBytes - reservedBytes;
        }

        private static bool TryReserveInboundTransferBytes(long bytes, out string reason)
        {
            if (!FitsInboundTransferBudget(_reservedInboundTransferBytes, bytes))
            {
                reason = "global inbound transfer memory budget";
                return false;
            }

            _reservedInboundTransferBytes = checked(_reservedInboundTransferBytes + bytes);
            reason = null;
            return true;
        }

        private static void ReleaseInboundTransferBytes(long bytes)
        {
            if (bytes <= 0)
                return;
            _reservedInboundTransferBytes = Math.Max(0, _reservedInboundTransferBytes - bytes);
        }

        private static bool TryConsumeSpawnRateToken(ushort sender, float now)
        {
            float interval = BasisImagePickupSettings.MinSecondsBetweenSpawnsPerSender;
            if (interval <= 0f)
                return true;

            if (!_spawnRateBySender.TryGetValue(sender, out SpawnRateLimitState state))
            {
                state = new SpawnRateLimitState
                {
                    Tokens = BasisImagePickupSettings.SpawnRateBurstAllowance,
                    LastRefillTime = now,
                };
                _spawnRateBySender[sender] = state;
            }
            else
            {
                float elapsed = Mathf.Max(0f, now - state.LastRefillTime);
                state.Tokens = Mathf.Min(
                    BasisImagePickupSettings.SpawnRateBurstAllowance,
                    state.Tokens + elapsed / interval
                );
                state.LastRefillTime = now;
            }

            if (state.Tokens < 1f)
                return false;
            state.Tokens -= 1f;
            return true;
        }

        private static bool CanAcceptAnimation(
            ushort sender,
            Guid id,
            int totalBytes,
            int totalChunks,
            long playbackEpochUtcTicks,
            out string reason
        )
        {
            reason = null;
            if (BasisNetworkModeration.GlobalImagesLocked && !BasisNetworkModeration.LocalPlayerHasGlobalLockBypass())
            {
                reason = "shared images locked by admin";
                return false;
            }
            if (!BasisImagePickupSettings.ReceiveEnabled)
            {
                reason = "receiving disabled";
                return false;
            }
            if (totalBytes <= 0 || totalBytes > BasisImagePickupSettings.MaxAnimationNetworkBytes)
            {
                reason = "animation size";
                return false;
            }
            if (playbackEpochUtcTicks <= 0)
            {
                reason = "playback epoch";
                return false;
            }

            int expectedChunks =
                (totalBytes + BasisImagePickupSettings.ChunkPayloadBytes - 1)
                / BasisImagePickupSettings.ChunkPayloadBytes;
            if (totalChunks != expectedChunks)
            {
                reason = "animation chunk count";
                return false;
            }

            if (!_images.TryGetValue(id, out BasisImagePickupObject pickup) || pickup == null)
            {
                reason = "poster is unavailable";
                return false;
            }
            if (pickup.OwnerId != sender)
            {
                reason = "sender does not own the image";
                return false;
            }
            if (pickup.AnimatedImagePlayer != null)
            {
                reason = "animation is already attached";
                return false;
            }

            int activeTransfers = 0;
            long activeTransferBytes = totalBytes;
            foreach (KeyValuePair<Guid, BasisNativeAnimationPayload> entry in _remoteAnimationPayloads)
            {
                if (
                    entry.Value == null
                    || !_images.TryGetValue(entry.Key, out BasisImagePickupObject retainedPickup)
                    || retainedPickup == null
                    || retainedPickup.OwnerId != sender
                )
                {
                    continue;
                }
                activeTransferBytes += entry.Value.Length;
            }
            foreach (InboundAnimationTransfer transfer in _inboundAnimations.Values)
            {
                if (transfer.Sender != sender)
                    continue;
                activeTransfers++;
                activeTransferBytes += transfer.Buffer.Length;
            }
            foreach (InboundTransfer transfer in _inbound.Values)
            {
                if (transfer.Sender == sender)
                    activeTransfers++;
            }
            int queuedInboundDecodeCount = _queuedInboundAnimationDecodes.Count;
            for (int i = 0; i < queuedInboundDecodeCount; i++)
            {
                QueuedInboundAnimationDecode queued = _queuedInboundAnimationDecodes[i];
                if (queued.Sender == sender)
                    activeTransferBytes += queued.PayloadBytes;
            }
            int pendingInboundDecodeCount = _pendingInboundAnimationDecodes.Count;
            for (int i = 0; i < pendingInboundDecodeCount; i++)
            {
                PendingInboundAnimationDecode pending = _pendingInboundAnimationDecodes[i];
                if (pending.Sender == sender)
                    activeTransferBytes += pending.PayloadBytes;
            }
            if (activeTransfers >= BasisImagePickupSettings.MaxInboundTransfersPerSender)
            {
                reason = "too many animation transfers";
                return false;
            }
            if (activeTransferBytes > BasisImagePickupSettings.MaxInboundAnimationNetworkBytesPerSender)
            {
                reason = "aggregate animation payload budget";
                return false;
            }

            return true;
        }

        private static bool CanAttachLocalAnimation(
            BasisAnimatedImageData candidate,
            bool reloadable,
            out string reason
        )
        {
            if (
                !reloadable
                && BasisAnimatedImageData.TotalResidentNativeBytes
                    > BasisImagePickupSettings.MaxResidentAnimationNativeBytes
            )
            {
                reason = "global resident animation memory budget exceeded";
                return false;
            }

            long decodedFramePixels = reloadable ? 0 : candidate.DecodedFramePixels;
            long canvasPixels = (long)candidate.CanvasWidth * candidate.CanvasHeight;
            foreach (OwnedImage owned in _owned.Values)
            {
                BasisAnimatedImagePlayer existing = owned?.Object?.AnimatedImagePlayer;
                if (existing == null)
                    continue;
                if (!reloadable)
                    decodedFramePixels += existing.DecodedFramePixels;
                canvasPixels += existing.CanvasPixels;
            }

            return reloadable
                ? IsWithinRemoteAnimationCanvasBudget(canvasPixels, out reason)
                : IsWithinRemoteAnimationBudget(decodedFramePixels, canvasPixels, out reason);
        }

        private static bool CanAttachRemoteAnimation(
            ushort sender,
            BasisAnimatedImageData candidate,
            bool reloadable,
            out string reason
        )
        {
            if (
                !reloadable
                && BasisAnimatedImageData.TotalResidentNativeBytes
                    > BasisImagePickupSettings.MaxResidentAnimationNativeBytes
            )
            {
                reason = "global resident animation memory budget exceeded";
                return false;
            }

            long decodedFramePixels = reloadable ? 0 : candidate.DecodedFramePixels;
            long canvasPixels = (long)candidate.CanvasWidth * candidate.CanvasHeight;

            foreach (BasisImagePickupObject pickup in _images.Values)
            {
                if (pickup == null || pickup.OwnerId != sender || pickup.AnimatedImagePlayer == null)
                    continue;

                BasisAnimatedImagePlayer existing = pickup.AnimatedImagePlayer;
                if (!reloadable)
                    decodedFramePixels += existing.DecodedFramePixels;
                canvasPixels += existing.CanvasPixels;
            }

            return reloadable
                ? IsWithinRemoteAnimationCanvasBudget(canvasPixels, out reason)
                : IsWithinRemoteAnimationBudget(decodedFramePixels, canvasPixels, out reason);
        }

        internal static bool IsWithinRemoteAnimationBudget(
            long decodedFramePixels,
            long canvasPixels,
            out string reason
        )
        {
            if (decodedFramePixels > BasisImagePickupSettings.MaxRemoteAnimationDecodedFramePixelsPerSender)
            {
                reason = "aggregate decoded animation pixel budget exceeded";
                return false;
            }
            if (canvasPixels > BasisImagePickupSettings.MaxRemoteAnimationCanvasPixelsPerSender)
            {
                reason = "aggregate animation canvas budget exceeded";
                return false;
            }

            reason = null;
            return true;
        }

        private static bool IsWithinRemoteAnimationCanvasBudget(long canvasPixels, out string reason)
        {
            if (canvasPixels > BasisImagePickupSettings.MaxRemoteAnimationCanvasPixelsPerSender)
            {
                reason = "aggregate animation canvas budget exceeded";
                return false;
            }

            reason = null;
            return true;
        }

        private static void DisposeRejectedValidationResult(ref BasisImageValidationResult result)
        {
            if (result.Texture != null)
                UnityEngine.Object.Destroy(result.Texture);
            result.Texture = null;
            result.TakeAnimation()?.Dispose();
            result.TakeAnimationPayload()?.Dispose();
        }

        private static void CleanupExpiredTransfers(float now)
        {
            if (_inbound.Count > 0)
            {
                _scratchIds.Clear();
                foreach (KeyValuePair<Guid, InboundTransfer> entry in _inbound)
                {
                    InboundTransfer inbound = entry.Value;
                    if (now >= inbound.Deadline)
                    {
                        _scratchIds.Add(entry.Key);
                        continue;
                    }
                    // Says it while the card is still on screen, rather than only in the post-mortem 30
                    // seconds later, and names the sender so the stall can be chased from the other side.
                    if (
                        !inbound.StallLogged
                        && now - inbound.LastProgressTime
                            >= BasisImagePickupSettings.StalledTransferWarningSeconds
                    )
                    {
                        inbound.StallLogged = true;
                        BasisDebug.LogWarning(
                            $"Image pickup transfer from {inbound.Sender} has received no chunk for "
                                + $"{now - inbound.LastProgressTime:0.#}s at "
                                + $"{inbound.ReceivedCount:N0}/{inbound.TotalChunks:N0} chunks. It will be "
                                + $"dropped at {BasisImagePickupSettings.InboundTransferTimeoutSeconds:0}s "
                                + "without progress.",
                            LogTag
                        );
                    }
                }
                int expiredTransferCount = _scratchIds.Count;
                // RemoveImage, not RemoveInboundTransfer: a stalled transfer now has a placeholder card
                // standing in the world, and dropping only the transfer would strand it there empty forever.
                for (int i = 0; i < expiredTransferCount; i++)
                {
                    if (_inbound.TryGetValue(_scratchIds[i], out InboundTransfer expired))
                    {
                        BasisDebug.LogWarning(
                            $"Image pickup transfer from {expired.Sender} timed out after "
                                + $"{BasisImagePickupSettings.InboundTransferTimeoutSeconds:0}s with "
                                + $"{expired.ReceivedCount:N0}/{expired.TotalChunks:N0} chunks; "
                                + "removing its card.",
                            LogTag
                        );
                    }
                    RemoveImage(_scratchIds[i]);
                }
            }

            if (_inboundAnimations.Count > 0)
            {
                _scratchIds.Clear();
                foreach (KeyValuePair<Guid, InboundAnimationTransfer> entry in _inboundAnimations)
                {
                    if (now >= entry.Value.Deadline)
                        _scratchIds.Add(entry.Key);
                }
                int expiredAnimationCount = _scratchIds.Count;
                for (int i = 0; i < expiredAnimationCount; i++)
                    RemoveInboundAnimationTransfer(_scratchIds[i]);
            }
        }

        private static void RemoveInboundTransfer(Guid id)
        {
            if (!_inbound.TryGetValue(id, out InboundTransfer transfer))
                return;
            _inbound.Remove(id);
            ReleaseInboundTransferBytes(transfer.ReservedBytes);
            transfer.ReservedBytes = 0;
        }

        private static void RemoveInboundAnimationTransfer(Guid id)
        {
            if (!_inboundAnimations.TryGetValue(id, out InboundAnimationTransfer transfer))
                return;
            _inboundAnimations.Remove(id);
            _animationAttempted.Remove(id);
            if (transfer.Buffer.IsCreated)
                transfer.Buffer.Dispose();
            if (transfer.Received.IsCreated)
                transfer.Received.Dispose();
            ReleaseInboundTransferBytes(transfer.ReservedBytes);
            transfer.ReservedBytes = 0;
        }

        /// <summary>Cleans manager-owned state when a scene unload destroys a pickup directly.</summary>
        internal static void OnPickupDestroyed(BasisImagePickupObject pickup)
        {
            if (_destroying || ReferenceEquals(pickup, null))
                return;
            if (
                !_images.TryGetValue(pickup.ImageId, out BasisImagePickupObject tracked)
                || !ReferenceEquals(tracked, pickup)
            )
                return;
            RemoveImage(pickup.ImageId, false);
        }

        private static void RemoveImage(Guid id, bool destroyPickup = true)
        {
            _images.TryGetValue(id, out BasisImagePickupObject pickup);
            _images.Remove(id);

            if (pickup != null)
            {
                BasisAnimatedImagePlayer player = pickup.AnimatedImagePlayer;
                if (player != null)
                {
                    player.ClearReloadPayload();
                    player.DisposeOwnedResources();
                }
            }

            RemoveOutboundImageTransfers(id);
            RemoveOutboundAnimationTransfers(id);
            if (_owned.TryGetValue(id, out OwnedImage owned))
                owned.AnimationPayload?.Dispose();
            _owned.Remove(id);
            _serverHeldImages.Remove(id);
            _pendingOffers.Remove(id);
            if (_remoteAnimationPayloads.TryGetValue(id, out BasisNativeAnimationPayload remotePayload))
                remotePayload?.Dispose();
            _remoteAnimationPayloads.Remove(id);
            RemoveInboundTransfer(id);
            RemoveInboundAnimationTransfer(id);
            int queuedInboundDecodeCount = _queuedInboundAnimationDecodes.Count;
            for (int i = queuedInboundDecodeCount - 1; i >= 0; i--)
            {
                if (_queuedInboundAnimationDecodes[i].Id != id)
                    continue;
                RemoveQueuedInboundAnimationDecode(i);
            }
            int pendingInboundDecodeCount = _pendingInboundAnimationDecodes.Count;
            for (int i = pendingInboundDecodeCount - 1; i >= 0; i--)
            {
                if (_pendingInboundAnimationDecodes[i].Id != id)
                    continue;
                PendingInboundAnimationDecode pending = _pendingInboundAnimationDecodes[i];
                pending.Job?.Dispose();
                pending.Payload?.Dispose();
                ReleaseInboundTransferBytes(pending.ReservedBytes);
                pending.ReservedBytes = 0;
                _pendingInboundAnimationDecodes.RemoveAt(i);
            }
            _animationAttempted.Remove(id);
            BasisImagePickupProgressGizmos.Remove(id);
            BasisShareableRegistry.Unregister(id.ToString());

            if (!destroyPickup || pickup == null)
                return;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEngine.Object.DestroyImmediate(pickup.gameObject);
            else
#endif
                UnityEngine.Object.Destroy(pickup.gameObject);
        }

        private static bool SendSpawn(
            Guid id,
            ushort ownerId,
            string ownerName,
            int width,
            int height,
            byte[] png,
            Vector3 position,
            Quaternion rotation,
            ushort[] recipients
        )
        {
            if (png == null || png.Length <= 0 || recipients == null || recipients.Length == 0)
                return false;
            _outboundImages.Enqueue(
                new OutboundImageTransfer
                {
                    Id = id,
                    OwnerId = ownerId,
                    OwnerName = ownerName,
                    Width = width,
                    Height = height,
                    Png = png,
                    NextChunkIndex = 0,
                    Position = position,
                    Rotation = rotation,
                    Recipients = recipients,
                    HeaderSent = false,
                }
            );
            return true;
        }

        private static void ProcessOutboundImageTransfers()
        {
            int chunksRemaining =
                BasisImagePickupSettings.MaxImageNetworkChunksPerFrame;
            while (chunksRemaining > 0 && _outboundImages.Count > 0)
            {
                OutboundImageTransfer transfer = _outboundImages.Peek();
                if (
                    !_owned.TryGetValue(transfer.Id, out OwnedImage owned)
                    || owned.Object == null
                    || !ReferenceEquals(owned.CleanPng, transfer.Png)
                )
                {
                    _outboundImages.Dequeue();
                    continue;
                }

                int chunkSize = BasisImagePickupSettings.ChunkPayloadBytes;
                int totalChunks = (transfer.Png.Length + chunkSize - 1) / chunkSize;
                if (!transfer.HeaderSent)
                {
                    owned.Object.transform.GetPositionAndRotation(out transfer.Position, out transfer.Rotation);
                    SendCustomNetworkEventDirect(
                        EncodeSpawn(
                            transfer.Id,
                            transfer.OwnerId,
                            transfer.OwnerName,
                            transfer.Width,
                            transfer.Height,
                            transfer.Png.Length,
                            totalChunks,
                            transfer.Position,
                            transfer.Rotation
                        ),
                        DeliveryMethod.ReliableOrdered,
                        transfer.Recipients
                    );
                    transfer.HeaderSent = true;
                }

                while (chunksRemaining > 0 && transfer.NextChunkIndex < totalChunks)
                {
                    int offset = transfer.NextChunkIndex * chunkSize;
                    int length = Mathf.Min(chunkSize, transfer.Png.Length - offset);
                    int packetLength = ImageChunkHeaderBytes + length;
                    if (!TryReserveSendBandwidth(packetLength, transfer.Recipients))
                        return;
                    if (transfer.ChunkBuffer == null || transfer.ChunkBuffer.Length != packetLength)
                        transfer.ChunkBuffer = new byte[packetLength];
                    EncodeChunkInto(
                        transfer.ChunkBuffer,
                        transfer.Id,
                        transfer.NextChunkIndex,
                        transfer.Png,
                        offset,
                        length
                    );
                    SendCustomNetworkEventDirect(
                        transfer.ChunkBuffer,
                        DeliveryMethod.ReliableOrdered,
                        transfer.Recipients
                    );
                    transfer.NextChunkIndex++;
                    transfer.Rate.MovedBytes += length;
                    chunksRemaining--;
                }

                if (transfer.NextChunkIndex >= totalChunks)
                {
                    owned.Object.transform.GetPositionAndRotation(
                        out Vector3 finalPosition,
                        out Quaternion finalRotation
                    );
                    SendCustomNetworkEventDirect(
                        EncodeTransform(
                            transfer.Id,
                            finalPosition,
                            finalRotation,
                            owned.Object.transform.localScale.x
                        ),
                        DeliveryMethod.ReliableOrdered,
                        transfer.Recipients
                    );
                    _outboundImages.Dequeue();
                }
            }
        }

        private static bool HasPendingOutboundImageTransfer(Guid id, ushort[] recipients)
        {
            foreach (OutboundImageTransfer transfer in _outboundImages)
            {
                if (transfer.Id == id && RecipientSnapshotsMatch(transfer.Recipients, recipients))
                    return true;
            }
            return false;
        }

        private static void RemoveOutboundImageTransfers(Guid id)
        {
            int count = _outboundImages.Count;
            for (int i = 0; i < count; i++)
            {
                OutboundImageTransfer transfer = _outboundImages.Dequeue();
                if (transfer.Id != id)
                    _outboundImages.Enqueue(transfer);
            }
        }

        private static void RemoveOutboundImageTransfersForRecipient(ushort recipient)
        {
            int count = _outboundImages.Count;
            for (int i = 0; i < count; i++)
            {
                OutboundImageTransfer transfer = _outboundImages.Dequeue();
                ushort[] reduced = RemoveRecipientFromSnapshot(transfer.Recipients, recipient);
                if (reduced.Length == 0)
                    continue;
                transfer.Recipients = reduced;
                _outboundImages.Enqueue(transfer);
            }
        }

        private static void SendAnimation(Guid id, OwnedImage owned, ushort[] recipients)
        {
            if (
                recipients == null
                || recipients.Length == 0
                || owned == null
                || owned.AnimationPayload == null
                || !owned.AnimationPayload.IsCreated
                || owned.AnimationPayload.Length <= 0
                || owned.AnimationPayload.Length
                    > BasisImagePickupSettings.MaxAnimationNetworkBytes
                || owned.PlaybackEpochUtcTicks <= 0
            )
            {
                return;
            }

            _outboundAnimations.Enqueue(
                new OutboundAnimationTransfer
                {
                    Id = id,
                    Payload = owned.AnimationPayload,
                    NextChunkIndex = 0,
                    PlaybackEpochUtcTicks = owned.PlaybackEpochUtcTicks,
                    Recipients = recipients,
                    PacketJob = null,
                    Packets = null,
                    HeaderSent = false,
                    EnqueuedTimestamp = Stopwatch.GetTimestamp(),
                    FirstPacketQueueTicks = 0,
                }
            );
        }

        private static BasisAnimationPacketJobRequest ScheduleAnimationPacketBatch(
            Guid id,
            BasisNativeAnimationPayload animationPayload,
            long playbackEpochUtcTicks,
            int startChunkIndex
        )
        {
            return BasisAnimatedImageJobs.SchedulePacketBuild(
                id,
                animationPayload,
                playbackEpochUtcTicks,
                animationPayload.Format,
                OpAnimationSpawn,
                OpAnimationChunk,
                BasisImagePickupSettings.ChunkPayloadBytes,
                startChunkIndex,
                BasisImagePickupSettings.AnimationPacketBuildChunksPerJob
            );
        }

        private static void ProcessOutboundAnimationTransfers()
        {
            int chunksRemaining =
                BasisImagePickupSettings.MaxAnimationNetworkChunksPerFrame;

            // Keep one animation transfer at the head until all of its chunks are sent. The receiver
            // intentionally limits concurrent native transfer buffers; round-robin headers opened many
            // transfers at once and caused later animations in a large drag batch to be dropped forever.
            while (chunksRemaining > 0 && _outboundAnimations.Count > 0)
            {
                OutboundAnimationTransfer transfer = _outboundAnimations.Peek();
                if (HasPendingOutboundImageTransfer(transfer.Id, transfer.Recipients))
                    return;
                if (
                    !_owned.TryGetValue(transfer.Id, out OwnedImage owned)
                    || !ReferenceEquals(owned.AnimationPayload, transfer.Payload)
                    || owned.PlaybackEpochUtcTicks != transfer.PlaybackEpochUtcTicks
                )
                {
                    _outboundAnimations.Dequeue();
                    DisposeOutboundAnimationTransfer(transfer);
                    continue;
                }

                if (transfer.Packets == null)
                {
                    if (transfer.PacketJob == null)
                    {
                        if (!transfer.HeaderSent && transfer.NextChunkIndex == 0)
                        {
                            transfer.FirstPacketQueueTicks = Math.Max(
                                0L,
                                Stopwatch.GetTimestamp() - transfer.EnqueuedTimestamp
                            );
                        }

                        try
                        {
                            transfer.PacketJob = ScheduleAnimationPacketBatch(
                                transfer.Id,
                                transfer.Payload,
                                transfer.PlaybackEpochUtcTicks,
                                transfer.NextChunkIndex
                            );
                        }
                        catch (Exception exception)
                        {
                            BasisDebug.LogWarning(
                                $"Image pickup: could not schedule animation packet worker "
                                    + $"({exception.Message}).",
                                LogTag
                            );
                            _outboundAnimations.Dequeue();
                            DisposeOutboundAnimationTransfer(transfer);
                            continue;
                        }
                    }

                    BasisAnimationPacketBatch packetBatch;
                    try
                    {
                        if (!transfer.PacketJob.TryComplete(out packetBatch))
                            return;
                    }
                    catch (Exception exception)
                    {
                        BasisDebug.LogWarning(
                            $"Image pickup: animation packet worker failed while completing "
                                + $"({exception.GetBaseException().Message}).",
                            LogTag
                        );
                        _outboundAnimations.Dequeue();
                        DisposeOutboundAnimationTransfer(transfer);
                        continue;
                    }
                    DisposeRequest(transfer.PacketJob, "animation packet request");
                    transfer.PacketJob = null;
                    if (
                        packetBatch == null
                        || !packetBatch.Ok
                        || packetBatch.StartChunkIndex != transfer.NextChunkIndex
                        || packetBatch.PacketCount <= 0
                    )
                    {
                        BasisDebug.LogWarning(
                            $"Image pickup: Burst packet construction failed: "
                                + $"{packetBatch?.Error ?? "invalid batch"}.",
                            LogTag
                        );
                        packetBatch?.Dispose();
                        _outboundAnimations.Dequeue();
                        DisposeOutboundAnimationTransfer(transfer);
                        continue;
                    }

                    transfer.Packets = packetBatch;
                    if (!transfer.HeaderSent)
                    {
                        double readyMilliseconds =
                            packetBatch.ReadyElapsedTicks * 1000d / Stopwatch.Frequency;
                        double queueMilliseconds =
                            transfer.FirstPacketQueueTicks
                            * 1000d
                            / Stopwatch.Frequency;
                        BasisDebug.Log(
                            $"Image pickup: first native packet batch observed ready "
                                + $"{readyMilliseconds:0.###} ms after scheduling; transfer waited "
                                + $"{queueMilliseconds:0.###} ms in the serialized animation queue.",
                            LogTag
                        );
                    }
                }

                if (!transfer.HeaderSent)
                {
                    int headerLength = transfer.Packets.HeaderLength;
                    if (headerLength <= 0)
                    {
                        BasisDebug.LogWarning("Image pickup: first native packet batch has no header.", LogTag);
                        _outboundAnimations.Dequeue();
                        DisposeOutboundAnimationTransfer(transfer);
                        continue;
                    }
                    if (transfer.HeaderBuffer == null || transfer.HeaderBuffer.Length != headerLength)
                    {
                        transfer.HeaderBuffer = new byte[headerLength];
                    }
                    transfer.Packets.CopyHeaderTo(transfer.HeaderBuffer);
                    SendCustomNetworkEventDirect(
                        transfer.HeaderBuffer,
                        DeliveryMethod.ReliableOrdered,
                        transfer.Recipients
                    );
                    transfer.HeaderSent = true;
                }

                int batchIndex =
                    transfer.NextChunkIndex - transfer.Packets.StartChunkIndex;
                while (chunksRemaining > 0 && batchIndex >= 0 && batchIndex < transfer.Packets.PacketCount)
                {
                    int packetLength = transfer.Packets.GetPacketLength(batchIndex);
                    if (!TryReserveSendBandwidth(packetLength, transfer.Recipients))
                        return;
                    int fullPacketLength =
                        BasisAnimatedImageNetworkCodec.AnimationChunkHeaderSize
                        + BasisImagePickupSettings.ChunkPayloadBytes;
                    byte[] packet;
                    if (packetLength == fullPacketLength)
                    {
                        if (transfer.FullChunkBuffer == null || transfer.FullChunkBuffer.Length != packetLength)
                        {
                            transfer.FullChunkBuffer = new byte[packetLength];
                        }
                        packet = transfer.FullChunkBuffer;
                    }
                    else
                    {
                        if (transfer.TailChunkBuffer == null || transfer.TailChunkBuffer.Length != packetLength)
                        {
                            transfer.TailChunkBuffer = new byte[packetLength];
                        }
                        packet = transfer.TailChunkBuffer;
                    }
                    transfer.Packets.CopyPacketTo(batchIndex, packet);

                    SendCustomNetworkEventDirect(packet, DeliveryMethod.ReliableOrdered, transfer.Recipients);
                    transfer.NextChunkIndex++;
                    transfer.Rate.MovedBytes += Math.Max(
                        0,
                        packetLength - BasisAnimatedImageNetworkCodec.AnimationChunkHeaderSize
                    );
                    batchIndex++;
                    chunksRemaining--;
                }
                if (transfer.NextChunkIndex >= transfer.Packets.TotalChunks)
                {
                    _outboundAnimations.Dequeue();
                    DisposeOutboundAnimationTransfer(transfer);
                    continue;
                }

                if (batchIndex >= transfer.Packets.PacketCount)
                {
                    transfer.Packets.Dispose();
                    transfer.Packets = null;
                    try
                    {
                        transfer.PacketJob = ScheduleAnimationPacketBatch(
                            transfer.Id,
                            transfer.Payload,
                            transfer.PlaybackEpochUtcTicks,
                            transfer.NextChunkIndex
                        );
                    }
                    catch (Exception exception)
                    {
                        BasisDebug.LogWarning(
                            $"Image pickup: could not schedule the next Burst packet batch ({exception.Message}).",
                            LogTag
                        );
                        _outboundAnimations.Dequeue();
                        DisposeOutboundAnimationTransfer(transfer);
                        continue;
                    }
                }
            }
        }

        private static void RemoveOutboundAnimationTransfers(Guid id)
        {
            int count = _outboundAnimations.Count;
            for (int i = 0; i < count; i++)
            {
                OutboundAnimationTransfer transfer = _outboundAnimations.Dequeue();
                if (transfer.Id != id)
                    _outboundAnimations.Enqueue(transfer);
                else
                    DisposeOutboundAnimationTransfer(transfer);
            }
        }

        private static void RemoveOutboundAnimationTransfersForRecipient(ushort recipient)
        {
            int count = _outboundAnimations.Count;
            for (int i = 0; i < count; i++)
            {
                OutboundAnimationTransfer transfer = _outboundAnimations.Dequeue();
                ushort[] reduced = RemoveRecipientFromSnapshot(transfer.Recipients, recipient);
                if (reduced.Length == 0)
                {
                    DisposeOutboundAnimationTransfer(transfer);
                    continue;
                }
                transfer.Recipients = reduced;
                _outboundAnimations.Enqueue(transfer);
            }
        }

        private static void DisposeOutboundAnimationTransfer(OutboundAnimationTransfer transfer)
        {
            if (transfer == null)
                return;
            DisposeRequest(transfer.PacketJob, "animation packet request");
            transfer.PacketJob = null;
            DisposeRequest(transfer.Packets, "animation packet batch");
            transfer.Packets = null;
        }

        private static void DisposeRequest(IDisposable request, string description)
        {
            if (request == null)
                return;
            try
            {
                request.Dispose();
            }
            catch (Exception exception)
            {
                BasisDebug.LogWarning(
                    $"Image pickup could not fully dispose {description}: "
                        + $"{exception.GetBaseException().Message}",
                    LogTag
                );
            }
        }

        private static string ResolveOwnerName(ushort senderId)
        {
            if (BasisNetworkPlayer.GetPlayerById(senderId, out BasisNetworkPlayer player))
            {
                string name = player.SafeDisplayName;
                if (string.IsNullOrEmpty(name)) name = player.displayName;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return $"Player {senderId}";
        }

        private static bool TrySkipWireString(BinaryReader reader, int maxByteLength)
        {
            if (!TryRead7BitEncodedInt(reader, out int byteLength)) return false;
            if (byteLength < 0 || byteLength > maxByteLength) return false;

            long remainingBytes = reader.BaseStream.Length - reader.BaseStream.Position;
            if (remainingBytes < byteLength) return false;

            reader.BaseStream.Position += byteLength;
            return true;
        }

        private static bool TryRead7BitEncodedInt(BinaryReader reader, out int value)
        {
            value = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (reader.BaseStream.Length - reader.BaseStream.Position < 1) return false;

                byte b = reader.ReadByte();
                if (shift == 28 && (b & 0xF0) != 0) return false;

                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }

        internal static string ReadBoundedOwnerName(BinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));

            uint byteLength = 0;
            for (int byteIndex = 0; byteIndex < 5; byteIndex++)
            {
                byte value = reader.ReadByte();
                if (byteIndex == 4 && (value & 0xF8) != 0)
                    throw new InvalidDataException("Owner name length prefix is invalid.");
                byteLength |= (uint)(value & 0x7F) << (byteIndex * 7);
                if ((value & 0x80) != 0)
                    continue;

                if (byteLength > MaxOwnerNameUtf8Bytes)
                {
                    throw new InvalidDataException($"Owner name exceeds {MaxOwnerNameUtf8Bytes:N0} UTF-8 bytes.");
                }
                byte[] bytes = reader.ReadBytes((int)byteLength);
                if (bytes.Length != (int)byteLength)
                    throw new EndOfStreamException("Owner name is truncated.");
                return StrictUtf8.GetString(bytes);
            }

            throw new InvalidDataException("Owner name length prefix is invalid.");
        }

        internal static string NormalizeOwnerNameForNetwork(string ownerName)
        {
            string value = ownerName ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(value) <= MaxOwnerNameUtf8Bytes)
                return value;

            int characterCount = Math.Min(value.Length, MaxOwnerNameUtf8Bytes);
            while (characterCount > 0)
            {
                if (char.IsHighSurrogate(value[characterCount - 1]))
                    characterCount--;
                string candidate = value.Substring(0, characterCount);
                if (Encoding.UTF8.GetByteCount(candidate) <= MaxOwnerNameUtf8Bytes)
                    return candidate;
                characterCount--;
            }
            return string.Empty;
        }

        private static byte[] EncodeSpawn(
            Guid id,
            ushort ownerId,
            string ownerName,
            int width,
            int height,
            int totalBytes,
            int totalChunks,
            Vector3 position,
            Quaternion rotation
        )
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(OpSpawn);
            BasisAnimatedImageNetworkCodec.WriteGuid(writer, id);
            writer.Write(ownerId);
            writer.Write(NormalizeOwnerNameForNetwork(ownerName));
            writer.Write(width);
            writer.Write(height);
            writer.Write(totalBytes);
            writer.Write(totalChunks);
            WritePose(writer, position, rotation);
            writer.Flush();
            return stream.ToArray();
        }

        /// <summary>
        /// Writes one chunk packet into <paramref name="destination"/>, which must be
        /// <see cref="ImageChunkHeaderBytes"/> plus <paramref name="length"/> bytes long. The transports
        /// copy the buffer before returning, so a caller may reuse one array for every chunk of a transfer.
        /// </summary>
        private static void EncodeChunkInto(
            byte[] destination,
            Guid id,
            int chunkIndex,
            byte[] source,
            int offset,
            int length
        )
        {
            using var stream = new MemoryStream(destination, true);
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(OpChunk);
            BasisAnimatedImageNetworkCodec.WriteGuid(writer, id);
            writer.Write(chunkIndex);
            writer.Write(length);
            writer.Write(source, offset, length);
            writer.Flush();
        }

        private static byte[] EncodeTransform(Guid id, Vector3 position, Quaternion rotation, float scale)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(OpTransform);
            BasisAnimatedImageNetworkCodec.WriteGuid(writer, id);
            WritePose(writer, position, rotation);
            writer.Write(scale);
            writer.Flush();
            return stream.ToArray();
        }

        private static byte[] EncodeDespawn(Guid id)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(OpDespawn);
            BasisAnimatedImageNetworkCodec.WriteGuid(writer, id);
            writer.Flush();
            return stream.ToArray();
        }

        private static byte[] EncodeClaim(Guid id)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(OpClaim);
            BasisAnimatedImageNetworkCodec.WriteGuid(writer, id);
            writer.Flush();
            return stream.ToArray();
        }

        private static void WritePose(BinaryWriter writer, Vector3 position, Quaternion rotation)
        {
            writer.Write(position.x);
            writer.Write(position.y);
            writer.Write(position.z);
            writer.Write(rotation.x);
            writer.Write(rotation.y);
            writer.Write(rotation.z);
            writer.Write(rotation.w);
        }

        internal static Vector3 CalculateBatchSpawnLocalOffset(int index, int count)
        {
            int columns = Mathf.Min(BasisImagePickupSettings.BatchSpawnColumns, count);
            return CalculateBatchSpawnLocalOffset(index, count, columns, float.NegativeInfinity);
        }

        internal static Vector3 CalculateBatchSpawnLocalOffset(int index, int count, int columns, float minimumLocalY)
        {
            if (count <= 1)
            {
                if (index != 0)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return new Vector3(0f, Mathf.Max(0f, minimumLocalY), 0f);
            }
            if (index < 0 || index >= count)
                throw new ArgumentOutOfRangeException(nameof(index));

            columns = Mathf.Clamp(columns, 1, count);
            int rows = (count + columns - 1) / columns;
            int row = index / columns;
            int column = index % columns;
            int itemsInRow = Mathf.Min(columns, count - row * columns);

            float x =
                (column - (itemsInRow - 1) * 0.5f)
                * BasisImagePickupSettings.BatchSpawnHorizontalSpacingMeters;
            float centeredY =
                ((rows - 1) * 0.5f - row)
                * BasisImagePickupSettings.BatchSpawnVerticalSpacingMeters;
            float lowestCenteredY =
                -(rows - 1)
                * 0.5f
                * BasisImagePickupSettings.BatchSpawnVerticalSpacingMeters;
            float upwardShift = float.IsNegativeInfinity(minimumLocalY)
                ? 0f
                : Mathf.Max(0f, minimumLocalY - lowestCenteredY);
            return new Vector3(x, centeredY + upwardShift, 0f);
        }

        internal static int CalculateBatchSpawnColumns(int count, float batchCenterY, float minimumCenterY)
        {
            if (count <= 1)
                return 1;

            float verticalSpacing = Mathf.Max(0.01f, BasisImagePickupSettings.BatchSpawnVerticalSpacingMeters);
            float availableDownward = Mathf.Max(0f, batchCenterY - minimumCenterY);
            int rowsWithoutCrossingMinimum = Mathf.Max(
                1,
                Mathf.FloorToInt(availableDownward * 2f / verticalSpacing) + 1
            );
            int requiredColumns = Mathf.CeilToInt(count / (float)rowsWithoutCrossingMinimum);
            int defaultColumns = Mathf.Min(BasisImagePickupSettings.BatchSpawnColumns, count);
            int maximumColumns = Mathf.Min(BasisImagePickupSettings.BatchSpawnMaximumColumns, count);
            return Mathf.Clamp(Mathf.Max(defaultColumns, requiredColumns), 1, maximumColumns);
        }

        private static float GetMinimumBatchImageCenterY(float batchCenterY)
        {
            float playerGroundY =
                BasisLocalPlayer.Instance != null
                    ? BasisLocalPlayer.Instance.transform.position.y
                    : batchCenterY - 1.5f;
            return playerGroundY
                + BasisImagePickupSettings.BaseHeightMeters * 0.5f
                + BasisImagePickupSettings.BatchSpawnGroundClearanceMeters;
        }

        private static void GetSpawnPose(out Vector3 position, out Quaternion rotation)
        {
            if (BasisLocalCameraDriver.HasInstance && BasisLocalCameraDriver.Instance != null)
            {
                BasisLocalCameraDriver.Instance.transform.GetPositionAndRotation(
                    out Vector3 cameraPosition,
                    out Quaternion cameraRotation
                );
                Vector3 forward = cameraRotation * Vector3.forward;
                position =
                    cameraPosition + forward * BasisImagePickupSettings.SpawnDistance;
                rotation = Quaternion.LookRotation(forward, Vector3.up);
            }
            else
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
            }
        }

        // ── Animated image scheduler ───────────────────────────────────────────────────────────────

        private static void EnsureSchedulerResources()
        {
            if (_schedulerReady)
                return;
            _schedulerReady = true;

            BasisDebug.Log(
                BasisImagePickupSettings.UseDepthBufferAnimationVisibility
                    ? "Image pickup animation visibility uses the depth buffer."
                    : "Image pickup animation visibility uses front-face physics.",
                RenderLogTag
            );
            _commands = new CommandBuffer { name = "Basis Animated Images" };
            for (int i = _visibilityFrustums.Count; i < _visibilityFrustums.Capacity; i++)
                _visibilityFrustums.Add(new Plane[6]);
            if (BasisImagePickupSettings.UseDepthBufferAnimationVisibility)
                BasisAnimatedImageDepthVisibility.Initialize();

            Shader shader = Shader.Find(CompositorShaderName);
            if (BasisImagePickupRuntimeUtility.CanUseAnimationCompositorShader(shader))
            {
                _compositorMaterial = new Material(shader)
                {
                    name = "Basis Animated Image Compositor",
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            else
            {
                BasisDebug.LogWarning(
                    "Animated image GPU compositor shader is missing, unsupported, or incomplete; "
                        + "using the CPU fallback.",
                    RenderLogTag
                );
            }

            BasisEventDriver.OnLateUpdate += SimulateLateUpdate;
            Application.onBeforeRender += FlushPendingJobsBeforeRender;
        }

        private static void ReleaseSchedulerResources()
        {
            if (!_schedulerReady)
                return;
            _schedulerReady = false;

            BasisEventDriver.OnLateUpdate -= SimulateLateUpdate;
            Application.onBeforeRender -= FlushPendingJobsBeforeRender;
            FlushPendingJobs();
            BasisAnimatedImageDepthVisibility.Shutdown();

            if (_commands != null)
            {
                _commands.Release();
                _commands = null;
            }
            if (_compositorMaterial != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    UnityEngine.Object.DestroyImmediate(_compositorMaterial);
                else
#endif
                    UnityEngine.Object.Destroy(_compositorMaterial);
                _compositorMaterial = null;
            }
            _players.Clear();
            _pendingRemoval.Clear();
            _pendingDecodedReleases.Clear();
            _pendingCompositorReleases.Clear();
            _pendingJobFlush.Clear();
            _compositorPriorityCandidate = null;
            _cpuFrontFacingPlayers.Clear();
            _reloadDecodePlayers.Clear();
            _visibilityCameras.Clear();
            _visibilityCameraPositions.Clear();
            _visibilityCameraForwards.Clear();
            _visibilityCameraOrthographic.Clear();
            _registeredCameraScratch.Clear();
            _visibilityFrustums.Clear();
            _localVisibilityCameraIndex = -1;
            _activeReloadDecodes = 0;
            _visiblePassStartIndex = 0;
            _cameraMaskLimitWarningLogged = false;
        }

        internal static void RegisterAnimatedPlayer(BasisAnimatedImagePlayer player)
        {
            if (player == null || _players.Contains(player))
                return;
            EnsureSchedulerResources();
            _players.Add(player);
        }

        internal static void UnregisterAnimatedPlayer(BasisAnimatedImagePlayer player)
        {
            if (player == null)
                return;
            _pendingDecodedReleases.Remove(player);
            _pendingCompositorReleases.Remove(player);
            _pendingJobFlush.Remove(player);
            if (ReferenceEquals(_compositorPriorityCandidate, player))
                _compositorPriorityCandidate = null;
            int playerIndex = _players.IndexOf(player);
            if (playerIndex >= 0)
                RemovePlayerAt(playerIndex);
        }

        internal static void RequestCompositorMemory(BasisAnimatedImagePlayer candidate)
        {
            Camera localCamera = BasisLocalCameraDriver.CameraInstance;
            if (candidate == null || localCamera == null)
                return;

            Vector3 cameraPosition = localCamera.transform.position;
            float candidateDistanceSquared = candidate.GetDistanceSquared(cameraPosition);
            if (
                _compositorPriorityCandidate == null
                || candidateDistanceSquared
                    < _compositorPriorityCandidate.GetDistanceSquared(cameraPosition)
            )
            {
                _compositorPriorityCandidate = candidate;
            }
            if (_pendingCompositorReleases.Count > 0)
                return;

            candidate = _compositorPriorityCandidate;
            candidateDistanceSquared = candidate.GetDistanceSquared(cameraPosition);
            BasisAnimatedImagePlayer farthest = null;
            float farthestDistanceSquared = candidateDistanceSquared;
            int playerCount = _players.Count;
            for (int i = 0; i < playerCount; i++)
            {
                BasisAnimatedImagePlayer player = _players[i];
                if (
                    player == null
                    || ReferenceEquals(player, candidate)
                    || !player.HasAllocatedCompositor
                )
                {
                    continue;
                }

                float distanceSquared = player.GetDistanceSquared(cameraPosition);
                if (distanceSquared <= farthestDistanceSquared)
                    continue;
                farthest = player;
                farthestDistanceSquared = distanceSquared;
            }

            // Release after the current command buffer is no longer referencing the canvas.
            if (farthest != null)
                _pendingCompositorReleases.Add(farthest);
        }

        internal static bool TryAcquireReloadDecodeSlot(BasisAnimatedImagePlayer player, long nativeBytes)
        {
            return TryAcquireReloadDecodeSlot(
                player,
                nativeBytes,
                BasisImagePickupSettings.MaxRemoteAnimationDecodedFramePixelsPerSender
            );
        }

        internal static bool TryAcquireReloadDecodeSlot(
            BasisAnimatedImagePlayer player,
            long nativeBytes,
            long decodedPixelLimit
        )
        {
            if (
                player == null
                || _activeReloadDecodes >= BasisImagePickupSettings.MaxConcurrentAnimationDecodeJobs
                || _reloadDecodePlayers.Contains(player)
                || _pendingDecodedReleases.Count > 0
                || !TryMakeRoomForDecodedPixels(player, decodedPixelLimit)
                || !BasisAnimatedImageData.TryReserveRestoreBytes(nativeBytes)
            )
            {
                return false;
            }
            _activeReloadDecodes++;
            _reloadDecodePlayers.Add(player);
            return true;
        }

        internal static void ReleaseReloadDecodeSlot(BasisAnimatedImagePlayer player, long nativeBytes)
        {
            int playerIndex = _reloadDecodePlayers.IndexOf(player);
            if (playerIndex >= 0)
            {
                _reloadDecodePlayers.RemoveAt(playerIndex);
                if (_activeReloadDecodes > 0)
                    _activeReloadDecodes--;
            }
            BasisAnimatedImageData.ReleaseRestoreBytes(nativeBytes);
        }

        internal static void EnforceDecodedPixelBudget(BasisAnimatedImagePlayer candidate)
        {
            EnforceDecodedPixelBudget(
                candidate,
                BasisImagePickupSettings.MaxRemoteAnimationDecodedFramePixelsPerSender
            );
        }

        internal static void EnforceDecodedPixelBudget(BasisAnimatedImagePlayer candidate, long decodedPixelLimit)
        {
            if (candidate == null || !candidate.HasDecodedData)
                return;
            TrimDecodedPixelBudget(candidate, false, false, decodedPixelLimit);
        }

        private static bool TryMakeRoomForDecodedPixels(
            BasisAnimatedImagePlayer candidate,
            long decodedPixelLimit
        )
        {
            return TrimDecodedPixelBudget(candidate, true, true, decodedPixelLimit);
        }

        private static bool TrimDecodedPixelBudget(
            BasisAnimatedImagePlayer candidate,
            bool candidateNeedsDecode,
            bool deferReleases,
            long decodedPixelLimit
        )
        {
            long limit = decodedPixelLimit;
            long candidatePixels = candidate.DecodedFramePixels;
            if (limit <= 0)
                return false;
            if (candidatePixels <= 0 || candidatePixels > limit)
                return false;

            // The budget is per owner, so a player whose pickup has gone belongs to nobody's group
            // and is neither counted nor evicted here — its data is released by the unregister
            // path. Treating a missing pickup as owner 0 charged every orphan to whoever joined
            // the instance first, so their images hit this cap and got evicted before anyone else's.
            if (!candidate.TryGetOwnerId(out ushort ownerId))
                return false;

            long decodedPixels = candidateNeedsDecode ? candidatePixels : 0;
            int playerCount = _players.Count;
            for (int i = 0; i < playerCount; i++)
            {
                BasisAnimatedImagePlayer player = _players[i];
                if (
                    player == null
                    || !player.TryGetOwnerId(out ushort playerOwnerId)
                    || playerOwnerId != ownerId
                    || !player.HasDecodedData
                )
                {
                    continue;
                }
                decodedPixels += player.DecodedFramePixels;
            }

            int reloadDecodeCount = _reloadDecodePlayers.Count;
            for (int i = 0; i < reloadDecodeCount; i++)
            {
                BasisAnimatedImagePlayer player = _reloadDecodePlayers[i];
                if (
                    player == null
                    || ReferenceEquals(player, candidate)
                    || !player.TryGetOwnerId(out ushort playerOwnerId)
                    || playerOwnerId != ownerId
                    || player.HasDecodedData
                )
                {
                    continue;
                }
                decodedPixels += player.DecodedFramePixels;
            }

            Camera localCamera = BasisLocalCameraDriver.CameraInstance;
            Vector3 cameraPosition = localCamera != null ? localCamera.transform.position : Vector3.zero;
            float candidateDistanceSquared =
                localCamera != null ? candidate.GetDistanceSquared(cameraPosition) : float.PositiveInfinity;

            if (candidateNeedsDecode && decodedPixels > limit)
            {
                long reclaimablePixels = 0;
                playerCount = _players.Count;
                for (int i = 0; i < playerCount; i++)
                {
                    BasisAnimatedImagePlayer player = _players[i];
                    if (
                        player == null
                        || !player.TryGetOwnerId(out ushort playerOwnerId)
                        || playerOwnerId != ownerId
                        || !player.HasDecodedData
                        || !player.CanReleaseDecodedData
                    )
                    {
                        continue;
                    }

                    float distanceSquared =
                        localCamera != null
                            ? player.GetDistanceSquared(cameraPosition)
                            : 0f;
                    if (distanceSquared <= candidateDistanceSquared)
                        continue;
                    reclaimablePixels += player.DecodedFramePixels;
                }

                if (decodedPixels - reclaimablePixels > limit)
                    return false;
            }

            bool releaseDeferred = false;
            while (decodedPixels > limit)
            {
                BasisAnimatedImagePlayer farthest = null;
                float farthestDistanceSquared = -1f;
                playerCount = _players.Count;
                for (int i = 0; i < playerCount; i++)
                {
                    BasisAnimatedImagePlayer player = _players[i];
                    if (
                        player == null
                        || !player.TryGetOwnerId(out ushort playerOwnerId)
                        || playerOwnerId != ownerId
                        || !player.HasDecodedData
                        || !player.CanReleaseDecodedData
                        || (deferReleases && _pendingDecodedReleases.Contains(player))
                    )
                    {
                        continue;
                    }

                    float distanceSquared;
                    if (localCamera != null)
                        distanceSquared = player.GetDistanceSquared(cameraPosition);
                    else
                        distanceSquared = ReferenceEquals(player, candidate) ? float.PositiveInfinity : 0f;
                    if (distanceSquared <= farthestDistanceSquared)
                        continue;
                    farthest = player;
                    farthestDistanceSquared = distanceSquared;
                }

                if (
                    farthest == null
                    || (
                        candidateNeedsDecode
                        && farthestDistanceSquared <= candidateDistanceSquared
                    )
                )
                {
                    return false;
                }

                decodedPixels -= farthest.DecodedFramePixels;
                if (deferReleases)
                {
                    _pendingDecodedReleases.Add(farthest);
                    releaseDeferred = true;
                }
                else
                {
                    farthest.ReleaseDecodedDataForMemoryPressure();
                }
            }

            return !releaseDeferred;
        }

        internal static void ApplyPendingDecodedReleases()
        {
            int pendingReleaseCount = _pendingDecodedReleases.Count;
            for (int i = 0; i < pendingReleaseCount; i++)
            {
                BasisAnimatedImagePlayer player = _pendingDecodedReleases[i];
                if (player != null)
                    player.ReleaseDecodedDataForMemoryPressure();
            }
            _pendingDecodedReleases.Clear();
        }

        internal static void ApplyPendingCompositorReleases()
        {
            int pendingReleaseCount = _pendingCompositorReleases.Count;
            for (int i = 0; i < pendingReleaseCount; i++)
            {
                BasisAnimatedImagePlayer player = _pendingCompositorReleases[i];
                if (player != null)
                    player.ReleaseCompositorForMemoryPressure();
            }
            _pendingCompositorReleases.Clear();
        }

        internal static void PrioritizeDeferredCompositorCandidate()
        {
            BasisAnimatedImagePlayer candidate = _compositorPriorityCandidate;
            _compositorPriorityCandidate = null;
            if (candidate == null)
                return;
            int candidateIndex = _players.IndexOf(candidate);
            if (candidateIndex >= 0)
                _visiblePassStartIndex = candidateIndex;
        }

        private static void SimulateLateUpdate()
        {
            try
            {
                SimulateLateUpdateBody();
            }
            catch (Exception exception)
            {
                BasisDebug.LogErrorOnce(
                    $"Animated image scheduler simulation failed with {_players.Count:N0} players, "
                        + $"{_pendingCompositorReleases.Count:N0} pending compositor releases, "
                        + $"and {_activeReloadDecodes:N0} active reload decodes: {exception}",
                    RenderLogTag
                );
            }
        }

        private static void SimulateLateUpdateBody()
        {
            // Fallback only: BeforeRender normally lands these before the frame renders.
            FlushPendingJobs();

            if (_players.Count == 0)
            {
                _pendingDecodedReleases.Clear();
                _pendingCompositorReleases.Clear();
                _compositorPriorityCandidate = null;
                return;
            }

            using (BasisImagePickupMarkers.Schedule.Auto())
            {
                int registeredPlayerCount = _players.Count;
                for (int i = registeredPlayerCount - 1; i >= 0; i--)
                {
                    if (_players[i] == null)
                        RemovePlayerAt(i);
                }
                if (_players.Count == 0)
                {
                    _pendingDecodedReleases.Clear();
                    _pendingCompositorReleases.Clear();
                    _compositorPriorityCandidate = null;
                    return;
                }

                _commands.Clear();
                // Canvas disposal is safe only after commands from the previous pass are discarded.
                ApplyPendingCompositorReleases();
                ApplyPendingDecodedReleases();
                EnforceResidentNativeBudget();
                _pendingRemoval.Clear();

                bool useDepthBufferOcclusion =
                    BasisImagePickupSettings.UseDepthBufferAnimationVisibility;
                CollectVisibilityCameras();
                float unscaledTime = Time.unscaledTime;
                int frameCount = Time.frameCount;
                PrepareCpuFrontFacingPlayers(frameCount, unscaledTime);

                if (useDepthBufferOcclusion && BasisAnimatedImageDepthVisibility.IsActive)
                {
                    BasisAnimatedImageDepthVisibility.PrepareFrame(
                        _cpuFrontFacingPlayers,
                        BasisLocalCameraDriver.CameraInstance,
                        unscaledTime
                    );
                }

                int transitionsRemaining =
                    BasisImagePickupSettings.MaxAnimationTransitionsPerFrame;
                long pixelsRemaining =
                    BasisImagePickupSettings.MaxAnimationCompositedPixelsPerFrame;
                int raycastsRemaining =
                    BasisImagePickupSettings.MaxAnimationFaceOcclusionRaycastsPerFrame;
                bool gpuCommandsAdded = false;
                long synchronizedTicks = BasisNetworkManagement.RemoteUtcTime().Ticks;
                // Give released memory to the nearest blocked animation before farther players retry.
                PrioritizeDeferredCompositorCandidate();

                ScheduleVisiblePlayers(
                    frameCount,
                    synchronizedTicks,
                    unscaledTime,
                    ref _visiblePassStartIndex,
                    useDepthBufferOcclusion,
                    ref transitionsRemaining,
                    ref pixelsRemaining,
                    ref raycastsRemaining,
                    ref gpuCommandsAdded
                );

                int pendingRemovalCount = _pendingRemoval.Count;
                for (int i = 0; i < pendingRemovalCount; i++)
                {
                    int playerIndex = _players.IndexOf(_pendingRemoval[i]);
                    if (playerIndex >= 0)
                        RemovePlayerAt(playerIndex);
                }

                if (gpuCommandsAdded)
                {
                    using (BasisImagePickupMarkers.GpuCommands.Auto())
                    {
                        Graphics.ExecuteCommandBuffer(_commands);
                    }
                }
            }
        }

        /// <summary>
        /// Latest point that still lands the pixels before this frame renders. Completing here rather
        /// than at the end of the schedule pass gives the composition and atlas jobs the whole tail of
        /// the frame to run on the workers instead of stalling the main thread on the spot.
        /// </summary>
        private static void FlushPendingJobsBeforeRender()
        {
            try
            {
                FlushPendingJobs();
            }
            catch (Exception exception)
            {
                BasisDebug.LogErrorOnce(
                    $"Animated image job flush failed with {_players.Count:N0} players: {exception}",
                    RenderLogTag
                );
            }
        }

        /// <summary>
        /// Completes every job scheduled during this pass and performs the main-thread uploads.
        /// Players disposed mid-pass already completed their own handles, so they no-op here.
        /// </summary>
        private static void FlushPendingJobs()
        {
            int pendingCount = _pendingJobFlush.Count;
            if (pendingCount == 0)
                return;

            try
            {
                using (BasisImagePickupMarkers.JobFlush.Auto())
                {
                    for (int i = 0; i < pendingCount; i++)
                    {
                        BasisAnimatedImagePlayer player = _pendingJobFlush[i];
                        if (player != null)
                            player.FlushPendingJobs();
                    }
                }
            }
            finally
            {
                _pendingJobFlush.Clear();
            }
        }

        private static void EnforceResidentNativeBudget()
        {
            long limit = BasisImagePickupSettings.MaxResidentAnimationNativeBytes;
            if (BasisAnimatedImageData.TotalResidentNativeBytes <= limit)
                return;

            int playerCount = _players.Count;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = playerCount - 1; i >= 0; i--)
                {
                    BasisAnimatedImagePlayer player = _players[i];
                    if (player == null || !player.CanReleaseDecodedData || (pass == 0 && player.HasAllocatedCompositor))
                    {
                        continue;
                    }
                    player.ReleaseDecodedDataForMemoryPressure();
                    if (BasisAnimatedImageData.TotalResidentNativeBytes <= limit)
                        return;
                }
            }
        }

        private static void RemovePlayerAt(int playerIndex)
        {
            _players.RemoveAt(playerIndex);
            _visiblePassStartIndex = AdjustStartIndexAfterRemoval(_visiblePassStartIndex, playerIndex, _players.Count);
        }

        internal static int AdjustStartIndexAfterRemoval(int startIndex, int removedIndex, int remainingCount)
        {
            if (remainingCount <= 0)
                return 0;
            if (removedIndex < startIndex)
                startIndex--;
            if (startIndex < 0)
                return 0;
            return startIndex % remainingCount;
        }

        private static void PrepareCpuFrontFacingPlayers(int frame, float unscaledTime)
        {
            using var scope = BasisImagePickupMarkers.CpuFrontFacing.Auto();
            _cpuFrontFacingPlayers.Clear();
            int playerCount = _players.Count;
            for (int playerIndex = 0; playerIndex < playerCount; playerIndex++)
            {
                BasisAnimatedImagePlayer player = _players[playerIndex];
                if (player == null || !player.IsInitialized)
                    continue;

                ulong cameraMask = player.IsHidden
                    ? 0
                    : CalculateCpuFrontFacingCameraMask(player);
                player.SetCpuFrontFacingCameraMask(cameraMask, frame);
                if (cameraMask == 0)
                {
                    player.ResetFaceVisibility();
                    player.ResetDepthVisibility();
                    player.UpdateVisibilityState(false, unscaledTime);
                    continue;
                }
                _cpuFrontFacingPlayers.Add(player);
            }
        }

        private static ulong CalculateCpuFrontFacingCameraMask(BasisAnimatedImagePlayer player)
        {
            BasisImagePickupObject pickup = player.Pickup;
            if (pickup == null || !pickup.HasFrontRenderer)
                return 0;

            Bounds bounds = pickup.FrontRendererBounds;
            pickup.GetFrontFacePose(out Vector3 faceCenter, out Vector3 frontNormal);
            int cameraCount = Mathf.Min(_visibilityCameras.Count, MaximumCpuFacingCameraBits);
            ulong cameraMask = 0;
            for (int cameraIndex = 0; cameraIndex < cameraCount; cameraIndex++)
            {
                Camera camera = _visibilityCameras[cameraIndex];
                if (
                    !IsCpuFrontFacingCandidate(
                        pickup.FrontRendererLayer,
                        bounds,
                        _visibilityFrustums[cameraIndex],
                        frontNormal,
                        faceCenter,
                        _visibilityCameraPositions[cameraIndex],
                        _visibilityCameraForwards[cameraIndex],
                        _visibilityCameraOrthographic[cameraIndex],
                        camera.cullingMask
                    )
                )
                {
                    continue;
                }
                cameraMask |= 1UL << cameraIndex;
            }
            return cameraMask;
        }

        internal static bool IsCpuFrontFacingCandidate(
            int rendererLayer,
            Bounds bounds,
            Plane[] frustumPlanes,
            Vector3 frontNormal,
            Vector3 faceCenter,
            Vector3 cameraPosition,
            Vector3 cameraForward,
            bool orthographic,
            int cameraCullingMask
        )
        {
            if (rendererLayer < 0 || rendererLayer > 31)
                return false;
            int layerMaskBit = 1 << rendererLayer;
            if ((cameraCullingMask & layerMaskBit) == 0)
                return false;
            if (!IsFrontFacingCamera(frontNormal, faceCenter, cameraPosition, cameraForward, orthographic))
            {
                return false;
            }
            return frustumPlanes != null
                && frustumPlanes.Length >= 6
                && GeometryUtility.TestPlanesAABB(frustumPlanes, bounds);
        }

        private static void ScheduleVisiblePlayers(
            int frameCount,
            long synchronizedTicks,
            float unscaledTime,
            ref int startIndex,
            bool useDepthBufferOcclusion,
            ref int transitionsRemaining,
            ref long pixelsRemaining,
            ref int raycastsRemaining,
            ref bool gpuCommandsAdded
        )
        {
            int playerCount = _players.Count;
            if (playerCount == 0)
                return;

            int normalizedStartIndex = startIndex % playerCount;
            if (normalizedStartIndex < 0)
                normalizedStartIndex += playerCount;

            for (int offset = 0; offset < playerCount; offset++)
            {
                int playerIndex = (normalizedStartIndex + offset) % playerCount;
                BasisAnimatedImagePlayer player = _players[playerIndex];
                if (player == null)
                    continue;
                if (!player.IsInitialized)
                {
                    _pendingRemoval.Add(player);
                    continue;
                }

                bool hasCpuFacingMask = player.TryGetCpuFrontFacingCameraMask(
                    frameCount,
                    out ulong cpuFacingCameraMask
                );
                if (player.IsHidden || !hasCpuFacingMask || cpuFacingCameraMask == 0)
                    continue;

                bool visible = useDepthBufferOcclusion
                    ? EvaluateDepthBufferVisibility(player, unscaledTime, cpuFacingCameraMask, ref raycastsRemaining)
                    : EvaluatePhysicsVisibility(
                        player,
                        unscaledTime,
                        cpuFacingCameraMask,
                        false,
                        ref raycastsRemaining
                    );
                player.UpdateVisibilityState(visible, unscaledTime);
                if (!visible)
                    continue;

                player.Schedule(
                    _commands,
                    synchronizedTicks,
                    ref transitionsRemaining,
                    ref pixelsRemaining,
                    ref gpuCommandsAdded
                );
                if (player.HasPendingJobs)
                {
                    // Kick each job as it is produced; the remaining players' occlusion raycasts
                    // then act as worker-thread cover instead of delaying the batch.
                    _pendingJobFlush.Add(player);
                    JobHandle.ScheduleBatchedJobs();
                }

                if (transitionsRemaining <= 0 || pixelsRemaining <= 0)
                {
                    startIndex = (playerIndex + 1) % playerCount;
                    return;
                }
            }

            // Rotate even when the pass completes without exhausting its budget so the
            // same registration does not permanently receive first consideration.
            startIndex = (normalizedStartIndex + 1) % playerCount;
        }

        private static bool EvaluatePhysicsVisibility(
            BasisAnimatedImagePlayer player,
            float unscaledTime,
            ulong cpuFacingCameraMask,
            bool skipLocalCamera,
            ref int raycastsRemaining
        )
        {
            if (
                player.NeedsFaceOcclusionCheck(unscaledTime)
                && TryEvaluateFaceVisibility(
                    player,
                    cpuFacingCameraMask,
                    skipLocalCamera,
                    ref raycastsRemaining,
                    out bool evaluatedVisible
                )
            )
            {
                player.SetFaceVisibility(evaluatedVisible, unscaledTime);
            }
            return player.IsFaceVisible;
        }

        private static bool EvaluateDepthBufferVisibility(
            BasisAnimatedImagePlayer player,
            float unscaledTime,
            ulong cpuFacingCameraMask,
            ref int raycastsRemaining
        )
        {
            if (
                BasisAnimatedImageDepthVisibility.IsActive
                && player.TryGetDepthVisibility(unscaledTime, out bool mainCameraVisible)
            )
            {
                bool mainCameraCpuFacing =
                    _localVisibilityCameraIndex >= 0
                    && _localVisibilityCameraIndex < MaximumCpuFacingCameraBits
                    && (cpuFacingCameraMask & (1UL << _localVisibilityCameraIndex))
                        != 0;
                if (mainCameraVisible && mainCameraCpuFacing)
                    return true;

                // Registered secondary cameras do not share the main camera depth texture.
                return EvaluatePhysicsVisibility(
                    player,
                    unscaledTime,
                    cpuFacingCameraMask,
                    true,
                    ref raycastsRemaining
                );
            }

            // Until the first asynchronous readback arrives, retain the physics path so
            // depth mode never incorrectly freezes newly spawned or newly visible cards.
            return EvaluatePhysicsVisibility(player, unscaledTime, cpuFacingCameraMask, false, ref raycastsRemaining);
        }

        private static bool TryEvaluateFaceVisibility(
            BasisAnimatedImagePlayer player,
            ulong cpuFacingCameraMask,
            bool skipLocalCamera,
            ref int raycastsRemaining,
            out bool visible
        )
        {
            visible = false;
            BasisImagePickupObject pickup = player.Pickup;
            if (pickup == null || !pickup.HasFrontRenderer)
                return true;

            pickup.GetFrontFacePose(out _, out Vector3 frontNormal);
            int cameraCount = Mathf.Min(_visibilityCameras.Count, MaximumCpuFacingCameraBits);
            for (int i = 0; i < cameraCount; i++)
            {
                if ((cpuFacingCameraMask & (1UL << i)) == 0)
                    continue;
                Camera camera = _visibilityCameras[i];
                if (skipLocalCamera && ReferenceEquals(camera, BasisLocalCameraDriver.CameraInstance))
                {
                    continue;
                }

                if (
                    !TryHasUnoccludedFaceSample(
                        pickup,
                        camera,
                        _visibilityCameraPositions[i],
                        _visibilityCameraForwards[i],
                        _visibilityCameraOrthographic[i],
                        frontNormal,
                        ref raycastsRemaining,
                        out bool cameraCanSeeFace
                    )
                )
                {
                    return false;
                }
                if (cameraCanSeeFace)
                {
                    visible = true;
                    return true;
                }
            }

            return true;
        }

        internal static bool IsFrontFacingCamera(
            Vector3 frontNormal,
            Vector3 faceCenter,
            Vector3 cameraPosition,
            Vector3 cameraForward,
            bool orthographic
        )
        {
            Vector3 towardCamera = orthographic
                ? -cameraForward
                : cameraPosition - faceCenter;
            return Vector3.Dot(frontNormal, towardCamera) > 0.0001f;
        }

        private static bool TryHasUnoccludedFaceSample(
            BasisImagePickupObject pickup,
            Camera camera,
            Vector3 cameraPosition,
            Vector3 cameraForward,
            bool cameraOrthographic,
            Vector3 frontNormal,
            ref int raycastsRemaining,
            out bool visible
        )
        {
            visible = false;
            for (int sampleIndex = 0; sampleIndex < FrontFaceSampleCount; sampleIndex++)
            {
                if (raycastsRemaining <= 0)
                    return false;
                raycastsRemaining--;

                Vector3 sample = pickup.GetFrontFaceOcclusionSample(sampleIndex, frontNormal);
                if (IsFaceSampleUnoccluded(pickup, camera, cameraPosition, cameraForward, cameraOrthographic, sample))
                {
                    visible = true;
                    return true;
                }
            }
            return true;
        }

        private static bool IsFaceSampleUnoccluded(
            BasisImagePickupObject pickup,
            Camera camera,
            Vector3 cameraPosition,
            Vector3 cameraForward,
            bool cameraOrthographic,
            Vector3 sample
        )
        {
            Vector3 origin;
            Vector3 direction;
            float distance;

            if (cameraOrthographic)
            {
                direction = cameraForward;
                distance = Vector3.Dot(sample - cameraPosition, direction);
                if (distance <= 0f)
                    return false;
                origin = sample - direction * distance;
            }
            else
            {
                origin = cameraPosition;
                Vector3 toSample = sample - origin;
                distance = toSample.magnitude;
                if (distance <= 0.0001f)
                    return true;
                direction = toSample / distance;
            }

            distance = Mathf.Max(
                0f,
                distance
                    - BasisImagePickupSettings.AnimationFaceOcclusionSurfaceOffsetMeters
                        * 0.5f
            );
            if (distance <= 0.0001f)
                return true;

            // Use the camera's own culling mask so geometry it cannot render does not occlude.
            int layerMask = camera.cullingMask & Physics.DefaultRaycastLayers;
            int hitCount = Physics.RaycastNonAlloc(
                origin,
                direction,
                _raycastHits,
                distance,
                layerMask,
                QueryTriggerInteraction.Collide
            );

            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = _raycastHits[i].collider;
                if (!IsBlockingOcclusionCollider(collider, pickup))
                    continue;
                return false;
            }

            return hitCount < _raycastHits.Length;
        }

        internal static bool IsBlockingOcclusionCollider(Collider collider, BasisImagePickupObject target)
        {
            if (collider == null || target == null || target.OwnsCollider(collider))
                return false;

            if (!collider.isTrigger)
                return true;

            // Image-card colliders are registered by their pickup owner. Unrelated trigger
            // volumes stay invisible without component discovery in this hot path.
            return BasisImagePickupObject.TryGetPickup(collider, out BasisImagePickupObject otherImage)
                && otherImage != target;
        }

        private static void CollectVisibilityCameras()
        {
            _visibilityCameras.Clear();
            Camera localCamera = BasisLocalCameraDriver.CameraInstance;
            AddVisibilityCamera(localCamera);
            _localVisibilityCameraIndex = _visibilityCameras.IndexOf(localCamera);

            _registeredCameraScratch.Clear();
            BasisCullingCameraRegistry.CollectInto(_registeredCameraScratch);
            int registeredCameraCount = _registeredCameraScratch.Count;
            for (int i = 0; i < registeredCameraCount; i++)
                AddVisibilityCamera(_registeredCameraScratch[i]);

            int cameraCount = _visibilityCameras.Count;
            while (_visibilityFrustums.Count < cameraCount)
                _visibilityFrustums.Add(new Plane[6]);

            _visibilityCameraPositions.Clear();
            _visibilityCameraForwards.Clear();
            _visibilityCameraOrthographic.Clear();
            for (int i = 0; i < cameraCount; i++)
            {
                Camera camera = _visibilityCameras[i];
                camera.transform.GetPositionAndRotation(out Vector3 cameraPosition, out Quaternion cameraRotation);
                _visibilityCameraPositions.Add(cameraPosition);
                _visibilityCameraForwards.Add(cameraRotation * Vector3.forward);
                _visibilityCameraOrthographic.Add(camera.orthographic);
                GeometryUtility.CalculateFrustumPlanes(camera, _visibilityFrustums[i]);
            }
        }

        private static void AddVisibilityCamera(Camera camera)
        {
            if (!IsGameplayVisibilityCamera(camera) || _visibilityCameras.Contains(camera))
            {
                return;
            }
            if (_visibilityCameras.Count >= MaximumCpuFacingCameraBits)
            {
                if (!_cameraMaskLimitWarningLogged)
                {
                    _cameraMaskLimitWarningLogged = true;
                    BasisDebug.LogWarning(
                        $"Animated image visibility supports at most {MaximumCpuFacingCameraBits} gameplay cameras; additional cameras are ignored.",
                        RenderLogTag
                    );
                }
                return;
            }
            _visibilityCameras.Add(camera);
        }

        internal static bool IsSupportedVisibilityCameraType(CameraType cameraType)
        {
            return cameraType != CameraType.SceneView
                && cameraType != CameraType.Preview;
        }

        private static bool IsGameplayVisibilityCamera(Camera camera)
        {
            return camera != null
                && camera.isActiveAndEnabled
                && IsSupportedVisibilityCameraType(camera.cameraType);
        }
    }
}
