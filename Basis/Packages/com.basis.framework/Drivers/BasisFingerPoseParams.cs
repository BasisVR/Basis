using System;

[Serializable]
public class BasisFingerPoseParams
{
    public float Stretch;
    public float Spread;

    public BasisFingerPoseParams(float stretch, float spread)
    {
        Stretch = stretch;
        Spread = spread;
    }
}
