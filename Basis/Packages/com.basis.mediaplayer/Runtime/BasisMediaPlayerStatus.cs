/// <summary>The session's state as <see cref="BasisMediaPlayer.Status"/>
/// reports it, mapped from the engine's <see cref="BmState"/>. <c>Ready</c>
/// is never reported: the engine has no state between buffering and
/// playing.</summary>
public enum BasisMediaPlayerStatus
{
    NoMedia = 0,
    Connecting,
    Buffering,
    Ready,
    Playing,
    Paused,
    Stopped,
    Ended,
    Error,
}
