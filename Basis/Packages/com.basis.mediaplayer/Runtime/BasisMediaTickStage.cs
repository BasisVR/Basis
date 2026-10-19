/// <summary>
/// Where a component sits in a player's per-frame tick. The player polls the
/// engine first and then runs its consumers in this order, so each one reads
/// the snapshot for the frame it is actually in.
/// </summary>
internal enum BasisMediaTickStage
{
    /// <summary>Reads the format and rate trim the poll announced.</summary>
    Audio,

    /// <summary>Reads the state and position the poll wrote, and sends.</summary>
    Networking,

    /// <summary>Reads the frame size and texture the poll produced.</summary>
    Output,

    /// <summary>Records the frame, so it must observe the whole of it.</summary>
    Diagnostics,
}

/// <summary>
/// A component driven by <see cref="BasisMediaPlayer"/>'s tick rather than by
/// its own Update. Register in OnEnable, unregister in OnDisable.
/// </summary>
internal interface IBasisMediaTickConsumer
{
    BasisMediaTickStage TickStage { get; }

    void MediaTick();
}
