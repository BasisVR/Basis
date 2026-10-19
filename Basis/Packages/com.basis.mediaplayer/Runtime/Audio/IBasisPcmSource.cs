/// <summary>
/// A decoded-audio producer the managed audio stack can pull from: one
/// interleaved float ring, consumed once, on the audio thread.
///
/// The engine is the only implementation that ships. The splitter, the taps
/// and the per-channel outputs are written against this interface alone, so
/// they can be exercised without a session open.
/// </summary>
public interface IBasisPcmSource
{
    /// <summary>Stream audio format, once known. False until the first
    /// audio frame has been decoded; the sink stays silent until
    /// then.</summary>
    bool TryGetPcmFormat(out int sampleRate, out int channels);

    /// <summary>
    /// Fill <paramref name="buffer"/> with up to its length in interleaved
    /// float samples and return how many floats were written, in whole
    /// frames. The caller zero-fills the remainder. Must not block and must
    /// be safe to call from the audio thread.
    /// </summary>
    int ReadPcm(float[] buffer);

    /// <summary>
    /// Rate trim the clock owner wants the pull to run at, parts per
    /// million either side of the stream rate (0 = none). Shared playback
    /// converges by nudging how fast the ring is consumed, so the figure
    /// has to reach the consumer rather than being applied inside the
    /// producer. Read on the main thread once a frame, not per DSP block.
    /// </summary>
    int PullRateOffsetPpm { get; }
}
