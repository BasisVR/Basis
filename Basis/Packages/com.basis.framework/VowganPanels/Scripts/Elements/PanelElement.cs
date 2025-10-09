using TMPro;
using UnityEngine;

namespace Basis.VowganUI
{
    public class PanelElement : AddressableUIInstanceBase
    {
        public TextMeshProUGUI TitleLabel;
        public RectTransform ContentParent;

        public static class ElementStyles
        {
            public static string Group => "VowganUI/Elements/Group";
        }

        public static PanelElement CreateGroup(Component parent)
        {
            PanelElement group = CreateNew<PanelElement>(ElementStyles.Group, parent);
            return group;
        }

        public void SetTitle(string title)
        {
            TitleLabel.text = title;
        }

    }
}
