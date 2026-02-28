using System.Globalization;

namespace Basis.BasisUI
{
    public class LibraryProviderStrUtil
    {
        public static string TitleToCase(string givenText)
        {
            TextInfo textInfo = CultureInfo.CurrentCulture.TextInfo;
            return textInfo.ToTitleCase(givenText);
        }
    }

}