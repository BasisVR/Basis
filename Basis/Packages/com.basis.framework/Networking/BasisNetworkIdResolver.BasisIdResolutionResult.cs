public struct BasisIdResolutionResult
{
    public ushort Id;
    public bool Success;

    public BasisIdResolutionResult(ushort id, bool success)
    {
        Id = id;
        Success = success;
    }
}
