using Basis.BTween;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class PanelToggle : PanelDataComponent<bool>
    {
        public static class Styles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Toggle.prefab";
            public static string Entry => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Toggle - Entry Variant.prefab";
        }

        public Toggle ToggleComponent;
        public RectTransform ToggleVisual;

        [Header("Visual Elements")]
        public Graphic Background;
        [Min(0)] public float ToggleVisualOffset = 20f;

        [Header("Tween Settings")]
        [Min(0)] public float TweenDuration = 0.2f;
        public Color OffColor = new Color(0.4f, 0.4f, 0.4f, 1f);
        public Color OnColor = new Color(0.2f, 0.8f, 0.4f, 1f);
        private TweenAnchorPosition _toggleTween;
        private TweenGraphicColor _backgroundTween;

        private bool _initialized;


        private PanelToggle() { }

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

            SetVisualInstant(binding.RawValue); // Instant slide + color
        }

        public override void OnComponentUsed()
        {
            base.OnComponentUsed();
            _initialized = true;
            SetValue(ToggleComponent.isOn);
        }

        protected override void ApplyValue()
        {
            base.ApplyValue();

            float targetX = Value ? ToggleVisualOffset : -ToggleVisualOffset;
            Color targetColor = Value ? OnColor : OffColor;

            if (!Application.isPlaying || !_initialized || TweenDuration <= 0f)
            {
                SetVisualInstant(Value);
                return;
            }



            if (ToggleVisual)
            {
                if (_toggleTween && _toggleTween.Active) _toggleTween.Finish();
                _toggleTween = ToggleVisual.TweenAnchorPosition(TweenDuration, new Vector2(targetX, 0));
            }

            if (Background)
            {
                if (_backgroundTween && _backgroundTween.Active) _backgroundTween.Finish();
                _backgroundTween = Background.TweenColor(TweenDuration, Background.color, targetColor);
            }
        }
        public override void SetValueWithoutNotify(bool value)
        {
            base.SetValueWithoutNotify(value);
            ToggleComponent.SetIsOnWithoutNotify(value);
        }

        private void SetVisualInstant(bool on)
        {
            if (ToggleVisual)
            {
                float x = on ? ToggleVisualOffset : -ToggleVisualOffset;
                Vector2 pos = ToggleVisual.anchoredPosition;
                ToggleVisual.anchoredPosition = new Vector2(x, pos.y);
            }

            if (Background)
            {
                Background.color = on ? OnColor : OffColor;
            }
        }
    }
}
