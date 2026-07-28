using System;
using System.Collections.Generic;
using System.Threading;
using Basis.Network.Core.Compression;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Behaviour;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using NUnit.Framework;
using UnityEngine;
using static SerializableBasis;

/// <summary>
/// Receive-side dispatch of AdditionalAvatarData (face tracking / behaviour params) through the
/// REAL <see cref="BasisNetworkAvatarDecompressor"/> path: main-thread frames dispatch inline;
/// frames arriving on a network socket thread are snapshotted and marshaled through
/// <see cref="Basis.Scripts.Device_Management.BasisDeviceManagement.mainThreadActions"/>;
/// the LinkedAvatarIndex gate and entry edge cases behave exactly as the wire tests assume.
/// </summary>
public class BasisAdditionalDataDispatchTests
{
    private class RecordingAvatarBehaviour : BasisNetworkAvatarBehaviour
    {
        public readonly List<byte[]> Received = new List<byte[]>();
        public readonly List<int> ThreadIds = new List<int>();

        public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {
            Received.Add(buffer);
            ThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
        }
    }

    private static readonly byte[] FaceBytes = { 16, 1, 42, 0, 200, 150, 100 };

    private int _savedMainThreadId;
    private GameObject _go;
    private RecordingAvatarBehaviour _behaviour;
    private BasisNetworkReceiver _receiver;

    [SetUp]
    public void SetUp()
    {
        _savedMainThreadId = BasisNetworkManagement.mainThreadId;
        BasisNetworkManagement.mainThreadId = Thread.CurrentThread.ManagedThreadId;

        DrainMainThreadQueue(); // stale actions from other systems must not interfere

        _go = new GameObject("AdditionalDataDispatchTest");
        _behaviour = _go.AddComponent<RecordingAvatarBehaviour>();
        _receiver = new BasisNetworkReceiver(1)
        {
            NetworkBehaviours = new BasisNetworkAvatarBehaviour[] { _behaviour, _behaviour },
            NetworkBehaviourCount = 2,
            LastLinkedAvatarIndex = 5,
        };
    }

    [TearDown]
    public void TearDown()
    {
        BasisNetworkManagement.mainThreadId = _savedMainThreadId;
        DrainMainThreadQueue();
        if (_go != null) UnityEngine.Object.DestroyImmediate(_go);
    }

    private static void DrainMainThreadQueue()
    {
        while (Basis.Scripts.Device_Management.BasisDeviceManagement.mainThreadActions.TryDequeue(out Action action))
        {
            action();
        }
    }

    /// <summary>A structurally valid High payload the pose decoder accepts (finite everywhere).</summary>
    private static byte[] BuildValidHighPayload()
    {
        var q = BasisAvatarBitPacking.BitQuality.High;
        var payload = new byte[BasisAvatarBitPacking.ConvertToSize(q)];

        int tail = BasisAvatarBitPacking.PositionBytes(q) + BasisBoneRotationCompression.RotationBytes(q);
        // Scale: 0x4000 decodes to 1.0 (same constant the load tester uses).
        payload[tail] = 0x00;
        payload[tail + 1] = 0x40;
        // Hips world rotation + hips local rotation delta: explicit smallest-three identity —
        // all-zero bytes do NOT decode to a valid identity.
        WriteIdentityQuaternion(payload, tail + BasisAvatarBitPacking.WriteScale);
        WriteIdentityQuaternion(payload, tail + BasisAvatarBitPacking.WriteScale + BasisAvatarBitPacking.WriteRotation + BasisAvatarBitPacking.WriteHipsDelta);
        return payload;
    }

    private static void WriteIdentityQuaternion(byte[] dst, int offset)
    {
        dst[offset] = 3;          // drop w
        dst[offset + 1] = 0x00; dst[offset + 2] = 0x80; // mid = 0
        dst[offset + 3] = 0x00; dst[offset + 4] = 0x80;
        dst[offset + 5] = 0x00; dst[offset + 6] = 0x80;
    }

    private static LocalAvatarSyncMessage MakeMessage(byte linkedIndex, params AdditionalAvatarData[] entries)
    {
        return new LocalAvatarSyncMessage
        {
            array = BuildValidHighPayload(),
            DataQualityLevel = (byte)BasisAvatarBitPacking.BitQuality.High,
            AdditionalAvatarDatas = entries,
            AdditionalAvatarDataSize = (byte)entries.Length,
            LinkedAvatarIndex = linkedIndex,
        };
    }

    private static AdditionalAvatarData FaceEntry(byte messageIndex) =>
        new AdditionalAvatarData { messageIndex = messageIndex, array = (byte[])FaceBytes.Clone() };

    private void RunOnBackgroundThread(Action action)
    {
        Exception threadException = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { threadException = ex; }
        });
        thread.Start();
        Assert.IsTrue(thread.Join(5000), "background thread did not finish");
        Assert.IsNull(threadException, $"background thread threw: {threadException}");
    }

    [Test]
    public void MainThread_DispatchesInline()
    {
        BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(_receiver, MakeMessage(5, FaceEntry(1)));

        Assert.AreEqual(1, _behaviour.Received.Count, "main-thread frame must dispatch without deferral");
        CollectionAssert.AreEqual(FaceBytes, _behaviour.Received[0]);
        Assert.AreEqual(Thread.CurrentThread.ManagedThreadId, _behaviour.ThreadIds[0]);
    }

    [Test]
    public void SocketThread_MarshalsToMainThread()
    {
        RunOnBackgroundThread(() =>
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(_receiver, MakeMessage(5, FaceEntry(1))));

        Assert.AreEqual(0, _behaviour.Received.Count, "off-main frame must not dispatch on the socket thread");

        DrainMainThreadQueue();

        Assert.AreEqual(1, _behaviour.Received.Count, "marshaled dispatch must run when the main-thread queue drains");
        CollectionAssert.AreEqual(FaceBytes, _behaviour.Received[0]);
        Assert.AreEqual(BasisNetworkManagement.mainThreadId, _behaviour.ThreadIds[0], "dispatch must run on the main thread");
    }

    [Test]
    public void SocketThread_SnapshotIsImmuneToPooledEntryReuse()
    {
        LocalAvatarSyncMessage message = MakeMessage(5, FaceEntry(1));
        RunOnBackgroundThread(() =>
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(_receiver, message));

        // Simulate the network pool overwriting the entries before the main thread drains —
        // exactly what the pooled ServerSideSyncPlayerMessage / ThreadStatic delta ssm do.
        message.AdditionalAvatarDatas[0] = new AdditionalAvatarData { messageIndex = 9, array = new byte[] { 0xDE, 0xAD } };

        DrainMainThreadQueue();

        Assert.AreEqual(1, _behaviour.Received.Count);
        CollectionAssert.AreEqual(FaceBytes, _behaviour.Received[0], "dispatch must deliver the ORIGINAL frame's bytes");
    }

    [Test]
    public void LinkedIndexMismatch_IsDropped_BothThreads()
    {
        BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(_receiver, MakeMessage(4, FaceEntry(1)));
        Assert.AreEqual(0, _behaviour.Received.Count, "main-thread frame for a different avatar must drop");

        RunOnBackgroundThread(() =>
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(_receiver, MakeMessage(4, FaceEntry(1))));
        DrainMainThreadQueue();
        Assert.AreEqual(0, _behaviour.Received.Count, "socket-thread frame for a different avatar must drop");
    }

    [Test]
    public void AvatarSwapBetweenEnqueueAndDrain_IsDropped()
    {
        RunOnBackgroundThread(() =>
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(_receiver, MakeMessage(5, FaceEntry(1))));

        _receiver.LastLinkedAvatarIndex = 6; // avatar change landed before the queue drained

        DrainMainThreadQueue();
        Assert.AreEqual(0, _behaviour.Received.Count, "stale-avatar face data must not reach the new avatar's behaviours");
    }

    [Test]
    public void ReceiverTearDownBeforeDrain_DoesNotThrow()
    {
        RunOnBackgroundThread(() =>
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(_receiver, MakeMessage(5, FaceEntry(1))));

        _receiver.NetworkBehaviours = null; // player left before the queue drained
        _receiver.NetworkBehaviourCount = 0;

        Assert.DoesNotThrow(DrainMainThreadQueue);
        Assert.AreEqual(0, _behaviour.Received.Count);
    }

    [Test]
    public void EmptyEntries_AreSkipped_OthersStillDispatch()
    {
        var empty = new AdditionalAvatarData { messageIndex = 0, array = null };
        var zeroLen = new AdditionalAvatarData { messageIndex = 0, array = Array.Empty<byte>() };
        BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(_receiver, MakeMessage(5, empty, zeroLen, FaceEntry(1)));

        Assert.AreEqual(1, _behaviour.Received.Count, "only the non-empty entry may dispatch");
        CollectionAssert.AreEqual(FaceBytes, _behaviour.Received[0]);
    }

    [Test]
    public void OutOfRangeMessageIndex_IsIgnored()
    {
        BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(_receiver, MakeMessage(5, FaceEntry(7)));
        Assert.AreEqual(0, _behaviour.Received.Count, "an index beyond NetworkBehaviourCount must be ignored");
    }
}
