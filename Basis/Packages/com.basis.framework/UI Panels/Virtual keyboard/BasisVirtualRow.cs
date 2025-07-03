using System.Collections.Generic;
using UnityEngine;
using static Basis.Scripts.Virtual_keyboard.KeyboardLayoutData;
namespace Basis.Scripts.Virtual_keyboard
{
public class BasisVirtualRow
{
    public List<BasisVirtualKeyboardButton> RowButtons = new List<BasisVirtualKeyboardButton>();
    public GameObject RowObject;
    public bool SetupButton(BasisVirtualKeyboardButton button, List<SpecialKeySizes> SpecialKeys, float ScaleSize,out SpecialKeySizes SpecialKeySizes)
    {
        SetScale(button, ScaleSize); // default size;
        foreach (var SpecialKey in SpecialKeys)
        {
            if (button.Text.text.ToLower() == SpecialKey.Match.ToLower())
            {
                SetScale(button, SpecialKey.WidthSize);//check for special
                button.Button.colors = SpecialKey.ColorBlock;
                SpecialKeySizes = SpecialKey;
                return true;
            }
        }
        SpecialKeySizes = new SpecialKeySizes();
        return false;
    }


    public void SetScale(BasisVirtualKeyboardButton button, float width)
    {
        button.ButtonRect.sizeDelta = new Vector2(width, button.ButtonRect.sizeDelta.y); // Adjust the width multiplier as needed
    }
}
}