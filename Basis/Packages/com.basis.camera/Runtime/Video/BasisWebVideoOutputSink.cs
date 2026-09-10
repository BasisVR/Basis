using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
namespace Basis
{
    public sealed class BasisWebVideoOutputSink
    {
        private const string Boundary = "basisframe", StreamPath = "/stream";
        private const int MaxClients = 4, PortAttempts = 16, IdleWaitMs = 15, HandshakeWaitMs = 5;
        private const int HandshakeTimeoutMs = 2000, WriteStallTimeoutMs = 4000;
        private static readonly byte[] FrameTrailer = Encoding.ASCII.GetBytes("\r\n");
        private sealed class Viewer
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public byte[] Scratch = Array.Empty<byte>();
            public volatile bool Writing, Dead;
            public string Failure;
            public int WriteStartedTick;
        }
        private sealed class Handshake
        {
            public TcpClient Client;
            public int DeadlineTick;
        }
        private TcpListener listener;
        private Thread worker;
        private volatile bool running;
        private volatile int clientCount;
        private readonly ManualResetEventSlim frameSignal = new ManualResetEventSlim(false);
        private readonly List<Viewer> clients = new List<Viewer>();
        private readonly List<Handshake> handshakes = new List<Handshake>();
        private byte[] rawFrame, rawSpare;
        private readonly object rawLock = new object();
        private volatile bool rawReady;
        private int rawWidth, rawHeight, rawQuality;
        private volatile bool encodeOnMainThread;
        private readonly object frameLock = new object();
        private byte[] pendingFrame;
        private Action<AsyncGPUReadbackRequest> readbackCallback;
        private volatile bool readbackInFlight;
        private int frameWidth, frameHeight, frameQuality;
        public string FailureMessage { get; private set; }
        public int Port { get; private set; }
        public string Url => $"http://127.0.0.1:{Port}/";
        public bool HasClients => clientCount > 0;
        public bool CanAcceptFrame => running && clientCount > 0 && !readbackInFlight && !rawReady;
        public bool Start(int port)
        {
            for (int Attempt = 0; Attempt < PortAttempts; Attempt++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, port + Attempt);
                    listener.Start();
                    Port = port + Attempt;
                    break;
                }
                catch (SocketException)
                {
                    listener = null;
                }
            }

            if (listener == null)
            {
                BasisDebug.LogError($"Web stream could not bind any port in {port}-{port + PortAttempts - 1}.", BasisDebug.LogTag.Camera);
                return false;
            }

            running = true;
            readbackCallback = OnReadbackComplete;
            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BasisWebVideoOutput" };
            worker.Start();
            return true;
        }
        public void PushFrame(RenderTexture frame, int quality)
        {
            if (!CanAcceptFrame) return;

            frameWidth = frame.width;
            frameHeight = frame.height;
            frameQuality = Mathf.Clamp(quality, 1, 100);
            readbackInFlight = true;
            AsyncGPUReadback.Request(frame, 0, TextureFormat.RGBA32, readbackCallback);
        }
        public void Stop()
        {
            running = false;
            frameSignal.Set();
            try { listener?.Stop(); } catch (Exception) { }
            listener = null;

            if (worker != null)
            {
                worker.Join(500);
                worker = null;
            }

            for (int Index = 0; Index < clients.Count; Index++)
            {
                try { clients[Index].Client.Close(); } catch (Exception) { }
            }
            clients.Clear();
            for (int Index = 0; Index < handshakes.Count; Index++)
            {
                try { handshakes[Index].Client.Close(); } catch (Exception) { }
            }
            handshakes.Clear();
            clientCount = 0;
            lock (rawLock)
            {
                rawReady = false;
                rawFrame = null;
                rawSpare = null;
            }
            lock (frameLock) pendingFrame = null;
        }
        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            readbackInFlight = false;
            if (!running || request.hasError) return;

            if (encodeOnMainThread)
            {
                byte[] jpeg = EncodeJpeg(request.GetData<byte>(), frameWidth, frameHeight, frameQuality);
                if (jpeg != null)
                {
                    lock (frameLock) pendingFrame = jpeg;
                    frameSignal.Set();
                }
                return;
            }

            NativeArray<byte> data = request.GetData<byte>();
            byte[] target;
            lock (rawLock)
            {
                target = rawSpare;
                rawSpare = null;
            }
            if (target == null || target.Length != data.Length) target = new byte[data.Length];
            data.CopyTo(target);

            lock (rawLock)
            {
                if (rawFrame != null && rawSpare == null) rawSpare = rawFrame;
                rawFrame = target;
                rawWidth = frameWidth;
                rawHeight = frameHeight;
                rawQuality = frameQuality;
                rawReady = true;
            }
            frameSignal.Set();
        }
        private byte[] TakeRawAndEncode()
        {
            if (encodeOnMainThread) return null;

            byte[] raw;
            int width, height, quality;
            lock (rawLock)
            {
                if (!rawReady) return null;
                raw = rawFrame;
                rawFrame = null;
                rawReady = false;
                width = rawWidth;
                height = rawHeight;
                quality = rawQuality;
            }

            try
            {
                return EncodeJpeg(raw, width, height, quality);
            }
            finally
            {
                lock (rawLock)
                {
                    if (rawSpare == null) rawSpare = raw;
                }
            }
        }
        private byte[] EncodeJpeg(byte[] raw, int width, int height, int quality)
        {
            try
            {
                return ImageConversion.EncodeArrayToJPG(raw, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height, 0, quality);
            }
            catch (Exception e)
            {
                if (!encodeOnMainThread)
                {
                    encodeOnMainThread = true;
                    BasisDebug.LogWarning($"Web stream falling back to main-thread JPEG encoding ({e.GetType().Name}).", BasisDebug.LogTag.Camera);
                }
                else
                {
                    FailureMessage = $"JPEG encode failed ({e.GetType().Name}: {e.Message})";
                }
                return null;
            }
        }
        private byte[] EncodeJpeg(NativeArray<byte> raw, int width, int height, int quality)
        {
            NativeArray<byte> encoded = default;
            try
            {
                encoded = ImageConversion.EncodeNativeArrayToJPG(raw, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height, 0, quality);
                return encoded.ToArray();
            }
            catch (Exception e)
            {
                FailureMessage = $"JPEG encode failed ({e.GetType().Name}: {e.Message})";
                return null;
            }
            finally
            {
                if (encoded.IsCreated) encoded.Dispose();
            }
        }
        private void WorkerLoop()
        {
            while (running)
            {
                bool didWork = false;
                try
                {
                    while (listener != null && listener.Pending())
                    {
                        QueueHandshake(listener.AcceptTcpClient());
                        didWork = true;
                    }

                    if (ReapViewers()) didWork = true;
                    if (handshakes.Count > 0 && ServiceHandshakes()) didWork = true;

                    byte[] jpeg = TakeRawAndEncode();
                    if (jpeg != null)
                    {
                        Broadcast(jpeg);
                        didWork = true;
                    }

                    byte[] pending = TakeFrame();
                    if (pending != null)
                    {
                        Broadcast(pending);
                        didWork = true;
                    }
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"Web stream worker error: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
                }

                if (didWork) continue;

                frameSignal.Wait(handshakes.Count > 0 ? HandshakeWaitMs : IdleWaitMs);
                frameSignal.Reset();
            }
        }
        private void QueueHandshake(TcpClient client)
        {
            if (handshakes.Count >= MaxClients)
            {
                try { client.Close(); } catch (Exception) { }
                return;
            }

            client.NoDelay = true;
            client.SendTimeout = 2000;
            client.ReceiveTimeout = 250;
            handshakes.Add(new Handshake { Client = client, DeadlineTick = Environment.TickCount + HandshakeTimeoutMs });
        }
        private bool ServiceHandshakes()
        {
            bool progressed = false;
            for (int Index = handshakes.Count - 1; Index >= 0; Index--)
            {
                Handshake handshake = handshakes[Index];
                bool ready;
                try
                {
                    ready = handshake.Client.Available > 0;
                }
                catch (Exception)
                {
                    ready = false;
                    handshake.DeadlineTick = Environment.TickCount - 1;
                }

                if (!ready && Environment.TickCount - handshake.DeadlineTick < 0) continue;

                handshakes.RemoveAt(Index);
                progressed = true;
                try
                {
                    if (ready) AdmitViewer(handshake.Client);
                    else handshake.Client.Close();
                }
                catch (Exception e)
                {
                    BasisDebug.Log($"Web stream handshake dropped: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
                    try { handshake.Client.Close(); } catch (Exception) { }
                }
            }
            return progressed;
        }
        private void AdmitViewer(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            string path = ReadRequestPath(stream);

            if (!path.StartsWith(StreamPath, StringComparison.Ordinal))
            {
                WriteViewerPage(stream);
                client.Close();
                return;
            }

            if (clients.Count >= MaxClients)
            {
                client.Close();
                return;
            }

            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\n" + "Connection: close\r\n" + "Cache-Control: no-store, no-cache, must-revalidate\r\n" + "Pragma: no-cache\r\n" + $"Content-Type: multipart/x-mixed-replace; boundary={Boundary}\r\n\r\n");
            stream.Write(header, 0, header.Length);

            clients.Add(new Viewer { Client = client, Stream = stream });
            clientCount = clients.Count;
            BasisDebug.Log($"Web stream viewer connected ({clients.Count} total).", BasisDebug.LogTag.Camera);
        }
        private static string ReadRequestPath(NetworkStream stream)
        {
            try
            {
                byte[] scratch = new byte[1024];
                int read = stream.Read(scratch, 0, scratch.Length);
                if (read <= 0) return "/";

                string request = Encoding.ASCII.GetString(scratch, 0, read);
                int start = request.IndexOf(' ');
                if (start < 0) return "/";
                int end = request.IndexOf(' ', start + 1);
                return end < 0 ? "/" : request.Substring(start + 1, end - start - 1);
            }
            catch (Exception)
            {
                return "/";
            }
        }
        private static void WriteViewerPage(NetworkStream stream)
        {
            byte[] body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=\"utf-8\"><title>Basis Camera</title>" + "<style>html,body{margin:0;height:100%;background:#000}" + "img{width:100%;height:100%;object-fit:contain;display:block}</style>" + $"<img src=\"{StreamPath}\" alt=\"Basis Camera\">");

            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\n" + "Connection: close\r\n" + "Cache-Control: no-store, no-cache\r\n" + "Content-Type: text/html; charset=utf-8\r\n" + $"Content-Length: {body.Length}\r\n\r\n");

            stream.Write(header, 0, header.Length);
            stream.Write(body, 0, body.Length);
        }
        private void Broadcast(byte[] frame)
        {
            if (clients.Count == 0) return;

            byte[] part = Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
            int total = part.Length + frame.Length + FrameTrailer.Length;

            for (int Index = clients.Count - 1; Index >= 0; Index--)
            {
                Viewer viewer = clients[Index];

                if (viewer.Writing) continue;

                if (viewer.Scratch.Length < total) viewer.Scratch = new byte[total + (total >> 2)];
                Buffer.BlockCopy(part, 0, viewer.Scratch, 0, part.Length);
                Buffer.BlockCopy(frame, 0, viewer.Scratch, part.Length, frame.Length);
                Buffer.BlockCopy(FrameTrailer, 0, viewer.Scratch, part.Length + frame.Length, FrameTrailer.Length);

                viewer.WriteStartedTick = Environment.TickCount;
                viewer.Writing = true;
                try
                {
                    viewer.Stream.BeginWrite(viewer.Scratch, 0, total, WriteCompleted, viewer);
                }
                catch (Exception e)
                {
                    viewer.Writing = false;
                    DropViewer(Index, $"{e.GetType().Name}: {e.Message}");
                }
            }
            clientCount = clients.Count;
        }
        private void WriteCompleted(IAsyncResult result)
        {
            Viewer viewer = (Viewer)result.AsyncState;
            try
            {
                viewer.Stream.EndWrite(result);
            }
            catch (Exception e)
            {
                viewer.Failure = $"{e.GetType().Name}: {e.Message}";
                viewer.Dead = true;
            }
            finally
            {
                viewer.Writing = false;
            }
        }
        private bool ReapViewers()
        {
            bool removed = false;
            for (int Index = clients.Count - 1; Index >= 0; Index--)
            {
                Viewer viewer = clients[Index];
                if (viewer.Dead)
                {
                    DropViewer(Index, viewer.Failure);
                    removed = true;
                    continue;
                }

                if (viewer.Writing && Environment.TickCount - viewer.WriteStartedTick > WriteStallTimeoutMs)
                {
                    DropViewer(Index, $"no progress for {WriteStallTimeoutMs}ms");
                    removed = true;
                }
            }
            return removed;
        }
        private void DropViewer(int index, string reason)
        {
            Viewer viewer = clients[index];
            clients.RemoveAt(index);
            clientCount = clients.Count;
            try { viewer.Client.Close(); } catch (Exception) { }

            BasisDebug.Log($"Web stream client dropped: {reason ?? "closed"}.", BasisDebug.LogTag.Camera);
        }
        private byte[] TakeFrame()
        {
            lock (frameLock)
            {
                byte[] frame = pendingFrame;
                pendingFrame = null;
                return frame;
            }
        }
    }
}
