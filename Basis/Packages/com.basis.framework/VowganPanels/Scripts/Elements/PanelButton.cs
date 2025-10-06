using Basis.VowganUI.Styling;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Basis.VowganUI
{
    public class PanelButton : PanelElement
    {
        public static class Styles
        {
            public static string Default => "VowganUI/Elements/Button";
        }

        public Button ButtonComponent;
        public TextMeshProUGUI Label;
        public Image Icon;
        public PaletteImage Styling;
        public UnityEvent OnClicked;

        public PaletteStyle NormalStyle = PaletteStyle.ButtonColor;
        public PaletteStyle ActiveStyle = PaletteStyle.SuccessColor;

        public void UseActiveStyle(bool value)
        {
            Styling?.SetStyling(value ? ActiveStyle : NormalStyle);
        }

        public static PanelButton CreateNew(Component parent)
        {
            PanelButton button = CreateNew<PanelButton>(Styles.Default, parent);
            return button;
        }

        public static PanelButton CreateNew(Component parent, string label)
        {
            PanelButton button = CreateNew(parent);
            button.Label.text = label;
            return button;
        }

        public static PanelButton CreateNew(Component parent, string label, Sprite icon)
        {
            PanelButton button = CreateNew(parent);
            button.Label.text = label;
            button.Icon.sprite = icon;
            return button;
        }

        public static PanelButton CreateNew(Component parent, Sprite icon)
        {
            PanelButton button = CreateNew(parent);
            button.Icon.sprite = icon;
            return button;
        }

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            ButtonComponent.onClick.AddListener(OnClick);
            UseActiveStyle(false);
        }

        public virtual void OnClick()
        {
            OnClicked?.Invoke();
        }

        /// <summary>
        /// Set this button active until the given element is released.
        /// </summary>
        public void BindActiveStateToAddressablesInstance(
            IAddressableInstance instance)
        {
            UseActiveStyle(true);
            instance.OnReleased += () =>
            {
                UseActiveStyle(false);
            };
        }
    }
}
