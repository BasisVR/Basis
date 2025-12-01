using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class PanelToggle : PanelDataComponent<bool>
    {
        public static class Styles
        {
            public static string Default => "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/PE Toggle.prefab";
            public static string Entry => "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/PE Toggle - Entry Variant.prefab";
        }

        public Toggle ToggleComponent;
        public RectTransform ToggleVisual;
        [Min(0)] public float ToggleVisualOffset = 20;

        private PanelToggle(){}

        public static PanelToggle CreateNew(Component parent) =>
            CreateNew<PanelToggle>(Styles.Default, parent);

        public static PanelToggle CreateNewEntry(Component parent) =>
            CreateNew<PanelToggle>(Styles.Entry, parent);

        public static PanelToggle CreateNew(Component parent, string style) =>
            CreateNew<PanelToggle>(style, parent);


        public override void AssignBinding(BasisSettingsBinding<bool> binding)
        {
            base.AssignBinding(binding);
            ToggleComponent.SetIsOnWithoutNotify(binding.RawValue);
        }


        public override void OnComponentUsed()
        {
            base.OnComponentUsed();
            SetValue(ToggleComponent.isOn);
        }

        protected override void ApplyValue()
        {
            base.ApplyValue();
            ToggleVisual.anchoredPosition = new Vector2(Value ? ToggleVisualOffset : -ToggleVisualOffset, 0);
        }
    }
}
