using Unity.Scripting.LifecycleManagement;
using UnityEngine;

/// <summary>
/// Builds the two authored audio arrangements in code: the stereo one output
/// case, and the eight positioned speakers a 5.1 / 7.1 mix wants.
///
/// The shipped prefabs are the reference for both (same GameObject names,
/// component set per output and speaker layout), so a rig built here matches
/// one built by hand from a prefab. The scene-setup menu items and the
/// developer A/B rig all build through here to keep them from drifting apart.
/// </summary>
[NoAutoStaticsCleanup]
public static class BasisMediaAudioRig
{
    /// <summary>Name of the single output a stereo arrangement uses.</summary>
    public const string StereoOutputName = "Stereo Downmix";

    /// <summary>
    /// The eight speakers, in decoded-channel order. Positions are the
    /// authored ones, stated against a screen 16 units wide so a caller can
    /// scale them to the screen it built.
    /// </summary>
    static readonly (string Name, Vector3 Position)[] SurroundLayout =
    {
        ("Channel 1 - Front Left",   new Vector3(-8f, -1f, -1f)),
        ("Channel 2 - Front Right",  new Vector3(8f, -1f, -1f)),
        ("Channel 3 - Front Centre", new Vector3(0f, -1f, -1f)),
        ("Channel 4 - LFE",          new Vector3(0f, -4f, -1f)),
        ("Channel 5 - Back Left",    new Vector3(-5f, -1f, -18f)),
        ("Channel 6 - Back Right",   new Vector3(5f, -1f, -18f)),
        ("Channel 7 - Side Left",    new Vector3(-9f, -1f, -12f)),
        ("Channel 8 - Side Right",   new Vector3(9f, -1f, -12f)),
    };

    /// <summary>
    /// Adds the audio component to <paramref name="host"/> and gives it one
    /// unspatialised stereo output, the arrangement stereo content wants.
    /// </summary>
    public static BasisMediaPlayerAudio AddStereoOutput(GameObject host)
    {
        BasisMediaPlayerAudio audio = EnsureAudio(host);
        AudioSource output = AddOutput(host.transform, StereoOutputName, Vector3.zero,
                                       BasisMediaAudioChannel.Selection.Stereo, spatialise: false);
        audio.Outputs = new[] { output };
        return audio;
    }

    /// <summary>
    /// Adds the audio component to <paramref name="host"/> and gives it the
    /// eight positioned per-channel outputs. A stream with fewer channels
    /// leaves the outputs it has no channel for silent, so this is also the
    /// right arrangement for 5.1.
    /// </summary>
    /// <param name="screenWidth">Width of the screen the speakers sit around,
    /// in world units. The authored layout is stated against a 16-unit screen
    /// and scales from there.</param>
    public static BasisMediaPlayerAudio AddSurroundOutputs(GameObject host, float screenWidth = 16f)
    {
        BasisMediaPlayerAudio audio = EnsureAudio(host);
        float scale = screenWidth > 0f ? screenWidth / 16f : 1f;
        var outputs = new AudioSource[SurroundLayout.Length];
        for (int i = 0; i < SurroundLayout.Length; i++)
        {
            outputs[i] = AddOutput(host.transform, SurroundLayout[i].Name,
                                   SurroundLayout[i].Position * scale,
                                   (BasisMediaAudioChannel.Selection)i, spatialise: true);
        }
        audio.Outputs = outputs;
        return audio;
    }

    static BasisMediaPlayerAudio EnsureAudio(GameObject host)
    {
        if (!host.TryGetComponent(out BasisMediaPlayerAudio audio))
            audio = host.AddComponent<BasisMediaPlayerAudio>();
        return audio;
    }

    // The tap is added here rather than left to the component's own rebuild:
    // a rebuild appends it, which puts it below any filter the object already
    // carries, and a filter above the tap is fed silence.
    static AudioSource AddOutput(Transform parent, string name, Vector3 localPosition,
                                 BasisMediaAudioChannel.Selection channel, bool spatialise)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;

        AudioSource source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.spatialize = spatialise;
        source.spatializePostEffects = true;
        source.spatialBlend = spatialise ? 1f : 0f;

        go.AddComponent<BasisMediaPlayerAudioTap>();
        go.AddComponent<BasisMediaAudioChannel>().Channel = channel;
        return source;
    }
}
