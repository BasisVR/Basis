using System;
using UnityEngine;

namespace Basis.Scripts.Networking.Compression
{
public static class BasisAbsLargestValue
{
    public static int IntAbsLargestValue(float[] Values)
    {
        float largest = float.MinValue;
        foreach (float value in Values)
        {
            float Abs = Math.Abs(value);
            if (Abs > largest)
            {
                largest = Abs;
            }
        }
        // Rounding up if the largest value has a decimal part
        int result = (int)Math.Ceiling(largest);
        // Handling the case where the result exceeds the maximum byte value
        if (result > int.MaxValue)
        {
            BasisDebug.LogError("Maxed Value ");
            result = int.MaxValue;
        }
        return result;
    }
}
}