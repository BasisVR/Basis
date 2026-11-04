using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Basis.Editor.Identity
{
    public enum BasisIdentityKind { Text, Name, ProgId, Mime, Scheme, ReverseDomain, Host, Port, Url, Extension, Slug }

    public sealed class BasisIdentityTarget
    {
        public readonly string Path;
        public readonly Regex Pattern;
        public readonly bool CSharp;
        public readonly bool Optional;
        public BasisIdentityTarget(string path, Regex pattern, bool csharp, bool optional) { Path = path; Pattern = pattern; CSharp = csharp; Optional = optional; }
        public string FullPath => System.IO.Path.Combine(BasisIdentitySettings.RepoRoot, Path);
        public bool Exists => File.Exists(FullPath);
        public string Display => Path.Replace('\\', '/');
    }

    public sealed class BasisIdentityEntry
    {
        public readonly string Id, Group;
        public readonly BasisIdentityKind Kind;
        public readonly List<BasisIdentityTarget> Targets = new List<BasisIdentityTarget>();
        public Func<string> GetSetting;
        public Action<string> SetSetting;
        public string LinkedTo;
        public BasisIdentityEntry(string id, string group, BasisIdentityKind kind) { Id = id; Group = group; Kind = kind; }
        public string LabelKey => "editor.identity.entry." + Id + ".label";
        public string NoteKey => "editor.identity.entry." + Id + ".note";
        public bool IsSetting => GetSetting != null;

        public BasisIdentityEntry Const(string path, string symbol, bool optional = false)
        {
            string value = Kind == BasisIdentityKind.Port ? @"(\d+)" : "\"((?:[^\"\\\\]|\\\\.)*)\"";
            Targets.Add(new BasisIdentityTarget(path, new Regex(@"\b" + Regex.Escape(symbol) + @"\s*=\s*" + value, RegexOptions.CultureInvariant), true, optional));
            return this;
        }

        public BasisIdentityEntry Xml(string path, string pattern, bool optional = false)
        {
            Targets.Add(new BasisIdentityTarget(path, new Regex(pattern, RegexOptions.CultureInvariant), false, optional));
            return this;
        }

        public BasisIdentityEntry Setting(Func<string> get, Action<string> set) { GetSetting = get; SetSetting = set; return this; }
        public BasisIdentityEntry Link(string id) { LinkedTo = id; return this; }
    }

    public struct BasisIdentityRead
    {
        public BasisIdentityTarget Target;
        public int Matches;
        public string Value;
        public bool Missing;
    }

    [NoAutoStaticsCleanup]
    public static class BasisIdentitySettings
    {
        public static readonly string[] Groups = { "product", "network", "files", "migration", "links" };
        public static string RepoRoot => System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", ".."));

        const string Unity = "Basis/";
        const string Framework = Unity + "Packages/com.basis.framework/";
        const string ServerSource = "Basis Server/";
        const string ServerMirror = Unity + "Packages/com.basis.server/";
        const string SetupWindow = Unity + "Packages/com.basis.framework.editor/Editor/Project Window/";
        const string SdkConstants = Unity + "Packages/com.basis.sdk/Scripts/Editor/Constants/BasisSDKConstants.cs";

        static List<BasisIdentityEntry> entries;
        public static IReadOnlyList<BasisIdentityEntry> Entries => entries ??= Build();

        static List<BasisIdentityEntry> Build()
        {
            const string NetApp = "BasisNetworkCore/Protocol/BasisNetworkApplication.cs";
            const string PortParser = "BasisNetworkCore/Transport/HostPortConnectionTargetParser.cs";
            const string Saved = Framework + "Networking/SavedServersDirectorySource.cs";
            const string NetManagement = Framework + "Networking/BasisNetworkManagement.cs";
            const string DeepLink = Framework + "Networking/BasisDeepLinkProvider.cs";
            const string BeeFiles = Framework + "Networking/BasisDeepLinkProvider.BeeFiles.cs";
            const string Migration = Unity + "Packages/com.basis.common/BasisPersistentDataMigration.cs";
            return new List<BasisIdentityEntry>
            {
                new BasisIdentityEntry("companyName", "product", BasisIdentityKind.Name).Setting(() => PlayerSettings.companyName, v => PlayerSettings.companyName = v),
                new BasisIdentityEntry("productName", "product", BasisIdentityKind.Name).Setting(() => PlayerSettings.productName, v => PlayerSettings.productName = v),
                new BasisIdentityEntry("appIdStandalone", "product", BasisIdentityKind.ReverseDomain).Setting(() => PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Standalone), v => PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, v)),
                new BasisIdentityEntry("appIdAndroid", "product", BasisIdentityKind.ReverseDomain).Setting(() => PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android), v => PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, v)),
                new BasisIdentityEntry("appIdIos", "product", BasisIdentityKind.ReverseDomain).Setting(() => PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.iOS), v => PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, v)),

                new BasisIdentityEntry("networkCompany", "network", BasisIdentityKind.Name).Link("companyName").Const(ServerMirror + NetApp, "DefaultCompanyName").Const(ServerSource + NetApp, "DefaultCompanyName", true),
                new BasisIdentityEntry("networkProduct", "network", BasisIdentityKind.Name).Link("productName").Const(ServerMirror + NetApp, "DefaultProductName").Const(ServerSource + NetApp, "DefaultProductName", true),
                new BasisIdentityEntry("serverAddress", "network", BasisIdentityKind.Host).Const(Saved, "DefaultServerAddress").Const(NetManagement, "public static string Ip"),
                new BasisIdentityEntry("serverPort", "network", BasisIdentityKind.Port).Const(Saved, "DefaultServerPort").Const(ServerMirror + PortParser, "DefaultPort").Const(ServerSource + PortParser, "DefaultPort", true),
                new BasisIdentityEntry("serverPassword", "network", BasisIdentityKind.Text).Const(Saved, "DefaultServerPassword"),
                new BasisIdentityEntry("hostServerName", "network", BasisIdentityKind.Name).Const(Unity + "Packages/com.basis.provider.servers/Runtime/ServersProvider.cs", "DefaultHostServerName").Const(NetManagement, "public static string HostServerName"),

                new BasisIdentityEntry("urlScheme", "files", BasisIdentityKind.Scheme).Const(DeepLink, "DeepLinkScheme").Xml(Unity + "Assets/Plugins/Android/AndroidManifest.xml", "android:scheme=\"([^\"]*)\"", true),
                new BasisIdentityEntry("urlBundlePrefix", "files", BasisIdentityKind.ReverseDomain).Const(DeepLink, "BundleUrlName"),
                new BasisIdentityEntry("beeOwnerProduct", "files", BasisIdentityKind.Name).Link("productName").Const(BeeFiles, "BeeFileProductName"),
                new BasisIdentityEntry("beeProgId", "files", BasisIdentityKind.ProgId).Const(BeeFiles, "BeeFileProgId"),
                new BasisIdentityEntry("beeMimeType", "files", BasisIdentityKind.Mime).Const(BeeFiles, "BeeFileMimeType"),
                new BasisIdentityEntry("backupExtension", "files", BasisIdentityKind.Extension).Const(Framework + "Settings/BasisUserDataBackup.cs", "ArchiveExtension"),

                new BasisIdentityEntry("migrationCompany", "migration", BasisIdentityKind.Name).Const(Migration, "OldCompany"),
                new BasisIdentityEntry("migrationProduct", "migration", BasisIdentityKind.Name).Const(Migration, "OldProduct"),
                new BasisIdentityEntry("migrationMarker", "migration", BasisIdentityKind.Name).Const(Migration, "MarkerName"),

                new BasisIdentityEntry("websiteUrl", "links", BasisIdentityKind.Url).Const(SetupWindow + "BasisProjectSetup.cs", "BASIS_SITE"),
                new BasisIdentityEntry("docsUrl", "links", BasisIdentityKind.Url).Const(SetupWindow + "BasisProjectSetup.cs", "BASIS_GETTING_STARTED"),
                new BasisIdentityEntry("avatarDocsUrl", "links", BasisIdentityKind.Url).Const(SetupWindow + "BasisProjectSetup.cs", "BASIS_AVATARS").Const(SdkConstants, "AvatarDocumentationURL"),
                new BasisIdentityEntry("worldDocsUrl", "links", BasisIdentityKind.Url).Const(SetupWindow + "BasisProjectSetup.cs", "BASIS_WORLDS").Const(SdkConstants, "SceneDocumentationURL"),
                new BasisIdentityEntry("propDocsUrl", "links", BasisIdentityKind.Url).Const(SdkConstants, "PropDocumentationURL"),
                new BasisIdentityEntry("donateUrl", "links", BasisIdentityKind.Url).Const(SetupWindow + "BasisProjectSetup.cs", "BASIS_DONATE"),
                new BasisIdentityEntry("openCollectiveSlug", "links", BasisIdentityKind.Slug).Const(SetupWindow + "BasisProjectSetup.Funding.cs", "OC_SLUG"),
            };
        }

        public static BasisIdentityEntry Find(string id)
        {
            foreach (BasisIdentityEntry entry in Entries) if (entry.Id == id) return entry;
            return null;
        }

        public static BasisIdentityRead Read(BasisIdentityTarget target)
        {
            BasisIdentityRead read = new BasisIdentityRead { Target = target };
            if (!target.Exists) { read.Missing = true; return read; }
            MatchCollection matches = target.Pattern.Matches(ReadText(target.FullPath, out _));
            read.Matches = matches.Count;
            if (matches.Count == 1) read.Value = target.CSharp ? UnescapeCSharp(matches[0].Groups[1].Value) : UnescapeXml(matches[0].Groups[1].Value);
            return read;
        }

        public static List<BasisIdentityRead> ReadAll(BasisIdentityEntry entry)
        {
            List<BasisIdentityRead> reads = new List<BasisIdentityRead>(entry.Targets.Count);
            foreach (BasisIdentityTarget target in entry.Targets) reads.Add(Read(target));
            return reads;
        }

        public static string Current(BasisIdentityEntry entry)
        {
            if (entry.IsSetting) return entry.GetSetting() ?? string.Empty;
            foreach (BasisIdentityTarget target in entry.Targets)
            {
                BasisIdentityRead read = Read(target);
                if (read.Value != null) return read.Value;
            }
            return null;
        }

        public static string Validate(BasisIdentityEntry entry, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "editor.identity.invalid.empty";
            if (value != value.Trim()) return "editor.identity.invalid.whitespace";
            switch (entry.Kind)
            {
                case BasisIdentityKind.Name: return value.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ? "editor.identity.invalid.name" : null;
                case BasisIdentityKind.ProgId: return Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z0-9]+){1,2}$") && value.Length <= 39 ? null : "editor.identity.invalid.progId";
                case BasisIdentityKind.Mime: return Regex.IsMatch(value, @"^[a-z]+/[a-z0-9][a-z0-9.+-]*$") ? null : "editor.identity.invalid.mime";
                case BasisIdentityKind.Scheme: return Regex.IsMatch(value, @"^[a-z][a-z0-9+.-]*$") ? null : "editor.identity.invalid.scheme";
                case BasisIdentityKind.ReverseDomain: return Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9_-]*(\.[A-Za-z0-9_-]+)+\.?$") ? null : "editor.identity.invalid.reverseDomain";
                case BasisIdentityKind.Host: return Uri.CheckHostName(value) != UriHostNameType.Unknown ? null : "editor.identity.invalid.host";
                case BasisIdentityKind.Port: return ushort.TryParse(value, out ushort port) && port > 0 ? null : "editor.identity.invalid.port";
                case BasisIdentityKind.Url: return Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? null : "editor.identity.invalid.url";
                case BasisIdentityKind.Extension: return Regex.IsMatch(value, @"^\.[A-Za-z0-9]+$") ? null : "editor.identity.invalid.extension";
                case BasisIdentityKind.Slug: return Regex.IsMatch(value, @"^[a-z0-9][a-z0-9-]*$") ? null : "editor.identity.invalid.slug";
                default: return value.IndexOfAny(new[] { '\r', '\n' }) >= 0 ? "editor.identity.invalid.text" : null;
            }
        }

        public static List<string> Apply(IReadOnlyDictionary<BasisIdentityEntry, string> changes)
        {
            List<string> written = new List<string>();
            Dictionary<string, (string text, bool bom)> files = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
            bool settingsChanged = false;
            foreach (KeyValuePair<BasisIdentityEntry, string> change in changes)
            {
                if (Validate(change.Key, change.Value) != null) continue;
                if (change.Key.IsSetting)
                {
                    change.Key.SetSetting(change.Value);
                    settingsChanged = true;
                    continue;
                }
                foreach (BasisIdentityTarget target in change.Key.Targets)
                {
                    if (!target.Exists) continue;
                    string path = target.FullPath;
                    if (!files.TryGetValue(path, out (string text, bool bom) file)) file.text = ReadText(path, out file.bom);
                    MatchCollection matches = target.Pattern.Matches(file.text);
                    if (matches.Count != 1) continue;
                    Group group = matches[0].Groups[1];
                    string encoded = change.Key.Kind == BasisIdentityKind.Port ? change.Value : target.CSharp ? EscapeCSharp(change.Value) : EscapeXml(change.Value);
                    file.text = file.text.Substring(0, group.Index) + encoded + file.text.Substring(group.Index + group.Length);
                    files[path] = file;
                }
            }
            foreach (KeyValuePair<string, (string text, bool bom)> file in files)
            {
                File.WriteAllText(file.Key, file.Value.text, new UTF8Encoding(file.Value.bom));
                written.Add(file.Key);
            }
            if (settingsChanged) AssetDatabase.SaveAssets();
            if (written.Count > 0) AssetDatabase.Refresh();
            return written;
        }

        static string ReadText(string path, out bool bom)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return bom ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);
        }

        public static string EscapeCSharp(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        public static string UnescapeCSharp(string value) => Regex.Replace(value, @"\\(.)", m => m.Groups[1].Value);
        static string EscapeXml(string value) => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
        static string UnescapeXml(string value) => value.Replace("&quot;", "\"").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
    }
}
