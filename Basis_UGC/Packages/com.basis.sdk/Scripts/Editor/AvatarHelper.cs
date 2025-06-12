using System.Collections.Generic;
using UnityEngine;

public static class AvatarHelper
{
    public static Dictionary<string, int> SearchForVisemeIndex = new Dictionary<string, int>
    {
        { "vrc.v_sil", 0 },
        { "vrc.v_PP", 1 },
        { "vrc.v_FF", 2 },
        { "vrc.v_TH", 3 },
        { "vrc.v_DD", 4 },
        { "vrc.v_kk", 5 },
        { "vrc.v_CH", 6 },
        { "vrc.v_SS", 7 },
        { "vrc.v_nn", 8 },
        { "vrc.v_RR", 9 },
        { "vrc.v_aa", 10 },
        { "vrc.v_E", 11 },
        { "vrc.v_ih", 12 },
        { "vrc.v_oh", 13 },
        { "vrc.v_ou", 14 },
        { "sil", 0 },
        { "PP", 1 },
        { "FF", 2 },
        { "TH", 3 },
        { "DD", 4 },
        { "kk", 5 },
        { "CH", 6 },
        { "SS", 7 },
        { "nn", 8 },
        { "RR", 9 },
        { "aa", 10 },
        { "E", 11 },
        { "ih", 12 },
        { "oh", 13 },
        { "ou", 14 },
    };
    public static List<string> SearchForBlinkIndex = new List<string>
    {
        { "vrc.blink"},
        { "blink"},
    };
    public static List<string> FindAllNames(SkinnedMeshRenderer Renderer)
    {
        List<string> OnMeshBlendShapes = new List<string>();
        int BlendCount = Renderer.sharedMesh.blendShapeCount;
        for (int Index = 0; Index < BlendCount; Index++)
        {
            OnMeshBlendShapes.Add(Renderer.sharedMesh.GetBlendShapeName(Index).ToLower());
        }
        return OnMeshBlendShapes;
    }
    public static bool GetBlendShapes(List<string> Names, string DiscoveryString, out int blendShapeIndex)
    {
        blendShapeIndex = Names.IndexOf(DiscoveryString.ToLower());
        if (blendShapeIndex == -1)
        {
            return false;
        }
        Debug.Log("found blend shape " + DiscoveryString + " at index " + blendShapeIndex);
        return true;
    }
    public static string BoolToText(bool State)
    {
        if (State)
        {
            return "Active";
        }
        else
        {
            return "Disabled";
        }
    }


}