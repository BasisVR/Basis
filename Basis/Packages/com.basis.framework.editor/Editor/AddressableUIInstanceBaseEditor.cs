using UnityEditor;
using UnityEngine.UIElements;

namespace Basis.BasisUI.Editor
{
    [CustomEditor(typeof(AddressableUIInstanceBase), true)]
    public class AddressableUIInstanceBaseEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            return base.CreateInspectorGUI();
        }
    }
}
