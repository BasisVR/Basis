using UnityEngine;
using static SerializableBasis;

public static class BasisAvatarMuscleRange
{
    public const int SizeAfterGap = 95 - SecondBuffer;
    public const int FirstBuffer = 15;
    public const int SecondBuffer = 21;
    public static float[] MinMuscle;
    public static float[] MaxMuscle;
    public static float[] RangeMuscle;
    public static void Initalize()
    {
        MinMuscle = new float[LocalAvatarSyncMessage.StoredBones];
        MaxMuscle = new float[LocalAvatarSyncMessage.StoredBones];
        RangeMuscle = new float[LocalAvatarSyncMessage.StoredBones];
        for (int Index = 0, MuscleIndex = 0; Index < LocalAvatarSyncMessage.StoredBones; Index++)
        {
            if (Index < FirstBuffer || Index > SecondBuffer)
            {
                MinMuscle[MuscleIndex] = HumanTrait.GetMuscleDefaultMin(Index);
                MaxMuscle[MuscleIndex] = HumanTrait.GetMuscleDefaultMax(Index);
                MuscleIndex++;
            }
        }
        for (int Index = 0; Index < LocalAvatarSyncMessage.StoredBones; Index++)
        {
            RangeMuscle[Index] = MaxMuscle[Index] - MinMuscle[Index];
        }
    }
}
