using System;
using Basis.BTween;
using Basis.BasisUI.Styling;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class PanelButton : PanelComponent, IPointerDownHandler, IPointerUpHandler
    {
        private bool _isHovered;
        private PanelIconHover _iconHover;

        public override void OnPointerEnter(PointerEventData eventData)
        {
            base.OnPointerEnter(eventData);
            if (_isHovered) return;
            _isHovered = true;
            UIAnimations.HoverLift(transform);
            _iconHover?.SetHovered(true);
        }

        public override void OnPointerExit(PointerEventData eventData)
        {
            base.OnPointerExit(eventData);
            if (!_isHovered) return;
            _isHovered = false;
            UIAnimations.HoverReset(transform);
            _iconHover?.SetHovered(false);
        }

        /// <summary>
        /// Adds the <see cref="PanelIconHover"/> pop to this button's icon. Opt-in, so buttons that
        /// are built around their icon can use it without every dense panel row moving as well.
        /// </summary>
        public void EnableIconHoverAnimation()
        {
            _iconHover ??= new PanelIconHover();
            _iconHover.Bind(Descriptor);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            _isHovered = false;
            _iconHover?.Cancel();
        }
        public static class ButtonStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Button.prefab";
            public static string Tab => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Button - Tab Variant.prefab";
            public static string Hotbar => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Button - Hotbar Variant.prefab";
            public static string Avatar => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Button - Avatar Variant.prefab";
            public static string Prop => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Button - Library Variant.prefab";
            public static string StandardButton => "Packages/com.basis.sdk/Prefabs/Panel Elements/Button Standard Variant.prefab";
            public static string AcceptButton => "Packages/com.basis.sdk/Prefabs/Panel Elements/Button Yes Variant.prefab";
            public static string CancelButton => "Packages/com.basis.sdk/Prefabs/Panel Elements/Cancel Button Variant.prefab";
            public static string ExitButton => "Packages/com.basis.sdk/Prefabs/Panel Elements/Close Button.prefab";
            public static string ExitButtonOverlay => "Packages/com.basis.sdk/Prefabs/Panel Elements/Close Button - Modal.prefab";
        }

        private PanelButton() { }

        public Button ButtonComponent;
        public UiStyleButton ButtonStyling;

        protected override Selectable InteractableTarget => ButtonComponent;
        public Action OnClicked;
        public Action OnPressed;
        public Action OnReleased;

        /// <summary>
        /// Optional "put this back" action. Assigning it opts the button into the shared reset
        /// gesture — right click on desktop, thumbstick click in VR — the same way a slider or
        /// toggle offers its default back. See <see cref="BasisPanelResetGesture"/>.
        /// </summary>
        public Action OnResetRequested;

        protected bool _iconIsAddressable;

        public override bool HasResetDefault => OnResetRequested != null;

        public override void RequestReset() => OnResetRequested?.Invoke();


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
            UIAnimations.PunchScale(transform);
            OnClicked?.Invoke();
        }

        public virtual void OnPointerDown(PointerEventData eventData)
        {
            if (!IsInteractable) return;
            OnPressed?.Invoke();
        }

        public virtual void OnPointerUp(PointerEventData eventData)
        {
            OnReleased?.Invoke();
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
        public void SetWidth(float width) => SetSize(new Vector2(width, rectTransform.sizeDelta.y));

    }
}
