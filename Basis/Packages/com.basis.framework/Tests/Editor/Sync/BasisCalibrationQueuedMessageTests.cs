using System;
using System.Collections.Generic;
using Basis.Network.Core;
using Basis.Scripts.Networking.Behaviour;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static SerializableBasis;

/// <summary>
/// Replay of avatar-channel (ch15) messages that were deferred for a not-yet-loaded avatar and
/// drained by <see cref="BasisNetworkReceiver.OnCalibration"/>. The queued messageIndex was
/// validated against the PREVIOUS avatar's behaviour array, so the avatar that actually lands
/// can be shorter — most often the fallback from a failed download, which has none at all.
/// Throwing out of the drain aborts RemoteCalibration and the factory destroys the avatar,
/// so a stale index or a misbehaving content behaviour must never escape.
/// </summary>
public class BasisCalibrationQueuedMessageTests
{
    private class RecordingAvatarBehaviour : BasisNetworkAvatarBehaviour
    {
        public readonly List<byte[]> Received = new List<byte[]>();
        public readonly List<byte[]> ReceivedDirect = new List<byte[]>();
        public bool ThrowOnReceive;

        public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
            if (ThrowOnReceive) throw new InvalidOperationException("content behaviour threw");
            Received.Add(buffer);
        }

        public override void OnDirectNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
            if (ThrowOnReceive) throw new InvalidOperationException("content behaviour threw");
            ReceivedDirect.Add(buffer);
        }
    }

    private const byte CurrentLink = 5;
    private static readonly byte[] Payload = { 9, 8, 7 };

    private GameObject _go;
    private RecordingAvatarBehaviour _behaviour;
    private BasisNetworkReceiver _receiver;

    [SetUp]
    public void SetUp()
    {
        _go = new GameObject("CalibrationQueuedMessageTest");
        _behaviour = _go.AddComponent<RecordingAvatarBehaviour>();
        _receiver = new BasisNetworkReceiver(1)
        {
            NetworkBehaviours = new BasisNetworkAvatarBehaviour[] { _behaviour, _behaviour },
            NetworkBehaviourCount = 2,
            LastLinkedAvatarIndex = CurrentLink,
        };
    }

    [TearDown]
    public void TearDown()
    {
        LogAssert.ignoreFailingMessages = false;
        if (_go != null) UnityEngine.Object.DestroyImmediate(_go);
    }

    private void Queue(byte messageIndex, byte avatarLinkIndex, bool direct = false)
    {
        _receiver.NextMessages[messageIndex] = new BasisNetworkPlayer.ServerAvatarDataMessageQueue
        {
            Method = DeliveryMethod.ReliableOrdered,
            Direct = direct,
            ServerAvatarDataMessage = new ServerAvatarDataMessage
            {
                playerIdMessage = new PlayerIdMessage { playerID = 7 },
                avatarDataMessage = new RemoteAvatarDataMessage
                {
                    messageIndex = messageIndex,
                    AvatarLinkIndex = avatarLinkIndex,
                    payload = Payload,
                    PlayerIdMessage = new PlayerIdMessage { playerID = 7 },
                },
            },
        };
    }

    [Test]
    public void QueuedIndex_BeyondTheAvatarThatLanded_IsDroppedNotThrown()
    {
        _receiver.NetworkBehaviours = new BasisNetworkAvatarBehaviour[] { _behaviour };
        _receiver.NetworkBehaviourCount = 1;
        Queue(3, CurrentLink);

        Assert.DoesNotThrow(() => _receiver.OnCalibration(),
            "a queued index the newly loaded avatar has no behaviour for must not abort calibration");
        Assert.AreEqual(0, _behaviour.Received.Count);
        Assert.AreEqual(0, _receiver.NextMessages.Count, "the undeliverable entry must not stay queued forever");
    }

    [Test]
    public void FallbackAvatarWithNoBehaviours_DrainsCleanly()
    {
        _receiver.NetworkBehaviours = Array.Empty<BasisNetworkAvatarBehaviour>();
        _receiver.NetworkBehaviourCount = 0;
        Queue(0, CurrentLink);

        Assert.DoesNotThrow(() => _receiver.OnCalibration(),
            "the LoadAvatarAfterError fallback carries no behaviours at all");
        Assert.AreEqual(0, _receiver.NextMessages.Count);
    }

    [Test]
    public void NullBehaviours_DrainsCleanly()
    {
        _receiver.NetworkBehaviours = null;
        _receiver.NetworkBehaviourCount = 0;
        Queue(0, CurrentLink);

        Assert.DoesNotThrow(() => _receiver.OnCalibration(),
            "AvatarLoadComplete can still be sitting on the main-thread queue when calibration fires");
        Assert.AreEqual(0, _receiver.NextMessages.Count);
    }

    [Test]
    public void DestroyedBehaviourSlot_IsSkipped()
    {
        UnityEngine.Object.DestroyImmediate(_behaviour);
        Queue(0, CurrentLink);

        Assert.DoesNotThrow(() => _receiver.OnCalibration());
        Assert.AreEqual(0, _receiver.NextMessages.Count);
    }

    [Test]
    public void InRangeQueuedMessage_IsDispatchedAndDequeued()
    {
        Queue(1, CurrentLink);

        _receiver.OnCalibration();

        Assert.AreEqual(1, _behaviour.Received.Count, "a message for a slot the new avatar has must still replay");
        CollectionAssert.AreEqual(Payload, _behaviour.Received[0]);
        Assert.AreEqual(0, _receiver.NextMessages.Count);
    }

    [Test]
    public void DirectQueuedMessage_TakesTheDirectCallback()
    {
        Queue(0, CurrentLink, direct: true);

        _receiver.OnCalibration();

        Assert.AreEqual(1, _behaviour.ReceivedDirect.Count);
        Assert.AreEqual(0, _behaviour.Received.Count);
    }

    [Test]
    public void ThrowingBehaviour_DoesNotAbortTheRestOfTheQueue()
    {
        var thrower = _go.AddComponent<RecordingAvatarBehaviour>();
        thrower.ThrowOnReceive = true;
        _receiver.NetworkBehaviours = new BasisNetworkAvatarBehaviour[] { thrower, _behaviour };
        _receiver.NetworkBehaviourCount = 2;
        Queue(0, CurrentLink);
        Queue(1, CurrentLink);

        LogAssert.ignoreFailingMessages = true;
        Assert.DoesNotThrow(() => _receiver.OnCalibration(),
            "content-authored behaviour code must not take the avatar down with it");

        Assert.AreEqual(1, _behaviour.Received.Count, "the surviving behaviour must still get its replay");
        Assert.AreEqual(0, _receiver.NextMessages.Count);
    }

    [Test]
    public void StaleAvatarMessage_IsDiscardedWithoutDispatch()
    {
        Queue(0, (byte)(CurrentLink - 1));

        _receiver.OnCalibration();

        Assert.AreEqual(0, _behaviour.Received.Count);
        Assert.AreEqual(0, _receiver.NextMessages.Count);
    }

    [Test]
    public void FutureAvatarMessage_StaysQueued()
    {
        Queue(0, (byte)(CurrentLink + 1));

        _receiver.OnCalibration();

        Assert.AreEqual(0, _behaviour.Received.Count);
        Assert.AreEqual(1, _receiver.NextMessages.Count, "a message for an avatar still loading must survive this drain");
    }
}
