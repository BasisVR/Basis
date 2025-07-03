using System.Collections.Generic;
using static Basis.Scripts.Virtual_keyboard.KeyboardLayoutData;
namespace Basis.Scripts.Virtual_keyboard.Editor
{
public partial class KeyboardLayoutDataEditor
{
    public class BasisVirtualKeyboardDefaultLanguagesAndStyles
    {
        // If you have default items you want to add after clearing, you can define a method like this:
        public static List<LanguageStyle> DefaultLanguagesAndStyles()
        {
            return new List<LanguageStyle>()
            {
                new LanguageStyle()
                {
                    language = "English",
                    style = "QWERTY",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" } },
                        new RowCollection() { innerCollection = new List<string> { "A", "S", "D", "F", "G", "H", "J", "K", "L" } },
                        new RowCollection() { innerCollection = new List<string> { "Z", "X", "C", "V", "B", "N", "M" } },
                        new RowCollection() { innerCollection = new List<string> { " " } }
                    }
                },
                new LanguageStyle()
                {
                    language = "Mandarin Chinese",
                    style = "Pinyin",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" } },
                        new RowCollection() { innerCollection = new List<string> { "A", "S", "D", "F", "G", "H", "J", "K", "L" } },
                        new RowCollection() { innerCollection = new List<string> { "Z", "X", "C", "V", "B", "N", "M" } }
                    }
                },
                new LanguageStyle()
                {
                    language = "Hindi",
                    style = "Inscript",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "अ", "आ", "इ", "ई", "उ", "ऊ", "ऋ", "ए", "ऐ", "ओ", "औ" } },
                        new RowCollection() { innerCollection = new List<string> { "क", "ख", "ग", "घ", "च", "छ", "ज", "झ", "ञ" } },
                        new RowCollection() { innerCollection = new List<string> { "ट", "ठ", "ड", "ढ", "ण" } },
                        new RowCollection() { innerCollection = new List<string> { "त", "थ", "द", "ध", "न", "प", "फ", "ब", "भ", "म", "य", "र", "ल", "व", "श", "ष", "स", "ह" } }
                    }
                },
                new LanguageStyle()
                {
                    language = "Spanish",
                    style = "QWERTY",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" } },
                        new RowCollection() { innerCollection = new List<string> { "A", "S", "D", "F", "G", "H", "J", "K", "L", "Ñ" } },
                        new RowCollection() { innerCollection = new List<string> { "Z", "X", "C", "V", "B", "N", "M" } }
                    }
                },
                new LanguageStyle()
                {
                    language = "French",
                    style = "AZERTY",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "A", "Z", "E", "R", "T", "Y", "U", "I", "O", "P" } },
                        new RowCollection() { innerCollection = new List<string> { "Q", "S", "D", "F", "G", "H", "J", "K", "L", "M" } },
                        new RowCollection() { innerCollection = new List<string> { "W", "X", "C", "V", "B", "N" } },
                        new RowCollection() { innerCollection = new List<string> { " " } }
                    }
                },
                new LanguageStyle()
                {
                    language = "Standard Arabic",
                    style = "Arabic",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "ا", "ت", "ن", "م", "ك", "ط", "ظ", "ذ", "ء", "ئ", "ؤ", "ر", "ى", "ة", "و", "ز", "ح" } },
                        new RowCollection() { innerCollection = new List<string> { "ض", "ص", "ث", "ق", "ف", "غ", "ع", "ه", "خ" } },
                        new RowCollection() { innerCollection = new List<string> { "ش", "س", "ي", "ب", "ل" } },
                        new RowCollection() { innerCollection = new List<string> { " " } }
                    }
                },
                new LanguageStyle()
                {
                    language = "Bengali",
                    style = "Probhat",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "অ", "আ", "ই", "ঈ", "উ", "ঊ", "ঋ", "এ", "ঐ", "ও", "ঔ" } },
                        new RowCollection() { innerCollection = new List<string> { "ক", "খ", "গ", "ঘ", "ঙ", "চ", "ছ", "জ", "ঝ", "ঞ" } },
                        new RowCollection() { innerCollection = new List<string> { "ট", "ঠ", "ড", "ঢ", "ণ" } },
                        new RowCollection() { innerCollection = new List<string> { "ত", "থ", "দ", "ধ", "ন", "প", "ফ", "ব", "ভ", "ম", "য", "র", "ল", "শ", "ষ", "স", "হ" } }
                    }
                },
                new LanguageStyle()
                {
                    language = "Portuguese",
                    style = "QWERTY",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" } },
                         new RowCollection() { innerCollection = new List<string> { "A", "S", "D", "F", "G", "H", "J", "K", "L", "Ç" } },
                        new RowCollection() { innerCollection = new List<string> { "Z", "X", "C", "V", "B", "N", "M" } }
                    }
                },
                new LanguageStyle()
                {
                    language = "Russian",
                    style = "ЙЦУКЕН",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "Й", "Ц", "У", "К", "Е", "Н", "Г", "Ш", "Щ", "З" } },
                        new RowCollection() { innerCollection = new List<string> { "Х", "Ъ", "Ф", "Ы", "В", "А", "П", "Р", "О", "Л" } },
                        new RowCollection() { innerCollection = new List<string> { "Д", "Ж", "Э" } },
                        new RowCollection() { innerCollection = new List<string> { "Я", "Ч", "С", "М", "И", "Т", "Ь", "Б", "Ю" } }
                    }
                },
                new LanguageStyle()
                {
                    language = "Urdu",
                    style = "Urdu Phonetic",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { " ", "ق", "ک", "گ", "ل", "م", "ن", "و", "ہ", "ء", "ی", "ے", "ئ" } },
                        new RowCollection() { innerCollection = new List<string> { "ر", "ڑ", "ز", "ژ", "س", "ش", "ص", "ض", "ط", "ظ", "ع", "غ", "ف" } },
                        new RowCollection() { innerCollection = new List<string> { "ا", "ب", "پ", "ت", "ٹ", "ث", "ج", "چ", "ح", "خ", "د", "ڈ", "ذ" } },
                        new RowCollection() { innerCollection = new List<string> { " " } }
                    }
                },
                new LanguageStyle()
                {
                    language = "German",
                    style = "QWERTZ",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "Q", "W", "E", "R", "T", "Z", "U", "I", "O", "P", "Ü" } },
                        new RowCollection() { innerCollection = new List<string> { "A", "S", "D", "F", "G", "H", "J", "K", "L", "Ö", "Ä" } },
                        new RowCollection() { innerCollection = new List<string> { "Y", "X", "C", "V", "B", "N", "M" } }
                    }
                }
                // Add more language styles as needed
            };
        }
    }
}
}