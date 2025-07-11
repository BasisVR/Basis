public readonly struct BasisOwnershipResult
{
    public bool Success { get; }
    public ushort PlayerId { get; }

    public BasisOwnershipResult(bool success, ushort playerId)
    {
        Success = success;
        PlayerId = playerId;
    }

    public static BasisOwnershipResult Failed => new(false, 0);
}
