using System.Collections.Generic;
using UnityEngine;
using static Basis.Scripts.Virtual_keyboard.KeyboardLayoutData;
namespace Basis.Scripts.Virtual_keyboard
{
public class BasisVirtualRow
{
    public List<BasisVirtualKeyboardButton> RowButtons = new List<BasisVirtualKeyboardButton>();
    public GameObject RowObject;
    public bool SetupButton(BasisVirtualKeyboardButton button, List<SpecialKeySizes> SpecialKeys, float ScaleSize, out SpecialKeySizes SpecialKeySizes)
    {
        SetScale(button, ScaleSize); // default size
        if (SpecialKeys != null)
        {
            foreach (var SpecialKey in SpecialKeys)
            {
                if (button.Text.text.Equals(SpecialKey.Match, System.StringComparison.OrdinalIgnoreCase))
                {
                    SetScale(button, SpecialKey.WidthSize);
                    button.Button.colors = SpecialKey.ColorBlock;
                    SpecialKeySizes = SpecialKey;
                    return true;
                }
            }
        }
        SpecialKeySizes = new SpecialKeySizes();
        return false;
    }

    public void SetScale(BasisVirtualKeyboardButton button, float width)
    {
        button.ButtonRect.sizeDelta = new Vector2(width, button.ButtonRect.sizeDelta.y);
    }
}
}
