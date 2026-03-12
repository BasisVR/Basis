using System.Collections.Generic;
using static Basis.Scripts.Virtual_keyboard.KeyboardLayoutData;
namespace Basis.Scripts.Virtual_keyboard.Editor
{
public partial class KeyboardLayoutDataEditor
{
    public class BasisVirtualKeyboardDefaultLanguagesAndStyles
    {
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
                        new RowCollection() { innerCollection = new List<string> { "Z", "X", "C", "V", "B", "N", "M" } },
                    }
                },
                new LanguageStyle()
                {
                    language = "Hindi",
                    style = "Inscript",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0905", "\u0906", "\u0907", "\u0908", "\u0909", "\u090A", "\u090B", "\u090F", "\u0910", "\u0913", "\u0914" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0915", "\u0916", "\u0917", "\u0918", "\u091A", "\u091B", "\u091C", "\u091D", "\u091E" } },
                        new RowCollection() { innerCollection = new List<string> { "\u091F", "\u0920", "\u0921", "\u0922", "\u0923" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0924", "\u0925", "\u0926", "\u0927", "\u0928", "\u092A", "\u092B", "\u092C", "\u092D", "\u092E", "\u092F", "\u0930", "\u0932", "\u0935", "\u0936", "\u0937", "\u0938", "\u0939" } },
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
                        new RowCollection() { innerCollection = new List<string> { "A", "S", "D", "F", "G", "H", "J", "K", "L", "\u00D1" } },
                        new RowCollection() { innerCollection = new List<string> { "Z", "X", "C", "V", "B", "N", "M" } },
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
                    }
                },
                new LanguageStyle()
                {
                    language = "Standard Arabic",
                    style = "Arabic",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0627", "\u062A", "\u0646", "\u0645", "\u0643", "\u0637", "\u0638", "\u0630", "\u0621", "\u0626", "\u0624", "\u0631", "\u0649", "\u0629", "\u0648", "\u0632", "\u062D" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0636", "\u0635", "\u062B", "\u0642", "\u0641", "\u063A", "\u0639", "\u0647", "\u062E" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0634", "\u0633", "\u064A", "\u0628", "\u0644" } },
                    }
                },
                new LanguageStyle()
                {
                    language = "Bengali",
                    style = "Probhat",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0985", "\u0986", "\u0987", "\u0988", "\u0989", "\u098A", "\u098B", "\u098F", "\u0990", "\u0993", "\u0994" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0995", "\u0996", "\u0997", "\u0998", "\u0999", "\u099A", "\u099B", "\u099C", "\u099D", "\u099E" } },
                        new RowCollection() { innerCollection = new List<string> { "\u099F", "\u09A0", "\u09A1", "\u09A2", "\u09A3" } },
                        new RowCollection() { innerCollection = new List<string> { "\u09A4", "\u09A5", "\u09A6", "\u09A7", "\u09A8", "\u09AA", "\u09AB", "\u09AC", "\u09AD", "\u09AE", "\u09AF", "\u09B0", "\u09B2", "\u09B6", "\u09B7", "\u09B8", "\u09B9" } },
                    }
                },
                new LanguageStyle()
                {
                    language = "Portuguese",
                    style = "QWERTY",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" } },
                        new RowCollection() { innerCollection = new List<string> { "A", "S", "D", "F", "G", "H", "J", "K", "L", "\u00C7" } },
                        new RowCollection() { innerCollection = new List<string> { "Z", "X", "C", "V", "B", "N", "M" } },
                    }
                },
                new LanguageStyle()
                {
                    language = "Russian",
                    style = "\u0419\u0426\u0423\u041A\u0415\u041D",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0419", "\u0426", "\u0423", "\u041A", "\u0415", "\u041D", "\u0413", "\u0428", "\u0429", "\u0417" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0425", "\u042A", "\u0424", "\u042B", "\u0412", "\u0410", "\u041F", "\u0420", "\u041E", "\u041B" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0414", "\u0416", "\u042D" } },
                        new RowCollection() { innerCollection = new List<string> { "\u042F", "\u0427", "\u0421", "\u041C", "\u0418", "\u0422", "\u042C", "\u0411", "\u042E" } },
                    }
                },
                new LanguageStyle()
                {
                    language = "Urdu",
                    style = "Urdu Phonetic",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0642", "\u06A9", "\u06AF", "\u0644", "\u0645", "\u0646", "\u0648", "\u06C1", "\u0621", "\u06CC", "\u06D2", "\u0626" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0631", "\u0691", "\u0632", "\u0698", "\u0633", "\u0634", "\u0635", "\u0636", "\u0637", "\u0638", "\u0639", "\u063A", "\u0641" } },
                        new RowCollection() { innerCollection = new List<string> { "\u0627", "\u0628", "\u067E", "\u062A", "\u0679", "\u062B", "\u062C", "\u0686", "\u062D", "\u062E", "\u062F", "\u0688", "\u0630" } },
                    }
                },
                new LanguageStyle()
                {
                    language = "German",
                    style = "QWERTZ",
                    rows = new List<RowCollection>()
                    {
                        new RowCollection() { innerCollection = new List<string> { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" } },
                        new RowCollection() { innerCollection = new List<string> { "Q", "W", "E", "R", "T", "Z", "U", "I", "O", "P", "\u00DC" } },
                        new RowCollection() { innerCollection = new List<string> { "A", "S", "D", "F", "G", "H", "J", "K", "L", "\u00D6", "\u00C4" } },
                        new RowCollection() { innerCollection = new List<string> { "Y", "X", "C", "V", "B", "N", "M" } },
                    }
                }
            };
        }
    }
}
}
