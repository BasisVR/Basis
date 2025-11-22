using UnityEngine;

namespace Basis.BasisUI.Styling
{
    public class UiStyleIDAttribute : PropertyAttribute
    {
        public StyleComponentType Type;

        public UiStyleIDAttribute(StyleComponentType type)
        {
            Type = type;
        }
    }
}