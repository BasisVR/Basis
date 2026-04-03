using Unity.Mathematics;
[System.Serializable]
public struct BasisEyeCalibration
{
    // basis maps canonical eye-space -> rig eye local-space
    // canonical: +Z forward, +Y up, +X right
    public quaternion basis;
    public quaternion invBasis;
    public quaternion initialRotation;
}
