using UnityEngine;

namespace Basis.BasisUI.StylingOLD
{
    public abstract class StyleComponent : MonoBehaviour
    {

        public bool UseActiveStyle;

        public PaletteStyle NormalStyle;
        public PaletteStyle ActiveStyle;

        public void SetActiveState(bool value)
        {
            UseActiveStyle = value;
            ApplyColor();
        }

        public abstract void ApplyColor();

#if UNITY_EDITOR
        private void OnValidate()
        {
            ApplyColor();
        }
#endif

    }
}
