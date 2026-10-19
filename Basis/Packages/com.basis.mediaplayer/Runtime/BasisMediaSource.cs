using System;
using System.Collections.Generic;

/// <summary>How a source is delivered, as a <see cref="BasisMediaSource"/>
/// states it. Maps onto <see cref="BmLiveness"/>.</summary>
public enum BasisMediaDelivery
{
    Auto = 0,
    Live = 1,
    OnDemand = 2,
}

/// <summary>A source described in full, for
/// <see cref="BasisMediaPlayer.LoadSource"/>. The engine takes
/// <see cref="Uri"/>, <see cref="AudioUri"/>, <see cref="Delivery"/> and
/// <see cref="StartPosition"/>; <see cref="Loop"/>, <see cref="Volume"/>,
/// <see cref="Mute"/> and <see cref="Metadata"/> apply through the player's
/// own fields and <see cref="BasisMediaPlayer.ApplyMetadata"/>; the rest has
/// no engine counterpart and is carried only so code that sets it still
/// compiles.</summary>
public sealed class BasisMediaSource
{
    public BasisMediaSource() { }

    public BasisMediaSource(string uri)
    {
        Uri = uri;
    }

    public string Uri;
    public Dictionary<string, string> Headers;
    public string AudioUri;
    public BasisMediaDelivery Delivery = BasisMediaDelivery.Auto;
    public bool Loop;
    public float PlaybackRate = 1f;
    public float? Volume;
    public bool? Mute;
    public TimeSpan StartPosition = TimeSpan.Zero;
    public TimeSpan OpenTimeout = TimeSpan.Zero;
    public Dictionary<string, object> Options;
    public BasisMediaMetadata Metadata;

    public static BasisMediaSource FromUrl(string url) => new BasisMediaSource(url);

    public static BasisMediaSource FromLocalPath(string path) => new BasisMediaSource(path);
}
