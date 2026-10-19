namespace Basis.Scripts.Constraints
{
    public enum BasisConstraintKind : byte
    {
        Position = 0,
        Rotation = 1,
        Scale = 2,
        Parent = 3,
        Aim = 4,
        LookAt = 5,
        Blend = 6,
        Override = 7,
        Damped = 8,
        TwistCorrection = 9,
        TwoBoneIK = 10,
        ChainIK = 11,
        TwistChain = 12,
        Referential = 13,
    }
}
