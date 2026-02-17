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
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Button.prefab";
            public static string Tab => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Button - Tab Variant.prefab";
            public static string Hotbar => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Button - Hotbar Variant.prefab";
            public static string Avatar => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Button - Avatar Variant.prefab";

            public static string Prop => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Button - Avatar Variant.prefab";
            public static string AcceptButton => "Packages/com.basis.sdk/Prefabs/Panel Elements/Button Yes Variant.prefab";

            public static string CancelButton => "Packages/com.basis.sdk/Prefabs/Panel Elements/Cancel Button Variant.prefab";

            public static string ExitButton => "Packages/com.basis.sdk/Prefabs/Panel Elements/Close Button.prefab";
            public static string ExitButtonOverlay => "Packages/com.basis.sdk/Prefabs/Panel Elements/Close Button - Modal.prefab";
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
        public LayoutElement Layout
        {
            get
            {
                if (!_layout) _layout = GetComponent<LayoutElement>();
                return _layout;
            }
        }
        private LayoutElement _layout;
        public void SetSize(Vector2 size)
        {
            rectTransform.sizeDelta = size;

            Layout.minWidth = size.x;
            Layout.minHeight = size.y;
            Layout.preferredWidth = size.x;
            Layout.preferredHeight = size.y;
        }

        public void SetHeight(float height) => SetSize(new Vector2(rectTransform.sizeDelta.x, height));
        public void SetWidth(float width) => SetSize(new Vector2(rectTransform.sizeDelta.x, width));

    }
}
