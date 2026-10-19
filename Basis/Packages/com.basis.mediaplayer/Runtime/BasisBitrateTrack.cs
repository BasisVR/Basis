/// <summary>A rung of a bitrate ladder, in the C player's shape. The engine
/// has no managed ladder: a resolver picks the rung before the open and the
/// engine picks an HLS variant for itself, so
/// <see cref="BasisMediaPlayer.BitrateTracks"/> is always empty. The type
/// exists so code that names it still compiles.</summary>
public sealed class BasisBitrateTrack
{
    public int Index = -1;
    public int BitsPerSecond;
    public int Width;
    public int Height;
    public string Codec;
    public string Label;
}
