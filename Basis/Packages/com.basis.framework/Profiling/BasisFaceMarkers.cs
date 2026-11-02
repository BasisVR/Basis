internal static class BasisFaceMarkers
{
    const string Group = "BasisFace";
    public static readonly BasisMarker IdleLocal = new BasisMarker(Group, "IdleLocal"),
        IdleRemote = new BasisMarker(Group, "IdleRemote");
}
