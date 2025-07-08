using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkServer.Security;
using BasisServerHandle;
using LiteNetLib;

public static class BasisNetworkMessageProcessor
{
    private static readonly ConcurrentQueue<(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method, long timestamp)> movementQueue = new();
    private static readonly ConcurrentQueue<(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method, long timestamp)> voiceQueue = new();
    private static readonly ConcurrentQueue<(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method, long timestamp)> fallQueue = new();
    private static readonly ConcurrentQueue<(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method, long timestamp)> generalQueue = new();

    private static readonly ManualResetEventSlim movementAvailable = new(false);
    private static readonly ManualResetEventSlim voiceAvailable = new(false);
    private static readonly ManualResetEventSlim fallAvailable = new(false);
    private static readonly ManualResetEventSlim generalAvailable = new(false);

    private static readonly int WorkerCount = Environment.ProcessorCount;
    private static readonly Thread[] workers;

    private const int MaxQueueSize = 64;

    // Stopwatch-based timing
    private static readonly long MaxMessageAgeTicks = Stopwatch.Frequency * 100 / 1000; // 100ms
    static BasisNetworkMessageProcessor()
    {
        workers = new Thread[WorkerCount];
        for (int Index = 0; Index < WorkerCount; Index++)
        {
            workers[Index] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"BasisNetWorker-{Index}"
            };
            workers[Index].Start();
        }
    }

    public static void Enqueue(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        long timestamp = Stopwatch.GetTimestamp();
        var item = (peer, reader, channel, deliveryMethod, timestamp);

        switch (channel)
        {
            case BasisNetworkCommons.PlayerAvatarChannel:
                if (movementQueue.Count > MaxQueueSize)
                {
                    reader.Recycle();
                    BNL.LogError("Dropping Movement Data Exceeding Max Queue");
                    return;
                }
                movementQueue.Enqueue(item);
                movementAvailable.Set();
                break;

            case BasisNetworkCommons.VoiceChannel:
                if (voiceQueue.Count > MaxQueueSize)
                {
                    reader.Recycle();
                    BNL.LogError("Dropping Voice Data Exceeding Max Queue");
                    return;
                }
                voiceQueue.Enqueue(item);
                voiceAvailable.Set();
                break;

            case BasisNetworkCommons.FallChannel:
                if (fallQueue.Count > MaxQueueSize)
                {
                    reader.Recycle();
                    BNL.LogError("Dropping Fall Data Exceeding Max Queue");
                    return;
                }
                fallQueue.Enqueue(item);
                fallAvailable.Set();
                break;

            default:
                generalQueue.Enqueue(item);
                generalAvailable.Set();
                break;
        }
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            bool didWork = false;

            didWork |= TryDequeueAndProcess(generalQueue, generalAvailable, null);
            didWork |= TryDequeueAndProcess(voiceQueue, voiceAvailable, BasisNetworkCommons.VoiceChannel);
            didWork |= TryDequeueAndProcess(movementQueue, movementAvailable, BasisNetworkCommons.PlayerAvatarChannel);
            didWork |= TryDequeueAndProcess(fallQueue, fallAvailable, BasisNetworkCommons.FallChannel);

            if (!didWork)
            {
                WaitHandle.WaitAny(new[]
                {
                    movementAvailable.WaitHandle,
                    voiceAvailable.WaitHandle,
                    fallAvailable.WaitHandle,
                    generalAvailable.WaitHandle
                });

                movementAvailable.Reset();
                voiceAvailable.Reset();
                fallAvailable.Reset();
                generalAvailable.Reset();
            }
        }
    }

    private static bool TryDequeueAndProcess(
        ConcurrentQueue<(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method, long timestamp)> queue,
        ManualResetEventSlim availableEvent,
        byte? dropIfOldChannel)
    {
        bool didWork = false;

        while (queue.TryDequeue(out var item))
        {
            didWork = true;

            try
            {
                long ageTicks = Stopwatch.GetTimestamp() - item.timestamp;
                if (dropIfOldChannel.HasValue && ageTicks > MaxMessageAgeTicks)
                {
                    item.reader?.Recycle();
                    BNL.LogError($"Dropped stale message on channel {dropIfOldChannel.Value} (older than {MaxMessageAgeTicks} was {ageTicks})");
                    continue;
                }

                ProcessMessage(item.peer, item.reader, item.channel, item.method);
            }
            catch (Exception ex)
            {
                BNL.LogError($"[Error] Exception in message processing:\n{ex}");
                item.reader?.Recycle();
            }
        }

        return didWork;
    }

    private static void ProcessMessage(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            switch (channel)
            {
                case BasisNetworkCommons.FallChannel:
                    if (deliveryMethod == DeliveryMethod.Unreliable)
                    {
                        if (reader.TryGetByte(out byte Byte))
                        {
                            ProcessMessage(peer, reader, Byte, deliveryMethod);
                        }
                        else
                        {
                            BNL.LogError($"Unknown channel no data remains: {channel} " + reader.AvailableBytes);
                            reader.Recycle();
                        }
                    }
                    else
                    {
                        BNL.LogError($"Unknown channel: {channel} " + reader.AvailableBytes);
                        reader.Recycle();
                    }
                    break;

                case BasisNetworkCommons.AuthIdentityMessage:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisServerHandleEvents.HandleAuth(reader, peer);
                    break;

                case BasisNetworkCommons.PlayerAvatarChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisServerHandleEvents.HandleAvatarMovement(reader, peer);
                    break;

                case BasisNetworkCommons.VoiceChannel:
                    BasisServerHandleEvents.HandleVoiceMessage(reader, peer);
                    break;

                case BasisNetworkCommons.AvatarChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisNetworkingGeneric.HandleAvatar(reader, deliveryMethod, peer);
                    break;

                case BasisNetworkCommons.SceneChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisNetworkingGeneric.HandleScene(reader, deliveryMethod, peer);
                    break;

                case BasisNetworkCommons.AvatarChangeMessage:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisServerHandleEvents.SendAvatarMessageToClients(reader, peer);
                    break;

                case BasisNetworkCommons.ChangeCurrentOwnerRequest:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisNetworkOwnership.OwnershipTransfer(reader, peer);
                    break;

                case BasisNetworkCommons.GetCurrentOwnerRequest:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisNetworkOwnership.OwnershipResponse(reader, peer);
                    break;

                case BasisNetworkCommons.RemoveCurrentOwnerRequest:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisNetworkOwnership.RemoveOwnership(reader, peer);
                    break;

                case BasisNetworkCommons.AudioRecipients:
                    BasisServerHandleEvents.UpdateVoiceReceivers(reader, peer);
                    break;

                case BasisNetworkCommons.netIDAssign:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisServerHandleEvents.netIDAssign(reader, peer);
                    break;

                case BasisNetworkCommons.LoadResourceMessage:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                    {
                        if (NetworkServer.authIdentity.NetIDToUUID(peer, out string UUID))
                        {
                            if (NetworkServer.authIdentity.IsNetPeerAdmin(UUID))
                            {
                                BasisServerHandleEvents.LoadResource(reader, peer);
                            }
                            else
                            {
                                BNL.LogError("Admin was not found! for " + UUID);
                            }
                        }
                        else
                        {
                            BNL.LogError("User " + UUID + " does not exist!");
                        }
                    }
                    break;

                case BasisNetworkCommons.UnloadResourceMessage:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                    {
                        if (NetworkServer.authIdentity.NetIDToUUID(peer, out string UUID))
                        {
                            if (NetworkServer.authIdentity.IsNetPeerAdmin(UUID))
                            {
                                BasisServerHandleEvents.UnloadResource(reader, peer);
                            }
                            else
                            {
                                BNL.LogError("Admin was not found! for " + UUID);
                            }
                        }
                        else
                        {
                            BNL.LogError("User " + UUID + " does not exist!");
                        }
                    }
                    break;

                case BasisNetworkCommons.AdminMessage:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisPlayerModeration.OnAdminMessage(peer, reader);
                    reader.Recycle();
                    break;

                case BasisNetworkCommons.AvatarCloneRequestMessage:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                    {
                        // BasisAvatarRequestMessages.AvatarCloneRequestMessage();
                    }
                    reader.Recycle();
                    break;

                case BasisNetworkCommons.AvatarCloneResponseMessage:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                    {
                        // BasisAvatarRequestMessages.AvatarCloneResponseMessage();
                    }
                    reader.Recycle();
                    break;
                case BasisNetworkCommons.ServerBoundMessage:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                    {
                        BasisServerHandleEvents.OnServerReceived?.Invoke(peer, reader, deliveryMethod);
                    }
                    reader.Recycle();
                    break;
                default:
                    BNL.LogError($"Unknown channel: {channel} " + reader.AvailableBytes);
                    reader.Recycle();
                    break;
            }
        }
        catch (Exception ex)
        {
            BNL.LogError($"[Error] Exception occurred in ProcessMessage.\n" +
                         $"Peer: {peer.Address}, Channel: {channel}, DeliveryMethod: {deliveryMethod}\n" +
                         $"Message: {ex.Message}\n" +
                         $"StackTrace: {ex.StackTrace}\n" +
                         $"InnerException: {ex.InnerException}");
            reader?.Recycle();
        }
    }
}
