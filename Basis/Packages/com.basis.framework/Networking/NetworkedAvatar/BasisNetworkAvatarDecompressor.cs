using Basis.Network.Core.Compression;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Receivers;
using System;
using Unity.Mathematics;
using static SerializableBasis;
namespace Basis.Scripts.Networking.NetworkedAvatar
{
    public static class BasisNetworkAvatarDecompressor
    {
        public static void DecompressAndProcessAvatar(BasisNetworkReceiver baseReceiver, ServerSideSyncPlayerMessage syncMessage)
        {
            if (syncMessage.avatarSerialization.array == null)
            {
                throw new ArgumentException("Cannot serialize avatar data.");
            }

            byte[] data = syncMessage.avatarSerialization.array;
            int length = data.Length;

            BasisAvatarBitPacking.BitQuality q = (BasisAvatarBitPacking.BitQuality)syncMessage.avatarSerialization.DataQualityLevel;
            if (!BasisAvatarBitPacking.IsValidQuality(q))
            {
                BasisDebug.LogError($"Invalid avatar quality level {syncMessage.avatarSerialization.DataQualityLevel}", BasisDebug.LogTag.Networking);
                return;
            }
            int expected = BasisAvatarBitPacking.ConvertToSize(q);

            if (length >= expected)
            {
                int offset = 0;

                // Rate selection per packet:
                //  - If we have an active P2P session with this sender, the announced cache
                //    is authoritative (they're sending us packets at that fast cadence).
                //  - Otherwise this packet came via the server, where per-receiver pacing is
                //    encoded in the server-patched interval byte. The announce cache may have
                //    been broadcast from a P2P sender we're NOT P2P-connected to, so trusting
                //    it would interpolate at the wrong cadence.
                double effectiveIntervalSec;
                ushort senderId = syncMessage.playerIdMessage.playerID;
                bool senderHasLocalP2P = Basis.Scripts.Networking.BasisP2PManager.GetSessionState(senderId)
                    == Basis.Scripts.Networking.BasisP2PManager.P2PSessionState.Connected;
                if (senderHasLocalP2P && Basis.Scripts.Networking.BasisAvatarRateRegistry.TryGetRate(senderId, out int cachedMs))
                {
                    effectiveIntervalSec = cachedMs / 1000.0;
                }
                else
                {
                    int serverDefaultMs = (int)BasisNetworkManagement.ServerMetaDataMessage.SyncInterval;
                    effectiveIntervalSec = Basis.Network.Core.BasisNetworkCommons.DecodeAvatarIntervalMs(syncMessage.interval, serverDefaultMs) / 1000.0;
                }

                if (TryCreateAvatarBuffer(data, ref offset, effectiveIntervalSec, q, out BasisAvatarBuffer avatarBuffer))
                {
                    avatarBuffer.Sequence = syncMessage.sequence;
                    EnqueueAndProcessAdditionalData(baseReceiver, avatarBuffer, syncMessage.avatarSerialization);
                }
            }
            else
            {
                BasisDebug.LogError("Data did not have enough for AvatarsyncMessage", BasisDebug.LogTag.Networking);
            }
        }

        public static void DecompressAndProcessAvatar(BasisNetworkReceiver baseReceiver, LocalAvatarSyncMessage avatarSerialization)
        {
            if (avatarSerialization.array == null)
            {
                throw new ArgumentException("Cannot serialize initial avatar data.");
            }

            byte[] data = avatarSerialization.array;
            int length = data.Length;

            BasisAvatarBitPacking.BitQuality q = (BasisAvatarBitPacking.BitQuality)avatarSerialization.DataQualityLevel;
            if (!BasisAvatarBitPacking.IsValidQuality(q))
            {
                BasisDebug.LogError($"Invalid avatar quality level {avatarSerialization.DataQualityLevel}", BasisDebug.LogTag.Networking);
                return;
            }
            int expected = BasisAvatarBitPacking.ConvertToSize(q);

            if (length >= expected)
            {
                int offset = 0;
                if (TryCreateAvatarBuffer(data, ref offset, 0.01f, q, out BasisAvatarBuffer avatarBuffer))
                {
                    EnqueueAndProcessAdditionalData(baseReceiver, avatarBuffer, avatarSerialization);
                }
            }
            else
            {
                BasisDebug.LogError("Data did not have enough for AvatarsyncMessage", BasisDebug.LogTag.Networking);
            }
        }

        private static bool TryCreateAvatarBuffer(byte[] data, ref int offset, double secondsInterval, BasisAvatarBitPacking.BitQuality quality, out BasisAvatarBuffer basisAvatarBuffer)
        {
            basisAvatarBuffer = null;
            int startOffset = offset;

            if (!math.isfinite(secondsInterval))
            {
                goto Fail;
            }

            secondsInterval = math.clamp(secondsInterval, 1e-3, 1.0);

            basisAvatarBuffer = BasisAvatarBufferPool.Get();

            // Position: High = 3 × float32, lower tiers = 3 × int24 millimetres (server-repacked)
            if (quality == BasisAvatarBitPacking.BitQuality.High)
            {
                if (!BasisUnityBitPackerExtensionsUnsafe.TryReadPosition(ref data, ref offset, out basisAvatarBuffer.Position))
                {
                    goto Fail;
                }
            }
            else
            {
                if (data.Length - offset < BasisAvatarBitPacking.WritePositionQuantized)
                {
                    goto Fail;
                }
                basisAvatarBuffer.Position = new Unity.Mathematics.float3(
                    BasisAvatarBitPacking.DecodeAxisMm(data, offset),
                    BasisAvatarBitPacking.DecodeAxisMm(data, offset + 3),
                    BasisAvatarBitPacking.DecodeAxisMm(data, offset + 6));
                offset += BasisAvatarBitPacking.WritePositionQuantized;
            }

            // Bone rotations (replaces muscle decompression)
            BasisBoneRotationUtils.DecompressBoneRotations(data, quality, ref basisAvatarBuffer.BoneRotations, ref offset);

            // Scale
            if (!BasisUnityBitPackerExtensionsUnsafe.TryReadUShort(ref data, ref offset, out ushort uScale))
            {
                goto Fail;
            }

            // Body rotation
            if (!BasisUnityBitPackerExtensionsUnsafe.TryReadCompressedQuaternionFromBytes(ref data, ref offset, out basisAvatarBuffer.Rotation))
            {
                goto Fail;
            }

            // Hips local-position delta (vs TPose). Paired with the root pose so
            // remotes can reproduce seated/IK-driven hips offsets from the local rig.
            // The wire helper returns the namespace-local float3 POD struct (so server
            // code can use it without Unity.Mathematics) — convert at this boundary.
            if (!BasisUnityBitPackerExtensionsUnsafe.TryReadHipsDelta(ref data, ref offset, out var rawHipsDelta))
            {
                goto Fail;
            }
            basisAvatarBuffer.HipsLocalDelta = new Unity.Mathematics.float3(rawHipsDelta.x, rawHipsDelta.y, rawHipsDelta.z);

            // Hips local-rotation delta (vs TPose). Required because Hips is
            // excluded from the bone packet — without this slot the remote
            // hips bone would never rotate independently of the root.
            if (!BasisUnityBitPackerExtensionsUnsafe.TryReadCompressedQuaternionFromBytes(ref data, ref offset, out basisAvatarBuffer.HipsLocalRotation))
            {
                goto Fail;
            }

            // End-effector anchoring block (High only; server-repacked lower qualities omit it).
            // Length was validated >= ConvertToSize(High) by the caller, so this fixed read is in bounds.
            if (quality == BasisAvatarBitPacking.BitQuality.High)
            {
                basisAvatarBuffer.EffectorMask = BasisAvatarEndEffectors.Decode(
                    data, offset, basisAvatarBuffer.EffectorPos, basisAvatarBuffer.EffectorRot);
                offset += BasisAvatarEndEffectors.BlockBytes;
            }
            else
            {
                basisAvatarBuffer.EffectorMask = 0;
            }

            basisAvatarBuffer.Scale = BasisUnityBitPackerExtensionsUnsafe.DecompressScale(uScale);
            basisAvatarBuffer.SecondsInterval = secondsInterval;
            return true;

        Fail:
            offset = startOffset;
            if (basisAvatarBuffer != null)
            {
                BasisAvatarBufferPool.Release(basisAvatarBuffer);
                basisAvatarBuffer = null;
            }
            BasisDebug.LogError($"non finite data found in Decompression Stage, bailing.", BasisDebug.LogTag.Remote);
            return false;
        }

        private static void EnqueueAndProcessAdditionalData(BasisNetworkReceiver baseReceiver, BasisAvatarBuffer avatarBuffer, LocalAvatarSyncMessage message)
        {
            baseReceiver.EnQueueAvatarBuffer(avatarBuffer);

            if (message.AdditionalAvatarDataSize > 0 && message.AdditionalAvatarDatas != null)
            {
                System.Threading.Interlocked.Increment(ref BasisAdditionalDataDiagnostics.ReceiverFramesWithAdditional);
                BasisAdditionalDataDebugCapture.RecordReceiverFrame(baseReceiver.playerId);

                bool isDifferentAvatar = message.LinkedAvatarIndex != baseReceiver.LastLinkedAvatarIndex;
                if (isDifferentAvatar)
                {
                    System.Threading.Interlocked.Increment(ref BasisAdditionalDataDiagnostics.ReceiverDroppedLinkedIndex);
                    BasisAdditionalDataDebugCapture.RecordReceiverGateDrop(baseReceiver.playerId);
                    BasisAdditionalDataDiagnostics.LastGateMessageIndex = message.LinkedAvatarIndex;
                    BasisAdditionalDataDiagnostics.LastGateReceiverIndex = baseReceiver.LastLinkedAvatarIndex;
                    return;
                }

                // Avatar behaviours (HVR face tracking et al.) touch Unity objects and expect the
                // main thread. Server-relayed frames arrive via the polled connection (main
                // thread) and dispatch inline; frames from the P2P socket thread (UnsyncedEvents)
                // must be marshaled — the pose above is fine because EnQueueAvatarBuffer is a
                // thread-safe queue, but this dispatch is a direct call into gameplay code.
                if (BasisNetworkManagement.IsMainThread())
                {
                    DispatchAdditionalData(baseReceiver, message.AdditionalAvatarDatas, message.AdditionalAvatarDataSize);
                }
                else
                {
                    // The message's AdditionalAvatarDatas array AND each entry's payload byte[]
                    // are pool-reused and overwritten by the next deserialize on this thread, so
                    // the snapshot has to own its payload bytes to stay stable across the hand-off.
                    System.Threading.Interlocked.Increment(ref BasisAdditionalDataDiagnostics.ReceiverMarshaledToMainThread);
                    int size = message.AdditionalAvatarDataSize;
                    var snapshot = new AdditionalAvatarData[size];
                    for (int Index = 0; Index < size; Index++)
                    {
                        AdditionalAvatarData entry = message.AdditionalAvatarDatas[Index];
                        if (entry.array != null)
                        {
                            entry.array = (byte[])entry.array.Clone();
                        }
                        snapshot[Index] = entry;
                    }
                    byte linkedIndex = message.LinkedAvatarIndex;
                    Basis.Scripts.Device_Management.BasisDeviceManagement.EnqueueOnMainThread(() =>
                    {
                        // Re-check on the main thread — an avatar swap may have landed in between.
                        if (linkedIndex != baseReceiver.LastLinkedAvatarIndex)
                        {
                            System.Threading.Interlocked.Increment(ref BasisAdditionalDataDiagnostics.ReceiverDroppedStaleOnDrain);
                            return;
                        }
                        DispatchAdditionalData(baseReceiver, snapshot, size);
                    });
                }
            }
        }

        private static void DispatchAdditionalData(BasisNetworkReceiver baseReceiver, AdditionalAvatarData[] additional, int size)
        {
            var behaviours = baseReceiver.NetworkBehaviours;
            int count = baseReceiver.NetworkBehaviourCount;
            if (behaviours == null)
            {
                System.Threading.Interlocked.Increment(ref BasisAdditionalDataDiagnostics.ReceiverDroppedNoBehaviours);
                return;
            }

            for (int Index = 0; Index < size; Index++)
            {
                AdditionalAvatarData data = additional[Index];
                // Size-0 entries (null/oversized payloads suppressed by the sender) carry no
                // data — behaviours expect a non-empty buffer, so skip instead of dispatching null.
                if (data.array == null || data.array.Length == 0)
                {
                    System.Threading.Interlocked.Increment(ref BasisAdditionalDataDiagnostics.ReceiverEntriesSkippedEmpty);
                    BasisAdditionalDataDebugCapture.RecordReceiverSkippedEmpty(baseReceiver.playerId);
                    continue;
                }
                if (data.messageIndex < count && data.messageIndex < behaviours.Length)
                {
                    behaviours[data.messageIndex].OnNetworkMessageServerReductionSystem(data.array);
                    System.Threading.Interlocked.Increment(ref BasisAdditionalDataDiagnostics.ReceiverEntriesDispatched);
                    BasisAdditionalDataDebugCapture.RecordReceived(baseReceiver.playerId, data.messageIndex, data.array);
                }
                else
                {
                    System.Threading.Interlocked.Increment(ref BasisAdditionalDataDiagnostics.ReceiverEntriesSkippedIndex);
                    BasisAdditionalDataDebugCapture.RecordReceiverSkippedIndex(baseReceiver.playerId);
                }
            }
        }

    }
}
