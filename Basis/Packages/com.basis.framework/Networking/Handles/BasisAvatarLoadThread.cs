using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using System;
using System.Collections.Concurrent;
using System.Threading;
using static SerializableBasis;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Everything about a joining player that can be produced without touching Unity, handed to
    /// the main thread as one record so the spawn step is nothing but object creation.
    /// </summary>
    public sealed class BasisPreparedJoin
    {
        public ServerReadyMessage Ready;
        public ushort PlayerId;
        /// <summary>Rich-text-stripped name; the regex ran on the load thread.</summary>
        public string SafeDisplayName;
        /// <summary>
        /// Decoded spawn avatar record. Null when the joiner sent no avatar bytes or the blob
        /// failed to decode — <see cref="AvatarDecodeError"/> says which.
        /// </summary>
        public BasisLoadableBundle InitialAvatar;
        public string AvatarDecodeError;
        /// <summary>
        /// Pooled buffer holding the spawn pose. Ownership passes to the receiver on the main
        /// thread; if the record is dropped before then it MUST be returned to the pool.
        /// </summary>
        public BasisAvatarBuffer SpawnPose;
        /// <summary>
        /// Connection generation this record was decoded under; see
        /// <see cref="BasisAvatarLoadThread.Flush"/>. A record whose generation is stale belongs
        /// to a connection that has already torn down and must not spawn.
        /// </summary>
        public int Generation;
    }

    /// <summary>
    /// The avatar load thread: a single dedicated background thread that owns the pure-managed
    /// half of a player join.
    /// </summary>
    /// <remarks>
    /// The spawn channels arrive on the LiteNetLib receive thread (<c>UnsyncedEvents = true</c>,
    /// see <c>LNLNetManager</c>) but both handlers used to wrap their whole body in
    /// <c>BasisDeviceManagement.EnqueueOnMainThread</c> — deliberately marshalling the DECODE onto
    /// the frame thread, not just the spawn. Per join fill that decode is a Deflate inflate of up
    /// to 32 KB per batch, one <c>ServerReadyMessage.Deserialize</c> per player already present
    /// (display name, UUID, platform, avatar blob, pose bytes), a second DeflateStream round trip
    /// inside each avatar blob, a rich-text regex, and the spawn-pose bit unpack. None of it
    /// touches Unity, and at the scales this project is tested at (~2k players) it is the bulk of
    /// the join cost that is not calibration.
    ///
    /// So the channel handlers now do the only thing they must do on the receive thread — copy the
    /// bytes out of the pooled <c>NetPacketReader</c> and recycle it — and hand the rest here. The
    /// main thread picks up a finished <see cref="BasisPreparedJoin"/> through the existing
    /// budgeted <c>LifecycleQueue</c> and is left with Unity-affine work only: the mouth marker,
    /// the nameplate, the receiver, and the fallback avatar's calibration.
    ///
    /// NOT decoded inline on the receive thread, which would have been the smaller change: that
    /// thread is LiteNetLib's socket read loop, and parking it in a 32 KB inflate plus 2000 record
    /// decodes stalls reception of every other channel — pose and voice included — for the
    /// duration. A DEDICATED thread rather than the pool for three further reasons: joins stay in
    /// wire order (one consumer, one queue), a 2k join fill cannot conscript every pool thread and
    /// starve the bundle download/decrypt tasks that share it, and the GC churn of decode is
    /// confined to a thread nothing else is waiting on.
    ///
    /// What deliberately did NOT move: avatar calibration. It is Animator/Transform/
    /// TransformAccessArray work end to end and is main-thread by construction.
    /// </remarks>
    [AutoStaticsCleanup]
    public static partial class BasisAvatarLoadThread
    {
        private readonly struct Job
        {
            public readonly byte[] Payload;
            public readonly bool IsBatch;
            /// <summary>
            /// Stamped when the packet is SUBMITTED, not when it is decoded. Taking it at decode
            /// time would let a packet that arrived on the old connection read the post-flush
            /// value if the two raced, and spawn a dead session's player into the new one.
            /// </summary>
            public readonly int Generation;

            public Job(byte[] payload, bool isBatch, int generation)
            {
                Payload = payload;
                IsBatch = isBatch;
                Generation = generation;
            }
        }

        [NoAutoStaticsCleanup] private static readonly BlockingCollection<Job> sJobs = new BlockingCollection<Job>();
        [NoAutoStaticsCleanup] private static readonly object sStartLock = new object();
        private static Thread sThread;
        [NoAutoStaticsCleanup] private static CancellationTokenSource sStop;
        private static volatile bool sShutdown;

        /// <summary>
        /// Generation counter bumped by <see cref="Flush"/>. Records prepared before a teardown
        /// carry the generation they were decoded under and are dropped rather than spawned, so a
        /// disconnect mid-decode cannot spawn players into the next session.
        /// </summary>
        private static int sGeneration;

        /// <summary>Packets decoded but not yet handed to the main thread. Diagnostics only.</summary>
        public static int PendingPackets => sJobs.Count;

        /// <summary>
        /// True while <paramref name="prepared"/> still belongs to the live connection. A record
        /// that fails this was decoded for a session that has since torn down.
        /// </summary>
        public static bool IsCurrent(BasisPreparedJoin prepared)
        {
            return prepared != null && prepared.Generation == Volatile.Read(ref sGeneration);
        }

        /// <summary>
        /// Touches the main-thread-only statics the load thread will go on to use, so the thread
        /// never triggers their initialization itself. <see cref="BasisPlayerSettingsManager"/>
        /// reads <c>Application.persistentDataPath</c> in its static constructor.
        /// </summary>
        public static void Initialize()
        {
            BasisPlayerSettingsManager.EnsureInitialized();
            sShutdown = false;
            EnsureRunning();
        }

        /// <summary>
        /// A single spawn packet (<c>CreateRemotePlayerChannel</c>). <paramref name="payload"/> must
        /// already be a private copy — the caller's reader is recycled the moment this returns.
        /// </summary>
        public static void SubmitSpawn(byte[] payload)
        {
            Submit(payload, isBatch: false);
        }

        /// <summary>
        /// The join-fill batch (<c>CreateRemotePlayersForNewPeerChannel</c>): a compressed run of
        /// spawn records for every player already present.
        /// </summary>
        public static void SubmitSpawnBatch(byte[] payload)
        {
            Submit(payload, isBatch: true);
        }

        /// <summary>
        /// Drops every packet queued but not yet decoded, and invalidates records already decoded
        /// but not yet spawned. Called when the connection tears down — the thread itself keeps
        /// running so a reconnect has no start-up race to lose.
        /// </summary>
        public static void Flush()
        {
            Interlocked.Increment(ref sGeneration);
            while (sJobs.TryTake(out _))
            {
            }
        }

        private static void Submit(byte[] payload, bool isBatch)
        {
            if (payload == null || payload.Length == 0)
            {
                return;
            }
            EnsureRunning();
            sJobs.Add(new Job(payload, isBatch, Volatile.Read(ref sGeneration)));
        }

        private static void EnsureRunning()
        {
            if (Volatile.Read(ref sThread) != null || sShutdown)
            {
                return;
            }
            lock (sStartLock)
            {
                if (sThread != null || sShutdown)
                {
                    return;
                }
                sStop = new CancellationTokenSource();
                Thread thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "Basis Avatar Load",
                };
                Volatile.Write(ref sThread, thread);
                thread.Start(sStop.Token);
                UnityEngine.Application.quitting -= Shutdown;
                UnityEngine.Application.quitting += Shutdown;
            }
        }

        private static void Shutdown()
        {
            UnityEngine.Application.quitting -= Shutdown;
            lock (sStartLock)
            {
                sShutdown = true;
                sStop?.Cancel();
                sStop = null;
                Volatile.Write(ref sThread, null);
            }
        }

        private static void Run(object state)
        {
            try
            {
                foreach (Job job in sJobs.GetConsumingEnumerable((CancellationToken)state))
                {
                    try
                    {
                        if (job.Generation != Volatile.Read(ref sGeneration))
                        {
                            // Submitted before a teardown; whatever it describes is gone.
                            continue;
                        }
                        if (job.IsBatch)
                        {
                            DecodeBatch(job.Payload, job.Generation);
                        }
                        else
                        {
                            DecodeSingle(job.Payload, job.Generation);
                        }
                    }
                    catch (Exception ex)
                    {
                        BasisDebug.LogError($"Dropping corrupt remote-player spawn packet: {ex.Message}", BasisDebug.LogTag.Networking);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static void DecodeSingle(byte[] payload, int generation)
        {
            ServerReadyMessage srm = new ServerReadyMessage();
            srm.Deserialize(new NetDataReader(payload));
            Prepare(srm, generation);
        }

        private static void DecodeBatch(byte[] payload, int generation)
        {
            ServerReadyBatchMessage batch = new ServerReadyBatchMessage();
            batch.Deserialize(new NetDataReader(payload));

            NetDataReader batchReader = new NetDataReader(batch.Payload);
            for (int Index = 0; Index < batch.Count; Index++)
            {
                // One corrupt entry must not cost the whole batch: every player after it in this
                // packet would otherwise never spawn. Stop at the bad record, keep the good ones.
                ServerReadyMessage srm = new ServerReadyMessage();
                try
                {
                    srm.Deserialize(batchReader);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Dropping remote-player spawn batch at entry {Index}/{batch.Count}: {ex.Message}", BasisDebug.LogTag.Networking);
                    break;
                }
                Prepare(srm, generation);
            }
        }

        /// <summary>
        /// Turns one decoded spawn record into everything the main thread would otherwise have had
        /// to derive from it, then queues the Unity half.
        /// </summary>
        private static void Prepare(ServerReadyMessage srm, int generation)
        {
            ushort playerId = srm.playerIdMessage.playerID;

            // Marked the moment the spawn is KNOWN, not when the budgeted queue finally runs it —
            // per-player traffic (voice, avatar data) races ahead of creation and consults this to
            // drop quietly. CreateRemotePlayer's finally clears it.
            BasisNetworkPlayers.JoiningPlayers.TryAdd(playerId, 0);

            ClientMetaDataMessage metaData = srm.localReadyMessage.playerMetaDataMessage;
            BasisPreparedJoin prepared = new BasisPreparedJoin
            {
                Ready = srm,
                PlayerId = playerId,
                SafeDisplayName = BasisRemotePlayer.BuildSafeDisplayName(metaData.playerDisplayName),
                Generation = generation,
            };

            ClientAvatarChangeMessage avatar = srm.localReadyMessage.clientAvatarChangeMessage;
            if (avatar.byteArray != null && avatar.byteArray.Length > 0)
            {
                try
                {
                    prepared.InitialAvatar = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(avatar.byteArray);
                }
                catch (Exception ex)
                {
                    // Matches the message the main-thread path produced for an undecodable blob.
                    prepared.AvatarDecodeError = "Invalid initial avatar data: failed to convert network bytes to loadable bundle";
                    BasisDebug.LogError($"Invalid Initial Data for {playerId}: {ex.Message}", BasisDebug.LogTag.Networking);
                }
            }

            // Warms BasisPlayerSettingsManager's cache for this UUID. Three separate steps of the
            // spawn await it (CreateAvatar, the jiggle collider setup, the nameplate's block
            // state); doing it here turns the first of those from a disc read plus JSON parse
            // resumed on the main thread into a synchronous dictionary hit, and the other two
            // were always cache hits behind it.
            BasisPlayerSettingsManager.Warm(metaData.playerUUID);

            LocalAvatarSyncMessage spawnSync = srm.localReadyMessage.localAvatarSyncMessage;
            if (spawnSync.array == null)
            {
                // The main-thread path used to THROW here, which aborted the spawn after the
                // mouth marker, nameplate and receiver had already been built — orphaning them.
                // The player is spawned poseless instead; the bone job holds them at the fallback
                // avatar's rest pose until their first real pose packet lands a frame or two later.
                BasisDebug.LogError($"Spawn record for {playerId} carried no avatar pose data.", BasisDebug.LogTag.Networking);
            }
            else if (BasisNetworkAvatarDecompressor.TryDecodeSpawnPose(spawnSync, out BasisAvatarBuffer spawnPose))
            {
                prepared.SpawnPose = spawnPose;
            }

            BasisNetworkHandleRemoval.LifecycleQueue.Enqueue(() =>
            {
                BasisRemotePlayerFactory.CreateRemotePlayer(prepared, BasisNetworkManagement.instantiationParameters);
            });
        }
    }
}
