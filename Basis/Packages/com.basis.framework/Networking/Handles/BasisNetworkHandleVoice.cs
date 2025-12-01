using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using System.Threading;
using System;
using System.Threading.Tasks;
using UnityEngine;
using System.Collections.Concurrent;
using static SerializableBasis;

/// <summary>
/// Central handler for incoming network voice packets.
/// Manages deserialization, routing to the correct <see cref="BasisNetworkReceiver"/>,
/// and a small pool/queue to reduce allocations under load.
/// </summary>
public static class BasisNetworkHandleVoice
{
    /// <summary>
    /// Concurrency gate ensuring only one audio update is processed at a time.
    /// </summary>
    private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Source used to cancel an in-flight <see cref="HandleAudioUpdate"/> if a new packet arrives.
    /// </summary>
    private static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

    /// <summary>
    /// Maximum time to wait for the semaphore before skipping processing (ms).
    /// </summary>
    private const int TimeoutMilliseconds = 1000;

    /// <summary>
    /// Small pool/queue of reusable <see cref="ServerAudioSegmentMessage"/> instances.
    /// </summary>
    public static ConcurrentQueue<ServerAudioSegmentMessage> Message = new ConcurrentQueue<ServerAudioSegmentMessage>();

    /// <summary>
    /// Upper bound on queued/preserved audio segment messages (older items dropped).
    /// </summary>
    public const int MaxStoredServerAudioSegmentMessage = 250;

    /// <summary>
    /// Reads one audio packet from <paramref name="Reader"/>, routes it to the target player,
    /// and recycles the message object back into the queue.
    /// </summary>
    /// <param name="Reader">Network packet reader positioned at a voice segment.</param>
    /// <remarks>
    /// - Cancels any in-progress processing to prefer the most recent packet.<br/>
    /// - Serializes access via <see cref="semaphore"/>; if not obtained within
    ///   <see cref="TimeoutMilliseconds"/>, processing is effectively skipped.<br/>
    /// - Uses a bounded queue as a lightweight object pool to reduce GC pressure.
    /// </remarks>
    public static async Task HandleAudioUpdate(NetPacketReader Reader)
    {
        // Cancel any ongoing task so we prefer the newest audio data.
        cancellationTokenSource.Cancel();
        cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            await semaphore.WaitAsync(TimeoutMilliseconds);

            try
            {
                // Reuse or create a message container
                if (Message.TryDequeue(out ServerAudioSegmentMessage audioUpdate) == false)
                {
                    audioUpdate = new ServerAudioSegmentMessage();
                }

                // Deserialize packet into message
                audioUpdate.Deserialize(Reader);

                // Route to the correct player if present
                if (BasisNetworkPlayers.RemotePlayers.TryGetValue(audioUpdate.playerIdMessage.playerID, out BasisNetworkReceiver player))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        BasisDebug.LogError("Operation canceled.");
                        return; // Early exit on cancellation
                    }

                    if (audioUpdate.audioSegmentData.LengthUsed == 0)
                    {
                        BasisDebug.LogError("Audio Segment Data Length was zero this is now unsupported", BasisDebug.LogTag.Voice);
                     //   player.ReceiveSilentNetworkAudio(audioUpdate);
                    }
                    else
                    {
                        player.ReceiveNetworkAudio(audioUpdate);
                    }
                }
                else
                {
                    BasisDebug.Log($"Missing Player For Message {audioUpdate.playerIdMessage.playerID}");
                }

                // Recycle the container and bound the pool
                Message.Enqueue(audioUpdate);
                while (Message.Count > MaxStoredServerAudioSegmentMessage)
                {
                    Message.TryDequeue(out ServerAudioSegmentMessage seg);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                BasisDebug.LogError($"Error in HandleAudioUpdate: {ex.Message} {ex.StackTrace}");
                if (Reader.IsNull == false)
                {
                    Reader.Recycle();
                }
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
            if (Reader.IsNull == false)
            {
                Reader.Recycle();
            }
        }
    }
}
