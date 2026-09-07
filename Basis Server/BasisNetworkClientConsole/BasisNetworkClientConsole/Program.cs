using System.Diagnostics;
using System.Runtime.InteropServices;
using Basis.Logging;
using Basis.Network;
using Basis.Config;
using Basis.Utils;
using Basis.Network.Core;

namespace Basis
{
    partial class Program
    {
        private const double DriverTickMs = 15.0;
        private const double MovementIntervalMs = 90.0;
        private const int MaxVoiceCatchUpFrames = 5;
        private static volatile bool _running = true;
        private static int _shutdownStarted;

        /// <summary>Driver iterations that took longer than DriverTickMs — the harness falling behind.</summary>
        private static long DriverOverruns;
        /// <summary>Worst driver iteration seen, in ms.</summary>
        private static double DriverPeakMs;

        public static async Task Main(string[] args)
        {
            ErrorHandlers.AttachGlobalHandlers();
            ConfigManager.LoadOrCreateConfigXml("ClientSimConfig.xml");
            NetDebug.Logger = new BasisClientLogger();

            // Face-data test mode: BASIS_EMIT_FACE=1 attaches a synthetic AdditionalAvatarData to
            // every avatar send and logs when other clients' additional data arrives — an
            // end-to-end probe of the face-tracking transport over real UDP. Companions:
            //   BASIS_FACE_SPACING=<m>  pin client i at (i*m,1,0), no random walk (distance tiers)
            //   BASIS_UPLINK_DELTAS=0   legacy all-keyframe uploads (no v42 uplink deltas)
            //   BASIS_PACKET_LOSS=<pct> simulate inbound/outbound UDP loss on every client
            //   BASIS_BUNDLE_CAPTURE=<path>  harvest decoded avatar-bundle bodies for Zstd
            //                                dictionary training (see BundleCaptureSink)
            //   BASIS_BUNDLE_CAPTURE_EVERY=<n>   keep 1 bundle in n (default 200)
            //   BASIS_BUNDLE_CAPTURE_MAX=<n>     stop after n samples (default 20000)
            string capturePath = Environment.GetEnvironmentVariable("BASIS_BUNDLE_CAPTURE");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                if (!int.TryParse(Environment.GetEnvironmentVariable("BASIS_BUNDLE_CAPTURE_EVERY"), out int captureEvery) || captureEvery < 1) captureEvery = 200;
                if (!int.TryParse(Environment.GetEnvironmentVariable("BASIS_BUNDLE_CAPTURE_MAX"), out int captureMax) || captureMax < 1) captureMax = 20000;
                BundleCaptureSink.Configure(capturePath, captureMax, captureEvery);
                BNL.Log($"[BundleCapture] Capturing 1 bundle in {captureEvery} (max {captureMax}) to {capturePath}.");
            }
            if (Environment.GetEnvironmentVariable("BASIS_EMIT_FACE") == "1")
            {
                MovementSender.EmitFaceData = true;
                BNL.Log("[FaceObserver] EmitFaceData enabled — every avatar send carries additional data.");
            }
            if (float.TryParse(Environment.GetEnvironmentVariable("BASIS_FACE_SPACING"), out float spacing) && spacing > 0f)
            {
                MovementSender.PinSpacingMeters = spacing;
                BNL.Log($"[FaceObserver] Positions pinned at {spacing}m spacing.");
            }
            if (Environment.GetEnvironmentVariable("BASIS_UPLINK_DELTAS") == "0")
            {
                MovementSender.UseUplinkDeltas = false;
                BNL.Log("[FaceObserver] Uplink deltas disabled — legacy all-keyframe uploads.");
            }
            // Spectator mode: join a live server (e.g. during a Unity-client repro) and report
            // whether OTHER senders' additional data reaches the wire, without emitting any.
            if (Environment.GetEnvironmentVariable("BASIS_FACE_OBSERVE_ONLY") == "1")
            {
                MessageHandler.ObserveOnly = true;
                BNL.Log("[FaceObserver] Observe-only: reporting additional data from other clients.");
            }
            // BASIS_POSE_OBSERVE=1: decode the rotation region of every avatar frame this
            // process RECEIVES and report the pose a remote would draw. Frame counts prove
            // delivery; this proves the frames carry a pose rather than a T-pose.
            if (Environment.GetEnvironmentVariable("BASIS_POSE_OBSERVE") == "1")
            {
                PoseObserver.Enabled = true;
                BNL.Log("[PoseObserver] Decoding received bone rotations.");
            }

            var clientManager = new ClientManager();
            clientManager.Prepare();

            // Every way this process is asked to stop ends in the same place, and all of them are
            // reachable: Ctrl-C interactively, SIGTERM from docker stop or systemd, a "stop" line
            // or a closed stdin from a harness driving it, and ProcessExit as the backstop for a
            // plain return. Before this, only ProcessExit was handled, and ProcessExit runs on a
            // budget measured in seconds - so a population of a few thousand never finished
            // announcing and the server timed most of them out instead of being told.
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Shutdown(clientManager);

            Console.CancelKeyPress += (_, e) =>
            {
                // Cancel the default kill so shutdown runs to completion rather than racing it.
                e.Cancel = true;
                Shutdown(clientManager);
                Environment.Exit(0);
            };

            using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                ctx.Cancel = true;
                Shutdown(clientManager);
                Environment.Exit(0);
            });

            StartStopRequestWatcher(clientManager);

            MovementSender.Initialize(clientManager.ClientCount);
            MovementSender.VoiceSender.Initialize(clientManager.ClientCount);

            // Drive all clients from one worker per CPU core
            StartClientDriverLoops(clientManager.FinalClients, clientManager.FinalPeers);

            // Simulated UDP loss (BASIS_PACKET_LOSS=<1-100>): set on the LiteNetLib transport
            // config BEFORE clients construct their managers, so uplink and downlink frames both
            // drop — exercises the keyframe-NACK/re-key recovery paths under realistic conditions.
            if (int.TryParse(Environment.GetEnvironmentVariable("BASIS_PACKET_LOSS"), out int lossPct) && lossPct > 0)
            {
                var lnl = Basis.Network.Core.BasisTransportConfigStore.Get<Basis.Network.Core.LNLTransportConfig>(
                    Basis.Network.Core.BasisNetworkStackRegistry.LiteNetLibId);
                lnl.SimulatePacketLoss = true;
                lnl.SimulationPacketLossChance = Math.Min(lossPct, 100);
                BNL.Log($"[FaceObserver] Simulating {lossPct}% packet loss on every client.");
            }

            await clientManager.StartClientsAsync();

            // Voice delivery accounting. On whenever voice is simulated: it is a per-frame dictionary
            // touch on the receive path, which is nothing against the avatar traffic beside it, and
            // without it a run can only report what the server chose to drop rather than what a
            // listener would actually have heard.
            if (Basis.Config.ConfigManager.SimulateVoice)
            {
                VoiceDeliveryStats.Enabled = true;
                _ = Task.Run(async () =>
                {
                    while (_running)
                    {
                        await Task.Delay(5000);
                        BNL.Log(VoiceDeliveryStats.Describe());
                    }
                });
            }

            // Periodic observer summary so a timed run ends with machine-readable totals.
            if (MovementSender.EmitFaceData || MessageHandler.ObserveOnly || PoseObserver.Enabled)
            {
                _ = Task.Run(async () =>
                {
                    while (_running)
                    {
                        await Task.Delay(5000);
                        BNL.Log(MessageHandler.Summary());
                        if (PoseObserver.Enabled) BNL.Log(PoseObserver.Summary());
                    }
                });
            }

            // Whether audio actually reaches the virtual cable is invisible otherwise: the capture
            // runs happily on silence, so a routing mistake looks identical to a working setup.
            if (MicrophoneCapture.Active)
            {
                _ = Task.Run(async () =>
                {
                    long lastFrames = 0, lastSpeech = 0;
                    while (_running)
                    {
                        await Task.Delay(5000);
                        long frames = Interlocked.Read(ref MicrophoneCapture.FramesCaptured);
                        long speech = Interlocked.Read(ref MicrophoneCapture.FramesSpeech);
                        long dF = frames - lastFrames, dS = speech - lastSpeech;
                        lastFrames = frames; lastSpeech = speech;
                        float peak = MicrophoneCapture.TakePeak();
                        if (dS > 0)
                            BNL.Log($"[Mic] {dF} frames/5s, {dS} with speech, peak {peak:F3} — transmitting.");
                        else if (peak <= 0f)
                            BNL.Log($"[Mic] {dF} frames/5s, peak 0.000 (digital silence) — nothing is routed into CABLE Input.");
                        else
                            BNL.Log($"[Mic] {dF} frames/5s, peak {peak:F4} but under the transmit threshold — signal is arriving, just too quiet.");
                    }
                });
            }

            // Report whether the harness itself is keeping up. Without this a driver that cannot
            // hit its tick looks identical to a server that cannot keep up, and every number the
            // run produces is quietly a measurement of the load generator instead.
            _ = Task.Run(async () =>
            {
                long lastOverruns = 0;
                while (_running)
                {
                    await Task.Delay(10000);
                    long overruns = Interlocked.Read(ref DriverOverruns);
                    long delta = overruns - lastOverruns;
                    lastOverruns = overruns;
                    double peak = Interlocked.Exchange(ref DriverPeakMs, 0);
                    if (delta > 0)
                        BNL.Log($"[Driver] BEHIND: {delta} slice overruns in 10s (peak {peak:F0}ms vs {DriverTickMs}ms tick) — harness is limiting, not the server.");
                    else
                        BNL.Log($"[Driver] healthy: 0 overruns in 10s ({DriverTickMs}ms tick met).");

                    BNL.Log(MessageHandler.SenderFairness());
                }
            });

            // Start random reconnects
            _ = StartRandomReconnectLoop(clientManager);

            await Task.Delay(-1); // keep main alive
        }


        /// <summary>
        /// Runs the shutdown once, whoever asks for it first.
        ///
        /// <para>Ctrl-C, SIGTERM and ProcessExit can all fire for one stop - Ctrl-C in particular
        /// runs its handler and then ProcessExit - so this has to be idempotent or the population
        /// is torn down twice and the second pass throws on already-disposed transports.</para>
        /// </summary>
        private static void Shutdown(ClientManager clientManager)
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;

            Console.WriteLine("Shutting down...");
            _running = false;
            MicrophoneCapture.Stop();
            clientManager.StopClientsAsync().GetAwaiter().GetResult();
            // Close the capture file here rather than relying on the finalizer: a run is
            // normally ended with Ctrl-C, and a half-written last record would make the
            // whole capture unreadable to the trainer.
            string captureSummary = BundleCaptureSink.Finish();
            if (captureSummary != null) Console.WriteLine(captureSummary);
        }

        /// <summary>
        /// Lets whatever started this process ask it to leave cleanly.
        ///
        /// <para>A harness cannot send SIGTERM on Windows, and killing the process runs no managed
        /// code at all - which is exactly the case that leaves a server holding several thousand
        /// peers until they time out. Watching stdin gives every platform one graceful stop: a
        /// "stop" or "quit" line, or simply closing the stream, both mean leave now.</para>
        ///
        /// <para>Harmless when nobody is driving it. An interactive run just blocks on a console
        /// nobody types into, and this thread is a background one, so it never holds up exit.</para>
        /// </summary>
        private static void StartStopRequestWatcher(ClientManager clientManager)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        string line = Console.ReadLine();

                        // End of stream is NOT a stop request. A process started with stdin closed
                        // - nohup, systemd, a detached launch - reads EOF immediately, and treating
                        // that as "leave now" would shut the run down the moment it started. Only an
                        // explicit word means stop; EOF just means nobody is going to send one.
                        if (line == null) return;

                        line = line.Trim();
                        if (line.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
                            line.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                            line.Equals("exit", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }
                }
                catch
                {
                    // No console to read at all. Nothing to wait for, and nothing to stop.
                    return;
                }

                Shutdown(clientManager);
                Environment.Exit(0);
            })
            {
                Name = "StopRequestWatcher",
                IsBackground = true,
            };
            thread.Start();
        }

        public static void StopClient(ClientManager manager, int index)
        {
            var peer = Volatile.Read(ref manager.FinalPeers[index]);
            if (peer != null)
            {
                peer.Disconnect();
            }
        }

        private static void StartClientDriverLoops(NetworkClient[] clients, NetPeer[] peers)
        {
            int count = peers.Length;
            int workerCount = Math.Min(Environment.ProcessorCount, count);
            if (workerCount <= 0) return;

            int chunkSize = (count + workerCount - 1) / workerCount;

            for (int w = 0; w < workerCount; w++)
            {
                int start = w * chunkSize;
                int end = Math.Min(start + chunkSize, count);
                if (start >= end) break;

                double phaseOffsetMs = MovementIntervalMs * w / workerCount;

                var thread = new Thread(() => DriveSlice(clients, peers, start, end, phaseOffsetMs))
                {
                    Name = $"ClientDriver({start}-{end})",
                    IsBackground = true
                };
                thread.Start();
            }
        }

        private static void DriveSlice(NetworkClient[] clients, NetPeer[] peers, int start, int end, double phaseOffsetMs)
        {
            var sw = Stopwatch.StartNew();
            double lastTickMs = 0;
            double lastMovementMs = phaseOffsetMs - MovementIntervalMs;
            double lastVoiceMs = 0;

            // Amortized voice-recipient sweep state: a cursor over this worker's slice plus the
            // fractional number of rebuilds owed, so the sweep runs at a steady rate rather than in
            // bursts. See the sweep in the voice block below.
            int sliceCount = end - start;
            int refreshCursor = start;
            double refreshDebt = 0;
            double lastRefreshMs = 0;


            while (_running)
            {
                double nowMs = sw.Elapsed.TotalMilliseconds;
                float dt = (float)(nowMs - lastTickMs);
                lastTickMs = nowMs;

                for (int i = start; i < end; i++)
                {
                    var client = Volatile.Read(ref clients[i]);
                    if (client != null)
                    {
                        client.Poll();
                        client.Update(dt);
                    }
                }

                if (nowMs - lastMovementMs >= MovementIntervalMs)
                {
                    lastMovementMs = nowMs;
                    for (int i = start; i < end; i++)
                    {
                        var peer = Volatile.Read(ref peers[i]);
                        if (peer != null && (peer.Tag as ConsoleClientIdentity)?.Authenticated == true)
                            MovementSender.ProcessSingle(peer, i);
                    }
                }

                if (Basis.Config.ConfigManager.SimulateVoice)
                {
                    double voiceFrameMs = Basis.Config.ConfigManager.VoiceFrameMs;
                    if (voiceFrameMs <= 0) voiceFrameMs = 20;

                    int dueFrames = (int)((nowMs - lastVoiceMs) / voiceFrameMs);
                    if (dueFrames > MaxVoiceCatchUpFrames)
                    {
                        dueFrames = MaxVoiceCatchUpFrames;
                        lastVoiceMs = nowMs;
                    }
                    else if (dueFrames > 0)
                    {
                        lastVoiceMs += dueFrames * voiceFrameMs;
                    }

                    // Amortized recipient sweep.
                    //
                    // Each client's audible set is an O(N) scan, and every client used to run its own
                    // rebuild timer — so the work per tick scaled with how many clients this worker
                    // owned, and at 4000 clients the driver could not hit its 15ms tick at all
                    // (measured: 400-500 overruns per 10s, peaks over 200ms). Since the driver was
                    // then polling clients late, the run stopped measuring the server and started
                    // measuring the harness.
                    //
                    // Instead the whole slice is swept once per window at a steady rate: carry the
                    // fractional debt between ticks and rebuild only the clients that come due. The
                    // per-tick cost is set by the window, not by the population, so it stays flat as
                    // clients are added — the window just takes proportionally longer per client.
                    double windowMs = Basis.Config.ConfigManager.VoiceRecipientRefreshMs;
                    if (windowMs <= 0) windowMs = 5000;
                    refreshDebt += sliceCount * (nowMs - lastRefreshMs) / windowMs;
                    lastRefreshMs = nowMs;
                    int dueRebuilds = (int)refreshDebt;
                    if (dueRebuilds > 0)
                    {
                        refreshDebt -= dueRebuilds;
                        // Never let a stall turn into a burst that stalls the next tick too.
                        if (dueRebuilds > sliceCount) dueRebuilds = sliceCount;
                        for (int n = 0; n < dueRebuilds; n++)
                        {
                            int idx = refreshCursor;
                            if (++refreshCursor >= end) refreshCursor = start;
                            var sweepPeer = Volatile.Read(ref peers[idx]);
                            if (sweepPeer != null && (sweepPeer.Tag as ConsoleClientIdentity)?.Authenticated == true)
                                MovementSender.VoiceSender.RebuildRecipients(sweepPeer, peers, idx);
                        }
                    }

                    for (int i = start; i < end && dueFrames > 0; i++)
                    {
                        var peer = Volatile.Read(ref peers[i]);
                        if (peer == null || (peer.Tag as ConsoleClientIdentity)?.Authenticated != true) continue;

                        // A client that has never been swept builds once immediately, so a joiner can
                        // transmit without waiting out a window. After that the sweep owns it.
                        bool ready = MovementSender.VoiceSender.HasRecipients(i);
                        if (!ready)
                        {
                            ready = MovementSender.VoiceSender.RebuildRecipients(peer, peers, i);
                        }
                        if (ready)
                        {
                            bool talking = MovementSender.VoiceSender.IsTalking(i, nowMs);
                            bool mic = MovementSender.VoiceSender.IsMicClient(i);

                            if (talking && mic)
                            {
                                MovementSender.VoiceSender.SendMicFrames(peer, i, dueFrames);
                            }
                            else if (talking)
                            {
                                for (int f = 0; f < dueFrames; f++)
                                    MovementSender.VoiceSender.SendFrame(peer, i);
                            }
                            else
                            {
                                // Idle mic clients track the live edge, so a burst opens on current
                                // audio instead of replaying whatever was buffered when they went quiet.
                                if (mic) MovementSender.VoiceSender.SyncMicCursor(i);
                                for (int f = 0; f < dueFrames; f++)
                                    MovementSender.VoiceSender.NoteSilence(i);
                            }
                        }
                    }
                }

                // A slice that takes longer than the tick silently degrades the whole simulation:
                // clients stop being polled on time, inbound packets back up in the socket buffer,
                // and peers start timing out — which looks exactly like a server that cannot keep
                // up. Track it so harness limits can never be mistaken for server results.
                double iterationMs = sw.Elapsed.TotalMilliseconds - nowMs;
                int sleepMs = (int)(DriverTickMs - iterationMs);
                if (sleepMs > 0)
                {
                    Thread.Sleep(sleepMs);
                }
                else
                {
                    Interlocked.Increment(ref DriverOverruns);
                    double peak;
                    while ((peak = Volatile.Read(ref DriverPeakMs)) < iterationMs &&
                           Interlocked.CompareExchange(ref DriverPeakMs, iterationMs, peak) != peak) { }
                }
            }
        }

        private static async Task StartRandomReconnectLoop(ClientManager clientManager)
        {
            int totalClients = clientManager.ClientCount;

            while (true)
            {
                int waitMinutes = Random.Shared.Next(1, 21); // 1–20 minutes
                await Task.Delay(TimeSpan.FromMinutes(waitMinutes));

                int indexToRestart = Random.Shared.Next(0, totalClients);
                BNL.Log($"Randomly restarting client at index {indexToRestart}");

                await clientManager.ReconnectClientAsync(indexToRestart);
            }
        }
    }
}
