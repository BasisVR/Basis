using UnityEngine;
using UnityEngine.UI;
using static UnityEngine.Rendering.DebugUI;

namespace Basis.BasisUI
{
    public class PanelImage : PanelComponent
    {
        public static class ImageStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Image.prefab";
        }
        public Image Image;
        private PanelImage() { }

        protected bool _iconIsAddressable;


        public static PanelImage CreateNew(Component parent)
            => CreateNew<PanelImage>(ImageStyles.Default, parent);

        public static PanelImage CreateNew(string style, Component parent)
            => CreateNew<PanelImage>(style, parent);


        public void SetIcon(Sprite icon, bool isAddressable)
        {
            _iconIsAddressable = isAddressable;
            Image.enabled = icon;
            Image.sprite = icon;
        }

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
        }
        public override void OnReleaseEvent()
        {
            base.OnReleaseEvent();
            if (Descriptor?.IconImage?.sprite && _iconIsAddressable) AddressableAssets.Release(Descriptor?.IconImage?.sprite);
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
