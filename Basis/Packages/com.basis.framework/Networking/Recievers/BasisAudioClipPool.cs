using System;
using System.Collections.Generic;
using UnityEngine;

public static class BasisAudioClipPool
{
    private static int maxPooledClips = 16;//how many pooled sources.

    private static Queue<AudioClip> pool = new Queue<AudioClip>();

    /// <summary>
    /// Gets an AudioClip from the pool or creates a new one if pool is empty.
    /// </summary>
    public static AudioClip Get(ushort LinkedPlayer)
    {

        if (pool.Count > 0)
        {
            AudioClip clip = pool.Dequeue();
            float[] emptySamples = new float[clip.samples * clip.channels];

            Array.Fill(emptySamples, 1.0f);

            clip.SetData(emptySamples, 0);
            clip.name = $"player [{LinkedPlayer}]";
            return clip;
        }
        else
        {
            return AudioClip.Create($"player [{LinkedPlayer}]", RemoteOpusSettings.FrameSize * (2 * 2), RemoteOpusSettings.Channels, AudioSettings.outputSampleRate, false, (buf) =>
            {
                Array.Fill(buf, 1.0f);
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
            GameObject.Destroy(clip); // optional: or just don't enqueue it
        }
    }

    /// <summary>
    /// Clears the entire pool and destroys the pooled AudioClips.
    /// </summary>
    public static void Clear()
    {
        foreach (var clip in pool)
        {
            GameObject.Destroy(clip);
        }
        pool.Clear();
    }

    /// <summary>
    /// Total clips currently in the pool.
    /// </summary>
    public static int Count => pool.Count;
}
