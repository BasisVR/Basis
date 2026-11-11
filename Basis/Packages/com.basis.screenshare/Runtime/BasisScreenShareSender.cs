using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using static Basis.Network.Core.BasisScreenShareProtocol;

public sealed class BasisScreenShareSender : IDisposable
{
    const int UplinkHigh = 150, UplinkLow = 30, WaitMs = 50;
    static readonly long AdaptTicks = Stopwatch.Frequency, PruneTicks = Stopwatch.Frequency / 4;
    readonly ConcurrentDictionary<ushort, bool> direct = new ConcurrentDictionary<ushort, bool>();
    readonly List<ushort> targets = new List<ushort>(), dropped = new List<ushort>();
    readonly byte[] audioBuffer = new byte[4000];
    byte[] videoBuffer = new byte[256 * 1024];
    readonly Thread thread;
    volatile bool running = true;
    long capture;
    volatile bool relay, accepted;
    int targetKbps, currentKbps;
    uint videoSequence, audioSequence;
    long lastPts = -1, nextAdapt, nextPrune;
    public int DirectViewers => direct.Count;
    public int CurrentKbps => Volatile.Read(ref currentKbps);
    public long LastPts => Interlocked.Read(ref lastPts);
    public event Action DirectViewersChanged;

    public BasisScreenShareSender()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        thread = new Thread(Run) { Name = "Basis Screen Share Sender", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        thread.Start();
#else
        thread = null;
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    public void Tick()
    {
        ulong handle = (ulong)Interlocked.Read(ref capture);
        if (!running || handle == 0) return;
        try
        {
            if (BasisScreenShareNative.bm_capture_wait(handle, 0) > 0) Pump(handle);
            Maintain();
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"[ScreenShare] sender: {e}");
        }
    }
#endif

    public void SetCapture(ulong handle, int target)
    {
        Volatile.Write(ref targetKbps, target);
        Volatile.Write(ref currentKbps, target);
        Interlocked.Exchange(ref capture, (long)handle);
    }

    public void SetTarget(int target)
    {
        Volatile.Write(ref targetKbps, target);
        if (CurrentKbps > target) ApplyBitrate(target);
    }

    public void SetRouting(bool isAccepted, int serverViewers)
    {
        accepted = isAccepted;
        relay = isAccepted && serverViewers > 0;
        if (!isAccepted && !direct.IsEmpty)
        {
            direct.Clear();
            DirectViewersChanged?.Invoke();
        }
    }

    public bool AddDirectViewer(ushort viewer)
    {
        if (!accepted) return false;
        direct[viewer] = true;
        RequestKeyframe();
        return true;
    }

    public bool RemoveDirectViewer(ushort viewer) => direct.TryRemove(viewer, out _);

    public void MarkNeedsKey(ushort viewer)
    {
        if (!direct.ContainsKey(viewer)) return;
        direct[viewer] = true;
        RequestKeyframe();
    }

    public void RequestKeyframe()
    {
        ulong handle = (ulong)Interlocked.Read(ref capture);
        if (handle != 0) BasisScreenShareNative.bm_capture_request_keyframe(handle);
    }

    void ApplyBitrate(int kbps)
    {
        Volatile.Write(ref currentKbps, kbps);
        ulong handle = (ulong)Interlocked.Read(ref capture);
        if (handle != 0) BasisScreenShareNative.bm_capture_set_bitrate(handle, (uint)kbps);
    }

    void Run()
    {
        while (running)
        {
            ulong handle = (ulong)Interlocked.Read(ref capture);
            if (handle == 0)
            {
                Thread.Sleep(WaitMs);
                continue;
            }
            try
            {
                if (BasisScreenShareNative.bm_capture_wait(handle, WaitMs) > 0) Pump(handle);
                Maintain();
            }
            catch (EntryPointNotFoundException e)
            {
                BasisDebug.LogError($"[ScreenShare] the loaded media plugin is older than this package, restart to load the new one: {e.Message}");
                running = false;
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[ScreenShare] sender: {e}");
                Thread.Sleep(WaitMs);
            }
        }
    }

    void Pump(ulong handle)
    {
        bool sendRelay = relay, sendDirect = accepted && !direct.IsEmpty;
        int read;
        while ((read = BasisScreenShareNative.ReadVideo(handle, ref videoBuffer, out BasisScreenShareNative.EncodedFrame frame)) > 0)
        {
            Track(frame.PtsUs);
            if (!sendRelay && !sendDirect) continue;
            bool key = frame.Key != 0;
            uint sequence = videoSequence++;
            if (sendRelay && !BasisScreenShareClient.SendFrameRelay(sequence, KindVideo, key, frame.PtsUs, videoBuffer, read)) RequestKeyframe();
            if (sendDirect && SelectTargets(true, key)) BasisScreenShareClient.SendFrameDirect(targets, sequence, KindVideo, key, frame.PtsUs, videoBuffer, read);
        }
        while ((read = BasisScreenShareNative.ReadAudio(handle, audioBuffer, out long pts)) > 0)
        {
            Track(pts);
            if (!sendRelay && !sendDirect) continue;
            uint sequence = audioSequence++;
            if (sendRelay) BasisScreenShareClient.SendFrameRelay(sequence, KindAudio, true, pts, audioBuffer, read);
            if (sendDirect && SelectTargets(false, true)) BasisScreenShareClient.SendFrameDirect(targets, sequence, KindAudio, true, pts, audioBuffer, read);
        }
    }

    void Track(long pts)
    {
        if (pts > Interlocked.Read(ref lastPts)) Interlocked.Exchange(ref lastPts, pts);
    }

    bool SelectTargets(bool video, bool key)
    {
        targets.Clear();
        dropped.Clear();
        foreach (KeyValuePair<ushort, bool> pair in direct)
        {
            if (BasisScreenShareClient.DirectBacklog(pair.Key) > BasisScreenShareClient.DirectQueueLimit)
            {
                if (video) dropped.Add(pair.Key);
                continue;
            }
            if (video && pair.Value && !key) continue;
            targets.Add(pair.Key);
        }
        if (video)
        {
            foreach (ushort viewer in dropped) direct[viewer] = true;
            if (dropped.Count > 0) RequestKeyframe();
            if (key) foreach (ushort viewer in targets) direct.TryUpdate(viewer, false, true);
        }
        return targets.Count > 0;
    }

    void Maintain()
    {
        long now = Stopwatch.GetTimestamp();
        if (now >= nextPrune && !direct.IsEmpty)
        {
            nextPrune = now + PruneTicks;
            bool changed = false;
            foreach (ushort viewer in direct.Keys)
            {
                if (BasisScreenShareClient.HasDirectLink(viewer)) continue;
                changed |= direct.TryRemove(viewer, out _);
            }
            if (changed) DirectViewersChanged?.Invoke();
        }
        if (now < nextAdapt || (!relay && direct.IsEmpty)) return;
        nextAdapt = now + AdaptTicks;
        int pending = relay ? Math.Max(BasisScreenShareClient.PendingUplink(), BasisScreenShareClient.ServerCongestion) : 0;
        foreach (ushort viewer in direct.Keys) pending = Math.Max(pending, Math.Min(BasisScreenShareClient.DirectBacklog(viewer), 100000));
        int current = CurrentKbps, target = Volatile.Read(ref targetKbps), next = current;
        if (pending > UplinkHigh) next = Math.Max(BasisScreenShareSettings.MinBitrateKbps, (int)(current * 0.75f));
        else if (pending < UplinkLow && current < target) next = Math.Min(target, (int)(current * 1.08f) + 50);
        if (next != current) ApplyBitrate(next);
    }

    public void Dispose()
    {
        running = false;
        Interlocked.Exchange(ref capture, 0);
        if (thread != null && thread.IsAlive && !thread.Join(500)) BasisDebug.LogWarning("[ScreenShare] sender thread did not stop in time");
    }
}
