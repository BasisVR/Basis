using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    [RequireComponent(typeof(LayoutElement))]
    public class PanelElement : AddressableUIInstanceBase
    {
        public TextMeshProUGUI TitleLabel;
        public RectTransform ContentParent;

        public LayoutElement Layout
        {
            get
            {
                if (!_layout) _layout = GetComponent<LayoutElement>();
                return _layout;
            }
        }
        private LayoutElement _layout;

        public void SetTitle(string title)
        {
            TitleLabel.text = title;
        }

        public void SetActive(bool value)
        {
            gameObject.SetActive(value);
        }

        public void ForceRebuild()
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
        }
    }
}
