using System;
using Basis.BasisUI.Styling;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class PanelButton : PanelComponent
    {
        public static class ButtonStyles
        {
            public static string Default => "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/PE Button.prefab";
            public static string Tab => "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/PE Button - Tab Variant.prefab";
            public static string Hotbar => "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/PE Button - Hotbar Variant.prefab";
        }

        private PanelButton() { }

        public Button ButtonComponent;
        public UiStyleButton ButtonStyling;
        public Action OnClicked;

        protected bool _iconIsAddressable;


        public static PanelButton CreateNew(Component parent)
            => CreateNew<PanelButton>(ButtonStyles.Default, parent);

        public static PanelButton CreateNew(string style, Component parent)
            => CreateNew<PanelButton>(style, parent);


        public void SetIcon(Sprite icon, bool isAddressable)
        {
            Descriptor.SetIcon(icon);
            _iconIsAddressable = isAddressable;
        }

        public void SetIcon(string iconAddress)
        {
            if (string.IsNullOrEmpty(iconAddress)) return;
            Descriptor.SetIcon(AddressableAssets.GetSprite(iconAddress));
            _iconIsAddressable = true;
        }

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            ButtonComponent.onClick.AddListener(OnClick);
        }

        public virtual void OnClick()
        {
            OnClicked?.Invoke();
        }

        /// <summary>
        /// Set this button active until the given element is released.
        /// </summary>
        public void BindActiveStateToAddressablesInstance(IAddressableInstance instance)
        {
            ButtonStyling.ShowIndicator(true);
            instance.OnInstanceReleased += () => ButtonStyling.ShowIndicator(false);
        }

        public override void OnReleaseEvent()
        {
            base.OnReleaseEvent();
            if (Descriptor.IconImage.sprite && _iconIsAddressable) AddressableAssets.Release(Descriptor.IconImage.sprite);
        }
    }
}
