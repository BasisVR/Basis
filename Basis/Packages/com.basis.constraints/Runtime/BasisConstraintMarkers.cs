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
