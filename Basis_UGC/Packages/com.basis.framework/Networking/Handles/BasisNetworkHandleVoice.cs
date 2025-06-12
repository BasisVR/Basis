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
    private static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    private const int TimeoutMilliseconds = 1000;
    public static ConcurrentQueue<ServerAudioSegmentMessage> Message = new ConcurrentQueue<ServerAudioSegmentMessage>();
    public static async Task HandleAudioUpdate(LiteNetLib.NetPacketReader Reader)
    {
        // Cancel any ongoing task
        cancellationTokenSource.Cancel();
        cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            await semaphore.WaitAsync(TimeoutMilliseconds);

            try
            {
                if (Message.TryDequeue(out ServerAudioSegmentMessage audioUpdate) == false)
                {
                    audioUpdate = new ServerAudioSegmentMessage();
                }
                audioUpdate.Deserialize(Reader);
                if (BasisNetworkManagement.RemotePlayers.TryGetValue(audioUpdate.playerIdMessage.playerID, out BasisNetworkReceiver player))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        BasisDebug.Log("Operation canceled.");
                        return; // Exit early if a cancellation is requested
                    }

                    if (audioUpdate.audioSegmentData.LengthUsed == 0)
                    {
                        player.ReceiveSilentNetworkAudio(audioUpdate);
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
                Message.Enqueue(audioUpdate);
                while (Message.Count > 250)
                {
                    Message.TryDequeue(out ServerAudioSegmentMessage seg);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                BasisDebug.LogError($"Error in HandleAudioUpdate: {ex.Message} {ex.StackTrace}");
            }
            finally
            {
                semaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            BasisDebug.Log("HandleAudioUpdate task canceled.");
        }
    }
}
