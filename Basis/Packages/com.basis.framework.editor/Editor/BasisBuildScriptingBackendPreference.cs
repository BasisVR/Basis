#if UNITY_EDITOR
using UnityEditor;

public static class BasisBuildScriptingBackendPreference
{
    public enum Mode
    {
        Ask = 0,
        Mono = 2,
        CoreCLR = 3,
    }

    private const string PrefKey = "Basis_Build_ScriptingBackend_Mode";

    public static Mode Current
    {
        get
        {
            Mode mode = (Mode)EditorPrefs.GetInt(PrefKey, (int)Mode.Ask);
            return mode == Mode.Mono || mode == Mode.CoreCLR ? mode : Mode.Ask;
        }
        set => EditorPrefs.SetInt(PrefKey, (int)value);
    }
}
#endif
