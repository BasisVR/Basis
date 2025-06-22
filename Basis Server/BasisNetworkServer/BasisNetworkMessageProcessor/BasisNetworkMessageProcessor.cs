using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkServer.Security;
using BasisServerHandle;
using LiteNetLib;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace BasisNetworkServer.BasisNetworkMessageProcessor
{
    public static class BasisNetworkMessageProcessor
    {
        private static readonly ConcurrentQueue<(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method, DateTime timestamp)> messageQueue = new();
        private static readonly ManualResetEventSlim messageAvailable = new(false);
        private static readonly int WorkerCount = Environment.ProcessorCount;
        private static readonly Thread[] workers;

        // Threshold to start dropping movement/voice messages
        private const int MaxQueueSize = 1000;
        private static readonly TimeSpan MaxMessageAge = TimeSpan.FromSeconds(1);
        static BasisNetworkMessageProcessor()
        {
            workers = new Thread[WorkerCount];
            for (int Index = 0; Index < WorkerCount; Index++)
            {
                workers[Index] = new Thread(ProcessMessages)
                {
                    IsBackground = true,
                    Name = $"Basis NetWorker-{Index}"
                };
                workers[Index].Start();
            }
        }

        public static void Enqueue(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            // Skip enqueuing movement and voice messages if queue is too full
            if (messageQueue.Count > MaxQueueSize)
            {
                if (channel == BasisNetworkCommons.MovementChannel || channel == BasisNetworkCommons.VoiceChannel || channel == BasisNetworkCommons.FallChannel)
                {
                    reader.Recycle(); // Important: recycle to avoid memory leak
                    BNL.LogError("Dropping Movement and Voice Data Exceeding Max Queue");
                    return;
                }
            }

            var timestamp = DateTime.UtcNow;
            messageQueue.Enqueue((peer, reader, channel, deliveryMethod, timestamp));
            messageAvailable.Set();
        }

        private static void ProcessMessages()
        {
            while (true)
            {
                while (messageQueue.TryDequeue(out var item))
                {
                    try
                    {
                        // Drop old messages for specific channels
                        if ((item.channel == BasisNetworkCommons.MovementChannel ||
                             item.channel == BasisNetworkCommons.VoiceChannel ||
                             item.channel == BasisNetworkCommons.FallChannel) &&
                            DateTime.UtcNow - item.timestamp > MaxMessageAge)
                        {
                            item.reader?.Recycle();
                            BNL.LogError("Dropped stale movement/voice/fall message (older than 1s)");
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

                messageAvailable.Wait();
                messageAvailable.Reset();
            }
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

                    case BasisNetworkCommons.MovementChannel:
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
}
