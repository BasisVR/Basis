using Basis.BasisUI.Styling;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class PanelButton : PanelElement
    {
        public static class ButtonStyles
        {
            public static string Default => "Packages/com.basis.framework/BasisUI/Prefabs/Elements/Panel Button.prefab";
            public static string Tab => "Packages/com.basis.framework/BasisUI/Prefabs/Elements/Panel Button - Tab Variant.prefab";
            public static string Hotbar => "Packages/com.basis.framework/BasisUI/Prefabs/Elements/Panel Button - Hotbar Variant.prefab";
        }

        public Button ButtonComponent;
        public Image Icon;
        public StyleImage BackgroundStyling;
        public StyleImage IconStyling;
        public StyleLabel LabelStyling;
        public UnityEvent OnClicked => ButtonComponent.onClick;

        protected bool _iconIsAddressable;


        public static PanelButton CreateNew(Component parent)
            => CreateNew<PanelButton>(ButtonStyles.Default, parent);

        public static PanelButton CreateNew(string style, Component parent)
            => CreateNew<PanelButton>(style, parent);


        public void UseActiveStyle(bool value)
        {
            BackgroundStyling.SetActiveState(value);
            IconStyling.SetActiveState(value);
            LabelStyling.SetActiveState(value);
        }

        public void SetIcon(Sprite icon, bool isAddressable)
        {
            Icon.sprite = icon;
            _iconIsAddressable = isAddressable;
        }

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            OnClicked.AddListener(OnClick);
            UseActiveStyle(false);
        }

        public virtual void OnClick()
        {
            // Debug.Log($"OnClick pressed for {gameObject}", gameObject);
        }

        /// <summary>
        /// Set this button active until the given element is released.
        /// </summary>
        public void BindActiveStateToAddressablesInstance(
            IAddressableInstance instance)
        {
            UseActiveStyle(true);
            instance.OnInstanceReleased += () =>
            {
                UseActiveStyle(false);
            };
        }

        public override void OnReleaseEvent()
        {
            base.OnReleaseEvent();
            if (Icon.sprite && _iconIsAddressable) AddressableAssets.Release(Icon.sprite);
        }
    }
}
