using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Scripts.Virtual_keyboard
{
public struct BasisVirtualKeyboardButton
{
    public RectTransform ButtonRect;
    public TextMeshProUGUI Text;
    public Button Button;
    public BasisVirtualKeyboardSpecialKey BasisVirtualKeyboardSpecialKey;
}
public enum BasisVirtualKeyboardSpecialKey
{
   NotSpecial, IsDeleteKey, IsCaseSwitchKey,IsEnterKey,IsCloseKey,IsPasteKey
}
}
