using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using System.Threading;
using System;
using System.Threading.Tasks;
using UnityEngine;
using System.Collections.Concurrent;
using static SerializableBasis;

public static class BasisNetworkHandleVoice
{
    private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
    private const int TimeoutMilliseconds = 1000;
    public static ConcurrentQueue<ServerAudioSegmentMessage> Message = new ConcurrentQueue<ServerAudioSegmentMessage>();
    public const int MaxStoredServerAudioSegmentMessage = 250;

    public static async Task HandleAudioUpdate(NetPacketReader Reader, bool largeId)
    {
        try
        {
            if (!await semaphore.WaitAsync(TimeoutMilliseconds))
            {
                // Timed out before acquiring the lock — drop this packet rather than
                // process it unsynchronized, and don't release a lock we never took.
                if (Reader.IsNull == false)
                {
                    Reader.Recycle();
                }
                return;
            }
            try
            {
                if (Message.TryDequeue(out ServerAudioSegmentMessage audioUpdate) == false)
                {
                    audioUpdate = new ServerAudioSegmentMessage();
                }
                int wireBytes = Reader.AvailableBytes;
                audioUpdate.Deserialize(Reader, largeId);
                if (BasisNetworkPlayers.RemotePlayerReceivers.TryGetValue(audioUpdate.playerIdMessage.playerID, out BasisNetworkReceiver player))
                {
                    player.AccountReceivedVoiceBytes(wireBytes);
                    if (audioUpdate.audioSegmentData.LengthUsed == 0)
                    {
                        BasisDebug.LogError("Audio Segment Data Length was zero this is now unsupported", BasisDebug.LogTag.Voice);
                    }
                    else
                    {
                        player.ReceiveNetworkAudio(audioUpdate);
                    }
                }
                else
                {
                    BasisDebug.Log("Missing Player For Message" + audioUpdate.playerIdMessage.playerID);
                }
                Message.Enqueue(audioUpdate);
                while (Message.Count > MaxStoredServerAudioSegmentMessage)
                {
                    Message.TryDequeue(out ServerAudioSegmentMessage seg);
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Error in HandleAudioUpdate:{ex.Message}{ex.StackTrace}");
            }
            finally
            {
                semaphore.Release();
                if (Reader.IsNull == false)
                {
                    Reader.Recycle();
                }
            }
        }
        catch (OperationCanceledException)
        {
            BasisDebug.LogError("HandleAudioUpdate task canceled.");
        }
    }

    private static readonly SemaphoreSlim shoutSemaphore = new SemaphoreSlim(1, 1);
    public static ConcurrentQueue<ServerAudioSegmentMessage> ShoutMessage = new ConcurrentQueue<ServerAudioSegmentMessage>();

    /// <summary>
    /// Handles shout voice audio arriving on ShoutVoiceChannel (channel 0).
    /// Routes to the non-spatialized shout audio receiver on the remote player.
    /// </summary>
    public static async Task HandleShoutAudioUpdate(NetPacketReader Reader)
    {
        try
        {
            if (!await shoutSemaphore.WaitAsync(TimeoutMilliseconds))
            {
                // Timed out before acquiring the lock — drop this packet rather than
                // process it unsynchronized, and don't release a lock we never took.
                if (Reader.IsNull == false)
                {
                    Reader.Recycle();
                }
                return;
            }
            try
            {
                if (ShoutMessage.TryDequeue(out ServerAudioSegmentMessage audioUpdate) == false)
                {
                    audioUpdate = new ServerAudioSegmentMessage();
                }
                int wireBytes = Reader.AvailableBytes;
                audioUpdate.Deserialize(Reader);
                if (BasisNetworkPlayers.RemotePlayerReceivers.TryGetValue(audioUpdate.playerIdMessage.playerID, out BasisNetworkReceiver shoutReceiver))
                {
                    shoutReceiver.AccountReceivedVoiceBytes(wireBytes);
                }
                if (audioUpdate.audioSegmentData.LengthUsed == 0)
                {
                    BasisDebug.LogError("Shout Audio Segment Data Length was zero", BasisDebug.LogTag.Voice);
                }
                else
                {
                    BasisShoutAudioDriver.ReceiveShoutAudio(audioUpdate.playerIdMessage.playerID, audioUpdate.audioSegmentData);
                }
                ShoutMessage.Enqueue(audioUpdate);
                while (ShoutMessage.Count > MaxStoredServerAudioSegmentMessage)
                {
                    ShoutMessage.TryDequeue(out ServerAudioSegmentMessage seg);
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Error in HandleShoutAudioUpdate:{ex.Message}{ex.StackTrace}");
            }
            finally
            {
                shoutSemaphore.Release();
                if (Reader.IsNull == false)
                {
                    Reader.Recycle();
                }
            }
        }
        catch (OperationCanceledException)
        {
            BasisDebug.LogError("HandleShoutAudioUpdate task canceled.");
        }
    }
}
