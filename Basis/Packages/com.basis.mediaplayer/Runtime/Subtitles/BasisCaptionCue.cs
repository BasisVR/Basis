// A timed caption cue from a sidecar subtitle track: plain text with the
// presentation time range it is active for. Styling and positioning are
// deliberately not surfaced.
//
// Text is null when the active caption clears (subscribers should hide their
// display). StartUs/EndUs are microseconds from stream start.
//
// In-band CEA-608 captions do not use this type: the engine parses those and
// the player releases them as plain strings at their due position.
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
