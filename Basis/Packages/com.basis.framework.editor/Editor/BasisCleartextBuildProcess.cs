using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEngine;

namespace Basis.Editor
{
    public static class BasisCleartextBuildProcess
    {
        private const string AtsKey = "NSAppTransportSecurity";
        private const string LocalNetworkingKey = "NSAllowsLocalNetworking";
        private const string UsageDescriptionKey = "NSLocalNetworkUsageDescription";

        [PostProcessBuild(51)]
        public static void OnPostprocessBuild(BuildTarget target, string buildPath)
        {
            if (!BasisNetworkingProjectSettings.instance.AllowLocalHttp) return;

            if (target == BuildTarget.iOS)
                PatchInfoPlist(Path.Combine(buildPath, "Info.plist"));
            else if (target == BuildTarget.StandaloneOSX)
                PatchInfoPlist(Path.Combine(buildPath, "Contents", "Info.plist"));
        }

        // Hand-rolled XML rather than UnityEditor.iOS.Xcode.PlistDocument so this assembly still
        // compiles on machines without the iOS build module installed.
        private static void PatchInfoPlist(string plistPath)
        {
            if (!File.Exists(plistPath)) return;

            var doc = new XmlDocument();
            var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
            using (var reader = XmlReader.Create(plistPath, readerSettings))
                doc.Load(reader);

            XmlElement rootDict = (XmlElement)doc.SelectSingleNode("plist/dict");
            if (rootDict == null) return;

            XmlElement ats = GetOrCreateDict(doc, rootDict, AtsKey);
            SetBool(doc, ats, LocalNetworkingKey, true);
            SetString(doc, rootDict, UsageDescriptionKey,
                BasisNetworkingProjectSettings.instance.LocalNetworkUsageDescription);

            doc.Save(plistPath);
            Debug.Log($"[BasisNetworking] Allowed local-network cleartext in {plistPath}");
        }

        private static XmlElement FindValue(XmlElement dict, string key)
        {
            bool keyMatched = false;
            foreach (XmlNode node in dict.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (keyMatched) return (XmlElement)node;
                if (node.Name == "key" && node.InnerText == key) keyMatched = true;
            }
            return null;
        }

        private static XmlElement GetOrCreateDict(XmlDocument doc, XmlElement parent, string key)
        {
            XmlElement existing = FindValue(parent, key);
            if (existing != null)
            {
                if (existing.Name == "dict") return existing;
                XmlElement replacement = doc.CreateElement("dict");
                parent.ReplaceChild(replacement, existing);
                return replacement;
            }
            Append(doc, parent, "key").InnerText = key;
            return Append(doc, parent, "dict");
        }

        private static void SetBool(XmlDocument doc, XmlElement dict, string key, bool value)
        {
            string tag = value ? "true" : "false";
            XmlElement existing = FindValue(dict, key);
            if (existing != null)
            {
                if (existing.Name == tag) return;
                dict.ReplaceChild(doc.CreateElement(tag), existing);
                return;
            }
            Append(doc, dict, "key").InnerText = key;
            Append(doc, dict, tag);
        }

        private static void SetString(XmlDocument doc, XmlElement dict, string key, string value)
        {
            XmlElement existing = FindValue(dict, key);
            if (existing != null && existing.Name == "string")
            {
                existing.InnerText = value;
                return;
            }
            if (existing != null)
            {
                XmlElement replacement = doc.CreateElement("string");
                replacement.InnerText = value;
                dict.ReplaceChild(replacement, existing);
                return;
            }
            Append(doc, dict, "key").InnerText = key;
            Append(doc, dict, "string").InnerText = value;
        }

        private static XmlElement Append(XmlDocument doc, XmlElement parent, string tag)
        {
            XmlElement el = doc.CreateElement(tag);
            parent.AppendChild(el);
            return el;
        }
    }

    public class BasisCleartextAndroidPostProcess : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 51;

        public void OnPostGenerateGradleAndroidProject(string gradlePath)
        {
            if (!BasisNetworkingProjectSettings.instance.AllowLocalHttp) return;

            string manifestPath = Path.Combine(gradlePath, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath)) return;

            const string androidNs = "http://schemas.android.com/apk/res/android";
            var doc = new XmlDocument();
            doc.Load(manifestPath);

            XmlElement application = doc.SelectSingleNode("/manifest/application") as XmlElement;
            if (application == null)
            {
                Debug.LogWarning("[BasisNetworking] Could not find <application> in AndroidManifest.xml.");
                return;
            }

            if (application.GetAttribute("usesCleartextTraffic", androidNs) == "true") return;

            application.SetAttribute("usesCleartextTraffic", androidNs, "true");
            doc.Save(manifestPath);
            Debug.Log($"[BasisNetworking] Allowed cleartext traffic in {manifestPath}");
        }
    }

    public class BasisInsecureHttpBuildPreprocess : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            BasisNetworkingProjectSettings settings = BasisNetworkingProjectSettings.instance;
            InsecureHttpOption desired = settings.DesiredInsecureHttpOption;
            if (PlayerSettings.insecureHttpOption == desired) return;

            settings.ApplyToPlayerSettings();
            Debug.Log($"[BasisNetworking] insecureHttpOption set to {desired} to match the Basis networking setting.");
        }
    }
}
