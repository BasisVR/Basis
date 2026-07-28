using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Basis.Localization
{
    /// <summary>
    /// Stateless localization helpers shared by the runtime
    /// <c>Basis.BasisUI.BasisLocalization</c> and the editor-time
    /// <c>Basis.Editor.Localization.BasisEditorLocalization</c>. Each keeps its own
    /// table state and loading backend; only the OS-locale mapping, the JSON table
    /// schema, and the format helper live here so the two stay in lockstep.
    /// </summary>
    public static class BasisLocalizationCore
    {
        /// <summary>
        /// Maps <see cref="Application.systemLanguage"/> to one of the codes in
        /// <paramref name="availableCodes"/>. Unknown or unavailable system
        /// languages return <paramref name="defaultLanguage"/>. A region-specific
        /// candidate ("zh-Hans") also matches a language-only file ("zh").
        /// </summary>
        public static string ResolveSystemLanguage(SystemLanguage systemLanguage, IReadOnlyList<string> availableCodes, string defaultLanguage)
        {
            string candidate;
            switch (systemLanguage)
            {
                case SystemLanguage.Japanese: candidate = "ja"; break;
                case SystemLanguage.Korean: candidate = "ko"; break;
                case SystemLanguage.ChineseSimplified or SystemLanguage.Chinese: candidate = "zh-Hans"; break;
                case SystemLanguage.ChineseTraditional: candidate = "zh-Hant"; break;
                case SystemLanguage.French: candidate = "fr"; break;
                case SystemLanguage.German: candidate = "de"; break;
                case SystemLanguage.Spanish: candidate = "es"; break;
                case SystemLanguage.Portuguese: candidate = "pt"; break;
                case SystemLanguage.Italian: candidate = "it"; break;
                case SystemLanguage.Russian: candidate = "ru"; break;
                case SystemLanguage.Dutch: candidate = "nl"; break;
                case SystemLanguage.Polish: candidate = "pl"; break;
                case SystemLanguage.Turkish: candidate = "tr"; break;
                case SystemLanguage.Arabic: candidate = "ar"; break;
                case SystemLanguage.Swedish: candidate = "sv"; break;
                case SystemLanguage.Norwegian: candidate = "no"; break;
                case SystemLanguage.Danish: candidate = "da"; break;
                case SystemLanguage.Finnish: candidate = "fi"; break;
                case SystemLanguage.Czech: candidate = "cs"; break;
                case SystemLanguage.Hungarian: candidate = "hu"; break;
                case SystemLanguage.Greek: candidate = "el"; break;
                case SystemLanguage.Hebrew: candidate = "he"; break;
                case SystemLanguage.Vietnamese: candidate = "vi"; break;
                case SystemLanguage.Thai: candidate = "th"; break;
                case SystemLanguage.Ukrainian: candidate = "uk"; break;
                case SystemLanguage.Indonesian: candidate = "id"; break;
                case SystemLanguage.Romanian: candidate = "ro"; break;
                case SystemLanguage.Bulgarian: candidate = "bg"; break;
                case SystemLanguage.Catalan: candidate = "ca"; break;
                case SystemLanguage.SerboCroatian: candidate = "sh"; break;
                case SystemLanguage.Slovak: candidate = "sk"; break;
                case SystemLanguage.Slovenian: candidate = "sl"; break;
                case SystemLanguage.Estonian: candidate = "et"; break;
                case SystemLanguage.Latvian: candidate = "lv"; break;
                case SystemLanguage.Lithuanian: candidate = "lt"; break;
                case SystemLanguage.Icelandic: candidate = "is"; break;
                case SystemLanguage.Afrikaans: candidate = "af"; break;
                case SystemLanguage.Basque: candidate = "eu"; break;
                case SystemLanguage.Belarusian: candidate = "be"; break;
                case SystemLanguage.Faroese: candidate = "fo"; break;
                case SystemLanguage.English:
                default:
                    return defaultLanguage;
            }

            if (availableCodes != null)
            {
                for (int i = 0; i < availableCodes.Count; i++)
                {
                    if (string.Equals(availableCodes[i], candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return availableCodes[i];
                    }
                }

                int dash = candidate.IndexOf('-');
                if (dash > 0)
                {
                    string basePart = candidate.Substring(0, dash);
                    for (int i = 0; i < availableCodes.Count; i++)
                    {
                        if (string.Equals(availableCodes[i], basePart, StringComparison.OrdinalIgnoreCase))
                        {
                            return availableCodes[i];
                        }
                    }
                }
            }

            return defaultLanguage;
        }

        /// <summary>
        /// Applies <paramref name="args"/> to an already-resolved template using the
        /// invariant culture. Returns the template unchanged when there are no args
        /// or the format string is malformed.
        /// </summary>
        public static string Format(string template, object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return template;
            }

            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, args);
            }
            catch (FormatException)
            {
                return template;
            }
        }
    }

    [Serializable]
    public class BasisLanguageTable
    {
        public string code;
        public string nativeName;
        public List<BasisLanguageEntry> entries = new();
    }

    [Serializable]
    public class BasisLanguageEntry
    {
        public string key;
        public string value;
    }
}
