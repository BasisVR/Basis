#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Basis.VowganUI.Styling
{
    public static class StyleUtilities
    {
        public static void RecordUndo(UnityEngine.Object obj, string name)
        {
#if UNITY_EDITOR
            Undo.RecordObject(obj, name);
#endif
        }
    }
}
