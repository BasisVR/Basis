internal static class BasisDeviceMarkers
{
    const string Group = "BasisDriver.DeviceManagement";
    public static readonly BasisMarker Loop = new BasisMarker(Group, "Loop"),
        BaseTypes = new BasisMarker(Group, "BaseTypes");
}
internal static class BasisLocoPoseMarkers
{
    const string Group = "BasisDriver.LocoPose";
    public static readonly BasisMarker Gate = new BasisMarker(Group, "Gate"),
        GraphStep = new BasisMarker(Group, "GraphStep"),
        Dispatch = new BasisMarker(Group, "Dispatch");
}
internal static class BasisConstraintMarkers
{
    const string Group = "BasisConstraints";
    public static readonly BasisMarker Rebuild = new BasisMarker(Group, "Rebuild"),
        Classify = new BasisMarker(Group, "Classify"),
        ScheduleSample = new BasisMarker(Group, "ScheduleSample"),
        Refresh = new BasisMarker(Group, "Refresh"),
        ScheduleSolve = new BasisMarker(Group, "ScheduleSolve");
    // Breakdown of Rebuild. It is the one stage whose cost is a spike rather than a rate, and the
    // four parts scale on different things — registrations, the dependency graph, the component
    // count, the transform count — so a rebuild that got slower says nothing until it is split.
    public static readonly BasisMarker RebuildFlatten = new BasisMarker(Group, "Rebuild.Flatten"),
        RebuildOrder = new BasisMarker(Group, "Rebuild.Order"),
        RebuildGroups = new BasisMarker(Group, "Rebuild.Groups"),
        RebuildBind = new BasisMarker(Group, "Rebuild.Bind");
}
internal static class BasisEyeMarkers
{
    const string Group = "BasisEye";
    public static readonly BasisMarker GazeGather = new BasisMarker(Group, "GazeGather"),
        GazeFrames = new BasisMarker(Group, "GazeFrames"),
        GazeRun = new BasisMarker(Group, "GazeRun");
}
internal static class BasisVisibilityMarkers
{
    const string Group = "BasisVisibility";
    public static readonly BasisMarker CollectCameras = new BasisMarker(Group, "CollectCameras"),
        Dispatch = new BasisMarker(Group, "Dispatch"),
        Join = new BasisMarker(Group, "Join"),
        Apply = new BasisMarker(Group, "Apply");
}
internal static class BasisNamePlateMarkers
{
    const string Group = "BasisNamePlate";
    public static readonly BasisMarker Rebuild = new BasisMarker(Group, "Rebuild"),
        Topology = new BasisMarker(Group, "Topology"),
        Finish = new BasisMarker(Group, "Finish");
}
