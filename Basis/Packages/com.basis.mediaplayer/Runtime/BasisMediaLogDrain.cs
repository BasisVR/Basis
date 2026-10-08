using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// The engine's free-text diagnostics, taken off the plugin's process-wide
/// ring once per frame and fanned out to the Console and to an in-memory
/// tail.
///
/// It carries what happens before a session handle exists and after it
/// closes (a transport refusal, a WHEP negotiation step, the Vulkan probe's
/// cause), so it is process-level rather than per-player.
/// The ring behind it is one queue for the whole plugin: every player
/// pumps this from its own tick and the first through each frame takes the
/// whole queue, so three players in a scene still print each line once.
///
/// Driving it from the players rather than from a startup hook is
/// deliberate. The drain is a P/Invoke, so anything that calls it loads
/// `basis_media`; a client whose scene has no media player in it should
/// not be mapping the plugin to poll an empty queue. The cost is that
/// lines produced with no player anywhere wait in the ring until one
/// appears, or age out of it.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisMediaLogDrain
{
    /// Records per drain call. The ring holds 512 and drops its oldest, so
    /// what one call cannot carry is asked for again rather than lost.
    const int DrainBatch = 32;

    /// Ceiling on one frame's drain, for the same reason the event drain has
    /// one: the ring holds 512, and taking all of it at once is a UTF-8
    /// decode and a Console line 512 times inside one frame. What is left
    /// stays in the ring and the next frame takes it.
    const int DrainPerFrame = 128;

    /// How much of the tail is kept for a diagnostics view to read. A tail,
    /// not a transcript: the Console and the platform log are where a whole
    /// session's narrative lives.
    const int HistoryCap = 256;

    /// The last frame a drain ran, so the second and third player through
    /// a frame stand down instead of finding an empty queue.
    static int _drainedFrame = -1;

    static readonly Queue<BasisMediaLogLine> _history = new Queue<BasisMediaLogLine>(HistoryCap);

    /// <summary>The most recent engine lines, oldest first. Bounded at 256.
    /// Nothing reads this yet; it exists so a diagnostics view can be built
    /// without the engine having to replay anything.</summary>
    public static IReadOnlyCollection<BasisMediaLogLine> History => _history;

    /// <summary>Raised for each line as it is drained, on the main thread.
    /// A view that wants to append rather than re-read <see cref="History"/>
    /// every frame subscribes here.</summary>
    public static event Action<BasisMediaLogLine> LineReceived;

    /// Lines the ring has evicted, as last reported, so growth is announced
    /// once rather than every frame it stays non-zero. The ring's count is the
    /// plugin's and outlives a Play session, so this must too.
    [NoAutoStaticsCleanup] static ulong _evictedSeen;

    /// <summary>From a player's tick. Idempotent within a frame.</summary>
    internal static unsafe void Pump()
    {
        if (_drainedFrame == Time.frameCount || Application.platform == RuntimePlatform.WebGLPlayer) return;
        _drainedFrame = Time.frameCount;

        var records = stackalloc BmLogRecord[DrainBatch];
        int drained = 0;
        int count;
        do
        {
            ulong evicted;
            count = BasisMediaNative.bm_drain_log(records, DrainBatch, &evicted);
            for (int i = 0; i < count; i++)
                Emit(&records[i]);
            ReportEvicted(evicted);
            if (count > 0) drained += count;
            // A short batch is the queue's end. A negative is an error code,
            // which ends the loop the same way.
        } while (count == DrainBatch && drained < DrainPerFrame);
    }

    // Taken by pointer rather than by value or `in`: a fixed buffer can only
    // be read through one, and copying a 256-byte record per line to avoid
    // that would be worse.
    static unsafe void Emit(BmLogRecord* record)
    {
        string detail = Encoding.UTF8.GetString(record->Detail, (int)record->DetailLen);
        var level = (BmLevel)record->Level;
        var line = new BasisMediaLogLine(record->WallUs, level, detail);

        if (_history.Count >= HistoryCap) _history.Dequeue();
        _history.Enqueue(line);

        // The clock here counts from the plugin's first diagnostic, not from
        // a session's start, so it is named rather than left to read as the
        // event drain's timestamp.
        string at = (record->WallUs / 1_000_000.0).ToString("F3", CultureInfo.InvariantCulture);
        string text = $"[BasisMedia process +{at}s] {detail}";
        switch (level)
        {
            // Unreported: an engine failure is usually the stream's fault or
            // the network's, not this client's, and it already reaches the
            // user through the session's own error. It does not belong in a
            // crash report.
            case BmLevel.Error:
                BasisDebug.LogErrorUnreported(text, BasisDebug.LogTag.Video);
                break;
            case BmLevel.Warn:
                BasisDebug.LogWarning(text, BasisDebug.LogTag.Video);
                break;
            default:
                BasisDebug.Log(text, BasisDebug.LogTag.Video);
                break;
        }

        Notify(line);
    }

    /// A subscriber is third-party code on a public event, and one throwing
    /// from inside the drain would take the rest of the batch with it: the
    /// Console lines, the tail, and every later subscriber. Those records
    /// have already left the native ring, so nothing could fetch them
    /// again. Each handler is invoked on its own and costs only itself.
    static void Notify(BasisMediaLogLine line)
    {
        Action<BasisMediaLogLine> handlers = LineReceived;
        if (handlers == null) return;

        Delegate[] list = handlers.GetInvocationList();
        for (int i = 0; i < list.Length; i++)
        {
            var handler = (Action<BasisMediaLogLine>)list[i];
            try
            {
                handler(line);
            }
            catch (Exception e)
            {
                // Keyed on the handler, so a chatty lane reports each broken
                // subscriber once rather than once per line.
                MethodInfo method = handler.Method;
                BasisDebug.LogErrorOnce(
                    $"BasisMediaLogDrain:{method.DeclaringType?.FullName}.{method.Name}",
                    $"[BasisMedia] log subscriber {method.DeclaringType?.Name}.{method.Name} threw: {e}",
                    BasisDebug.LogTag.Video);
            }
        }
    }

    static void ReportEvicted(ulong total)
    {
        if (total <= _evictedSeen) return;
        ulong lost = total - _evictedSeen;
        _evictedSeen = total;
        // The ring drops its oldest to keep taking new lines, so the hole is
        // at the start of what follows rather than at the end.
        BasisDebug.LogWarning(
            $"[BasisMedia] engine log ring overran: {lost} earlier line(s) lost, {total} this run",
            BasisDebug.LogTag.Video);
    }
}

/// <summary>One drained engine line.</summary>
public readonly struct BasisMediaLogLine
{
    /// <summary>Microseconds since the plugin's first diagnostic in this
    /// process. Not a session clock: a <see cref="BmEvent"/>'s timestamp
    /// counts from its own session's start, so the two are not comparable
    /// by subtraction.</summary>
    public readonly long WallUs;

    public readonly BmLevel Level;
    public readonly string Text;

    public BasisMediaLogLine(long wallUs, BmLevel level, string text)
    {
        WallUs = wallUs;
        Level = level;
        Text = text;
    }
}
