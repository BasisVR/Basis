using System;
using UnityEngine;

// Feeds one BasisMediaPlayer output AudioSource by mixing its channel(s) from the
// shared PCM splitter on the audio thread via OnAudioFilterRead, instead of a
// streaming AudioClip. The streaming clip buffered roughly its own length ahead of
// the speaker (the dominant output latency, ~0.5 s); OnAudioFilterRead runs per DSP
// block, so latency collapses to the DSP buffer (~tens of ms) and is deterministic.
//
// Spatialisation is preserved: this is a filter component on the AudioSource's
// GameObject, so with the source's Spatialize + SpatializePostEffects enabled, Unity
// runs it BEFORE the spatialiser — Steam Audio (HRTF, occlusion, transmission) and
// Unity 3D then process the audio it generates exactly as they would a clip's. The
// source still plays a short silent looping clip to keep its DSP chain active; this
// overwrites that silence each block.
//
// One tap per output. The owning BasisMediaPlayerAudio binds it with the splitter,
// this output's channel taps, and a gain provider; the primary tap also reports the
// mixed block back for the diagnostics metrics (consumed samples / peak / RMS).
[RequireComponent(typeof(AudioSource))]
public sealed class BasisMediaPlayerAudioTap : MonoBehaviour
{
    private BasisMultiChannelPcmSplitter splitter;
    private BasisMultiChannelPcmSplitter.Reader reader;
    private BasisMultiChannelPcmSplitter.Tap[] taps;
    private Func<float> gainProvider;
    private Action<float[], int> onMixedBlock;   // null unless this is the primary output
    private bool spreadMono;                      // replicate ch0 across the DSP width (positioned mono sources)
    private double sourceStep = 1.0;              // source frames per output frame (source rate / DSP rate)
    private volatile bool active;
    private volatile int observedChannels;        // DSP width seen on the audio thread; read on the main thread

    // The DSP output width Unity hands this (spatialised) source. Recorded on the
    // audio thread, safe to read from the main thread for diagnostics.
    public int ObservedChannels => observedChannels;

    // Called on the main thread by BasisMediaPlayerAudio during (re)build. `metrics`
    // is non-null only for the primary output. `spreadMono` true when this output
    // plays a single decoded channel that should present as a mono point source
    // (so the spatialiser positions it), false for a stereo downmix.
    public void Bind(BasisMultiChannelPcmSplitter s, BasisMultiChannelPcmSplitter.Tap[] t,
                     bool spreadMonoAcrossChannels, Func<float> gain, Action<float[], int> metrics,
                     double sourceFramesPerOutputFrame)
    {
        splitter = s;
        reader = s?.CreateReader();
        taps = t;
        spreadMono = spreadMonoAcrossChannels;
        gainProvider = gain;
        onMixedBlock = metrics;
        sourceStep = sourceFramesPerOutputFrame > 0 ? sourceFramesPerOutputFrame : 1.0;
        observedChannels = 0;
        active = s != null && t != null && reader != null;
    }

    public void Unbind()
    {
        active = false;
        splitter = null;
        reader = null;
        taps = null;
        onMixedBlock = null;
    }

    // Audio thread. Overwrites this block with the output's mix. For a spatialised
    // source (Spatialize + SpatializePostEffects) this is the pre-spatialiser buffer,
    // so the mix is what Steam Audio / Unity 3D then position, occlude and transmit.
    private void OnAudioFilterRead(float[] data, int channels)
    {
        // Snapshot the binding so this block reads a consistent set even if a
        // main-thread Unbind()/Bind() (Rebuild on a format change) interleaves
        // mid-flight. A torn set would not crash (ReadMixed null-guards), but a
        // fresh reader against the previous splitter would mix one stale block.
        var s = splitter;
        var r = reader;
        var t = taps;
        if (!active || s == null || r == null || t == null || channels < 1) return; // leave the source silent

        observedChannels = channels;
        int frames = data.Length / channels;
        float gain = gainProvider != null ? gainProvider() : 1f;
        Array.Clear(data, 0, data.Length);
        s.ReadMixed(r, data, frames, channels, t, gain, sourceStep);

        // A positioned single channel is mixed into out-channel 0 by its tap; spread
        // it across the DSP width so the spatialiser receives a proper mono signal.
        if (spreadMono && channels > 1)
        {
            for (int f = 0; f < frames; f++)
            {
                float v = data[f * channels];
                int b = f * channels;
                for (int c = 1; c < channels; c++) data[b + c] = v;
            }
        }

        onMixedBlock?.Invoke(data, channels);
    }
}
