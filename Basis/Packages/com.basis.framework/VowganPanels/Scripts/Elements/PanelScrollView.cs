using System;
using UnityEngine;

namespace Basis.VowganUI
{
    public class PanelScrollView : AddressableUIInstanceBase
    {
        public static class Styles
        {
            public static string Vertical => "BasisUI/CanvasElement/ScrollViewVertical";
            public static string Horizontal => "BasisUI/CanvasElement/ScrollViewHorizontal";
        }

        public LayoutContainer Layout;

        public static PanelScrollView CreateNew(Component parent, LayoutDirection direction)
        {
            switch (direction)
            {
                case LayoutDirection.Vertical:
                    return CreateNew<PanelScrollView>(Styles.Vertical, parent);
                case LayoutDirection.Horizontal:
                    return CreateNew<PanelScrollView>(Styles.Horizontal, parent);

                default:
                    throw new ArgumentOutOfRangeException(nameof(direction), direction, null);
            }
        }
    }
}
