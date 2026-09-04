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
    /// <summary>Next Stopwatch timestamp an unknown-player drop may log (written under the semaphore).</summary>
    private static long nextUnknownPlayerLogTimestamp;

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
                else if (!BasisNetworkPlayers.JoiningPlayers.ContainsKey(audioUpdate.playerIdMessage.playerID))
                {
                    // Voice outruns the budgeted player creation for every joiner — that case
                    // is expected and silent (the receiver appears when creation completes;
                    // realtime audio has nothing worth keeping). An id that is neither known
                    // nor joining is a disconnect straggler or a real desync — say so, but
                    // throttled: this arrives per voice packet.
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now >= nextUnknownPlayerLogTimestamp)
                    {
                        nextUnknownPlayerLogTimestamp = now + System.Diagnostics.Stopwatch.Frequency * 5;
                        BasisDebug.Log($"Voice for unknown player {audioUpdate.playerIdMessage.playerID} (not joining) — dropping.", BasisDebug.LogTag.Voice);
                    }
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

    private static readonly SemaphoreSlim announceSemaphore = new SemaphoreSlim(1, 1);
    public static ConcurrentQueue<ServerAudioSegmentMessage> AnnounceMessage = new ConcurrentQueue<ServerAudioSegmentMessage>();

    /// <summary>
    /// Handles announce voice audio arriving on AnnounceVoiceChannel (channel 4).
    /// Routes to the non-spatialized announce audio receiver on the remote player.
    /// </summary>
    public static async Task HandleAnnounceAudioUpdate(NetPacketReader Reader)
    {
        try
        {
            if (!await announceSemaphore.WaitAsync(TimeoutMilliseconds))
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
                if (AnnounceMessage.TryDequeue(out ServerAudioSegmentMessage audioUpdate) == false)
                {
                    audioUpdate = new ServerAudioSegmentMessage();
                }
                int wireBytes = Reader.AvailableBytes;
                audioUpdate.Deserialize(Reader);
                if (BasisNetworkPlayers.RemotePlayerReceivers.TryGetValue(audioUpdate.playerIdMessage.playerID, out BasisNetworkReceiver announceReceiver))
                {
                    announceReceiver.AccountReceivedVoiceBytes(wireBytes);
                }
                if (audioUpdate.audioSegmentData.LengthUsed == 0)
                {
                    BasisDebug.LogError("Announce Audio Segment Data Length was zero", BasisDebug.LogTag.Voice);
                }
                else
                {
                    BasisAnnounceAudioDriver.ReceiveAnnounceAudio(audioUpdate.playerIdMessage.playerID, audioUpdate.audioSegmentData);
                }
                AnnounceMessage.Enqueue(audioUpdate);
                while (AnnounceMessage.Count > MaxStoredServerAudioSegmentMessage)
                {
                    AnnounceMessage.TryDequeue(out ServerAudioSegmentMessage seg);
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Error in HandleAnnounceAudioUpdate:{ex.Message}{ex.StackTrace}");
            }
            finally
            {
                announceSemaphore.Release();
                if (Reader.IsNull == false)
                {
                    Reader.Recycle();
                }
            }
        }
        catch (OperationCanceledException)
        {
            BasisDebug.LogError("HandleAnnounceAudioUpdate task canceled.");
        }
    }
}
