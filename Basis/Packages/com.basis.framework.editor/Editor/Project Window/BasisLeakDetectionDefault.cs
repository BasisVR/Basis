#if UNITY_EDITOR
using Unity.Collections;
using UnityEditor;

[InitializeOnLoad]
internal static class BasisLeakDetectionDefault
{
    private const string PrefKey = "Basis_LeakDetection_ForceStackTrace";

    public static bool Enabled
    {
        get => EditorPrefs.GetBool(PrefKey, true);
        set
        {
            EditorPrefs.SetBool(PrefKey, value);
            Apply();
        }
    }

    static BasisLeakDetectionDefault()
    {
        Apply();
    }

    private static void Apply()
    {
        NativeLeakDetection.Mode = Enabled
            ? NativeLeakDetectionMode.EnabledWithStackTrace
            : NativeLeakDetectionMode.Disabled;
    }
}
#endif
