using UnityEngine;

public static class BasisMuscleRange
{
    public static float[] MinMuscle;
    public static float[] MaxMuscle;
    public static float[] RangeMuscle;
  //  public static string[] MusclesName;
    public static int TotalMuscles;
    public const ushort UShortMin = ushort.MinValue;
    public const ushort UShortMax = ushort.MaxValue;
    public const ushort UShortRangeDifference = UShortMax - UShortMin;
    // Stores all muscles as a single appended string
    //public static string AllMusclesString;

    public static void Initalize()
    {
        TotalMuscles = HumanTrait.MuscleName.Length;
        MinMuscle = new float[TotalMuscles];
        MaxMuscle = new float[TotalMuscles];
        RangeMuscle = new float[TotalMuscles];
        //MusclesName = new string[TotalMuscles];

        for (int MuscleIndex = 0; MuscleIndex < TotalMuscles; MuscleIndex++)
        {
            MinMuscle[MuscleIndex] = HumanTrait.GetMuscleDefaultMin(MuscleIndex);
            MaxMuscle[MuscleIndex] = HumanTrait.GetMuscleDefaultMax(MuscleIndex);
            RangeMuscle[MuscleIndex] = MaxMuscle[MuscleIndex] - MinMuscle[MuscleIndex];
           // MusclesName[MuscleIndex] = HumanTrait.MuscleName[MuscleIndex];
           // AllMusclesString += $"{MusclesName[MuscleIndex]}: Range {RangeMuscle[MuscleIndex]} ";
        }
      //  BasisDebug.Log(AllMusclesString);
    }
}
