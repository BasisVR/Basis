using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{    
    // I absolutely hate everything about this
    // but TMP does not expose the TMP_SelectionCaret and sets the default material to unity default UI material
    // and does not derive it from font asset
    // https://docs.unity3d.com/Packages/com.unity.textmeshpro@3.2/api/TMPro.TMP_SelectionCaret.html
    [ExecuteAlways] // Enables the callback in both Play and Edit modes
    public class TMP_CaretMaterialSetter : MonoBehaviour
    {
        [SerializeField] private Material desiredMaterial;

        void OnTransformChildrenChanged()
        {
            // Look for TMP_SelectionCaret among children
            var caret = GetComponentInChildren<TMP_SelectionCaret>(true);
            if (caret != null)
            {
                //BasisDebug.LogWarning("caret is found setting material");
                caret.material = desiredMaterial;

                // Job done — remove this component
                Destroy(this);
            }
            
        }
    }
}
