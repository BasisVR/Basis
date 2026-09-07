using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOLocalizationTests
    {
        private const string EnglishPath = "Packages/com.basis.framework/BasisUI/Localization/Languages/en.json";
        private const string SettingsProviderPath = "Packages/com.basis.framework/BasisUI/Menus/Main Menu Providers/SettingsProvider.cs";

        [System.Serializable]
        private sealed class Entry
        {
            public string key;
            public string value;
        }

        [System.Serializable]
        private sealed class Language
        {
            public string code;
            public string nativeName;
            public List<Entry> entries;
        }

        private static Language LoadEnglish()
        {
            if (!File.Exists(EnglishPath))
                Assert.Ignore($"{EnglishPath} is not present, so the framework is not in this project.");

            Language language = JsonUtility.FromJson<Language>(File.ReadAllText(EnglishPath));
            Assert.IsNotNull(language, "en.json did not parse.");
            Assert.IsNotNull(language.entries, "en.json has no entries array.");
            return language;
        }

        private static Dictionary<string, string> EnglishKeys()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            foreach (Entry entry in LoadEnglish().entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.key))
                    continue;
                map[entry.key] = entry.value;
            }
            return map;
        }

        [Test]
        public void EveryKeyTheSettingsPanelAsksForExists()
        {
            if (!File.Exists(SettingsProviderPath))
                Assert.Ignore($"{SettingsProviderPath} is not present, so the framework is not in this project.");

            Dictionary<string, string> english = EnglishKeys();
            string source = File.ReadAllText(SettingsProviderPath);

            HashSet<string> requested = new HashSet<string>();
            foreach (Match match in Regex.Matches(source, "\"(settings\\.(?:graphics|developer)\\.rtao[A-Za-z.]*)\""))
                requested.Add(match.Groups[1].Value);

            Assert.Greater(requested.Count, 0, "The settings panel does not reference any RTAO strings, so the section never landed.");

            List<string> missing = new List<string>();
            foreach (string key in requested)
            {
                if (!english.ContainsKey(key))
                    missing.Add(key);
            }

            Assert.IsEmpty(missing, $"The settings panel asks for keys that en.json does not have: {string.Join(", ", missing)}");
        }

        [Test]
        public void EveryRtaoStringHasText()
        {
            Dictionary<string, string> english = EnglishKeys();
            List<string> empty = new List<string>();

            foreach (KeyValuePair<string, string> pair in english)
            {
                if (!pair.Key.StartsWith("settings.graphics.rtao.") && !pair.Key.StartsWith("settings.developer.rtao"))
                    continue;
                if (string.IsNullOrWhiteSpace(pair.Value))
                    empty.Add(pair.Key);
            }

            Assert.IsEmpty(empty, $"These RTAO strings are blank: {string.Join(", ", empty)}");
        }

        [Test]
        public void TheDebugViewLivesOnTheDeveloperTab()
        {
            Dictionary<string, string> english = EnglishKeys();

            Assert.IsTrue(english.ContainsKey("settings.developer.rtaoDebug"),
                "Show Occlusion Buffer is a developer tool, so its string belongs under settings.developer.");
            Assert.IsFalse(english.ContainsKey("settings.graphics.rtao.debugView"),
                "The old graphics tab string should be gone, or the key lingers with nothing rendering it.");

            if (!File.Exists(SettingsProviderPath))
                Assert.Ignore($"{SettingsProviderPath} is not present.");

            string source = File.ReadAllText(SettingsProviderPath);
            Assert.IsTrue(source.Contains("settings.developer.rtaoDebug"), "The developer tab does not render the toggle.");
            Assert.IsTrue(english.ContainsKey("settings.developer.rtaoSkinnedBudget"),
                "The avatar re-pose budget is a tuning knob, so it lives on the developer tab and needs its string there.");
            Assert.IsFalse(source.Contains("settings.graphics.rtao.debugView"), "The graphics tab still renders the toggle.");
        }

        [Test]
        public void OcclusionSourceIsHiddenWhereItCannotBeHonoured()
        {
            if (!File.Exists(SettingsProviderPath))
                Assert.Ignore($"{SettingsProviderPath} is not present.");

            string source = File.ReadAllText(SettingsProviderPath);
            Assert.IsTrue(source.Contains("dropdownRtaoMode.Descriptor.SetActive(val && rtaoCanTrace)"),
                "On a GPU with no ray tracing every entry in the source dropdown resolves the same way, so the row must be hidden rather than offering a choice that does nothing.");
            Assert.IsTrue(source.Contains("BasisRTAOContext.HardwareSupported"),
                "The panel must ask the package whether this device can trace rather than assuming.");
        }

        [Test]
        public void DropdownEntriesHaveAStringForEveryOption()
        {
            Dictionary<string, string> english = EnglishKeys();

            string[] required =
            {
                "settings.graphics.rtao.mode.rayTraced",
                "settings.graphics.rtao.mode.screenSpace",
                "settings.graphics.rtao.denoise.standard",
                "settings.graphics.rtao.denoise.high",
                "settings.graphics.rtao.denoise.maximum",
                "settings.graphics.rtao.otherCameras",
                "settings.graphics.rtao.skinned.off",
                "settings.graphics.rtao.skinned.proxy"
            };

            foreach (string key in required)
                Assert.IsTrue(english.ContainsKey(key), $"{key} is missing, so that dropdown row would render as its raw key.");
        }

        [Test]
        public void DenoiseDropdownTextMatchesWhatTheParserAccepts()
        {
            Dictionary<string, string> english = EnglishKeys();

            Assert.AreEqual(1, BasisRTAOSettingsMap.ReadDenoisePasses(english["settings.graphics.rtao.denoise.standard"]));
            Assert.AreEqual(2, BasisRTAOSettingsMap.ReadDenoisePasses(english["settings.graphics.rtao.denoise.high"]));
            Assert.AreEqual(3, BasisRTAOSettingsMap.ReadDenoisePasses(english["settings.graphics.rtao.denoise.maximum"]));
        }

        [Test]
        public void ModeDropdownTextMatchesWhatTheParserAccepts()
        {
            Dictionary<string, string> english = EnglishKeys();

            Assert.AreEqual(BasisRTAOTracingMode.RayTracedOnly, BasisRTAOSettingsMap.ReadMode(english["settings.graphics.rtao.mode.rayTraced"]));
            Assert.AreEqual(BasisRTAOTracingMode.ScreenSpace, BasisRTAOSettingsMap.ReadMode(english["settings.graphics.rtao.mode.screenSpace"]));
        }

        /// <summary>
        /// The dropdown registers stored values and localized labels as two separate lists, and it is the
        /// VALUE that reaches the parser - so that is what has to round trip. The label is free to read as
        /// plain English ("On") without the parser ever seeing it.
        /// </summary>
        [Test]
        public void SkinnedDropdownValuesMatchWhatTheParserAccepts()
        {
            Dictionary<string, string> english = EnglishKeys();
            Assert.IsTrue(english.ContainsKey("settings.graphics.rtao.skinned.off"), "The Off row lost its label.");
            Assert.IsTrue(english.ContainsKey("settings.graphics.rtao.skinned.proxy"), "The avatars-on row lost its label.");

            Assert.AreEqual(BasisRTAOSkinnedMode.Off, BasisRTAOSettingsMap.ReadSkinnedMode("Off"));
            Assert.AreEqual(BasisRTAOSkinnedMode.Proxy, BasisRTAOSettingsMap.ReadSkinnedMode("Proxy"));
        }

        [Test]
        public void EveryBindingKeyIsLowercase()
        {
            string defaultsPath = "Packages/com.basis.framework/BasisUI/BasisSettingsDefaults.cs";
            if (!File.Exists(defaultsPath))
                Assert.Ignore($"{defaultsPath} is not present.");

            string source = File.ReadAllText(defaultsPath);
            MatchCollection matches = Regex.Matches(source, "RayTracedAmbientOcclusion\\w*\\s*=\\s*new\\(\"([^\"]+)\"");
            Assert.Greater(matches.Count, 0, "No RTAO bindings were found in BasisSettingsDefaults.");

            foreach (Match match in matches)
            {
                string key = match.Groups[1].Value;
                Assert.AreEqual(key.ToLowerInvariant(), key,
                    $"Binding key '{key}' has uppercase characters. The settings system lowercases incoming names, so it would never match.");
            }
        }

        [Test]
        public void SettingsDefaultsRegisterEveryRtaoBindingForLoading()
        {
            string defaultsPath = "Packages/com.basis.framework/BasisUI/BasisSettingsDefaults.cs";
            if (!File.Exists(defaultsPath))
                Assert.Ignore($"{defaultsPath} is not present.");

            string source = File.ReadAllText(defaultsPath);

            HashSet<string> declared = new HashSet<string>();
            foreach (Match match in Regex.Matches(source, "BasisSettingsBinding<\\w+>\\s+(RayTracedAmbientOcclusion\\w*|UseRayTracedAmbientOcclusion|DevRtaoDebugView)\\s*="))
                declared.Add(match.Groups[1].Value);

            Assert.Greater(declared.Count, 0, "No RTAO bindings were declared.");

            List<string> unloaded = new List<string>();
            foreach (string name in declared)
            {
                if (!source.Contains(name + ".LoadBindingValue();"))
                    unloaded.Add(name);
            }

            Assert.IsEmpty(unloaded,
                $"These bindings are never loaded in LoadAll, so they would read their default forever: {string.Join(", ", unloaded)}");
        }
    }
}
