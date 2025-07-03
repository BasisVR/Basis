using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UI;

namespace Basis.Scripts.Virtual_keyboard
{
[Serializable]
[CreateAssetMenu(fileName = "Data", menuName = "Basis/KeyboardLayoutData", order = 1)]
public class KeyboardLayoutData : ScriptableObject
{
    [System.Serializable]
    public class LanguageStyle
    {
        public string language;
        public string style;
        [SerializeField]
        public List<RowCollection> rows = new List<RowCollection>();
        [SerializeField]
        public List<SpecialKeySizes> SpecialKeys = new List<SpecialKeySizes>();
    }
    [Serializable]
    public struct RowCollection
    {
        public List<string> innerCollection;
    }

    [SerializeField]
    public List<LanguageStyle> languagesAndStyles = new List<LanguageStyle>();
    /// <summary>
    /// Retrieves a LanguageStyle object based on language and style.
    /// </summary>
    /// <param name="language">The language name to search for.</param>
    /// <param name="style">The style name to search for.</param>
    /// <returns>The LanguageStyle object if found; otherwise, null.</returns>
    public LanguageStyle GetLanguageStyle(string language, string style)
    {
        foreach (var langStyle in languagesAndStyles)
        {
            if (langStyle.language == language && langStyle.style == style)
            {
                return langStyle;
            }
        }
        return languagesAndStyles.FirstOrDefault();
    }
    [System.Serializable]
    public struct SpecialKeySizes
    {
        public string Match;
        public float WidthSize;
        public ColorBlock ColorBlock;
        public BasisVirtualKeyboardSpecialKey BasisVirtualKeyboardSpecialKey;
    }
}
}
