// A timed closed-caption cue surfaced by the media player. Slice 1 carries plain
// text (CEA-608 CC1) with the presentation time range it is active for; styling
// and positioning are intentionally not surfaced yet.
//
// Text is null when the active caption clears (subscribers should hide their
// display). StartUs/EndUs are microseconds from stream start; EndUs is long.MaxValue
// while the cue is still the latest one shown.
public readonly struct BasisCaptionCue
{
    public readonly string Text;
    public readonly long StartUs;
    public readonly long EndUs;

    public BasisCaptionCue(string text, long startUs, long endUs)
    {
        Text = text;
        StartUs = startUs;
        EndUs = endUs;
    }

}
