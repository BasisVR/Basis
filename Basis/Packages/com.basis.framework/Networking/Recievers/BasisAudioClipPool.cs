using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Device_Management;
using System;
using System.Collections.Generic;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisAudioClipPool
{
    // Kept in step with BasisAudioRemoteSource.MaxPoolSize — a returned voice object and its clip
    // come back together, so a smaller clip pool just means the paired Get() destroys and
    // re-Creates (SoundManager.CreateSoundInternal) on a reuse the object pool served for free.
    // Each clip is one Opus frame duration of mono float, a few KB, so the count is cheap.
    private static int maxPooledClips = 64;//how many pooled sources.

    /// <summary>
    /// Multiplier applied to <see cref="SharedOpusSettings.DesiredDurationInSeconds"/>
    /// when sizing newly-allocated <see cref="AudioClip"/>s. Acts as a secondary
    /// playback buffer between the decoded PCM queue and Unity's AudioSource;
    /// smaller = lower latency but tighter coupling to underrun, larger = more
    /// memory + more headroom. Existing pooled clips keep their original size,
    /// so callers should <see cref="Clear"/> the pool after lowering this if
    /// they want the change to take effect immediately on next <see cref="Get"/>.
    /// </summary>
    public static int ClipBufferScalar = 2;

    private static Queue<AudioClip> pool = new Queue<AudioClip>();

    // Cached buffer for resetting clips — avoids allocating a new float[] per Get().
    private static float[] _resetBuffer;

    /// <summary>
    /// Gets an AudioClip from the pool or creates a new one if pool is empty.
    /// </summary>
    public static AudioClip Get(ushort LinkedPlayer)
    {
        if (pool.Count > 0)
        {
            AudioClip clip = pool.Dequeue();
            int needed = clip.samples * clip.channels;
            if (_resetBuffer == null || _resetBuffer.Length < needed)
            {
                _resetBuffer = new float[needed];
            }

            clip.SetData(_resetBuffer, 0);
            clip.name = $"player [{LinkedPlayer}]";
            return clip;
        }
        else
        {
            int scalar = Mathf.Max(1, ClipBufferScalar);
            int clipFrames = Mathf.CeilToInt(SharedOpusSettings.DesiredDurationInSeconds * scalar * AudioSettings.outputSampleRate);
            return AudioClip.Create($"player [{LinkedPlayer}]", clipFrames, RemoteOpusSettings.Channels, AudioSettings.outputSampleRate, false, (buf) =>
            {
                Array.Clear(buf, 0, buf.Length);
            });
        }
    }
    /// <summary>
    /// Returns an AudioClip to the pool for reuse.
    /// </summary>
    public static void Return(AudioClip clip)
    {
        if (clip == null) return;

        if (pool.Count < maxPooledClips)
        {
            pool.Enqueue(clip);
        }
        else
        {
            AudioClip.Destroy(clip); // optional: or just don't enqueue it
        }
    }

    /// <summary>
    /// Clears the entire pool and destroys the pooled AudioClips.
    /// </summary>
    public static void Clear()
    {
        foreach (var clip in pool)
        {
            AudioClip.Destroy(clip);
        }
        pool.Clear();
    }

    /// <summary>
    /// Total clips currently in the pool.
    /// </summary>
    public static int Count => pool.Count;
}
