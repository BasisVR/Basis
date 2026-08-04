#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Cilbox;
using UnityEditor;

namespace Basis.Shims.Editor
{
    internal enum CilboxBoxKind
    {
        Avatar,
        Prop,
        Scene,
    }

    /// <summary>How a member reads to the sandbox.</summary>
    internal enum CilboxAccess
    {
        /// <summary>Reachable from a cilboxed script.</summary>
        Allowed,

        /// <summary>Reachable, but only because nothing has pinned the type's methods down.</summary>
        DefaultAllowed,

        /// <summary>Refused by the whitelists or by a hard deny.</summary>
        Blocked,

        /// <summary>The type could not be loaded, so nothing can be said about it.</summary>
        Unknown,
    }

    internal readonly struct CilboxVerdict
    {
        public readonly CilboxAccess Access;

        /// <summary>Why, in the window's own words — already localized.</summary>
        public readonly string Reason;

        public CilboxVerdict(CilboxAccess access, string reason)
        {
            Access = access;
            Reason = reason;
        }

        public bool IsAllowed => Access == CilboxAccess.Allowed || Access == CilboxAccess.DefaultAllowed;
    }

    /// <summary>One sandbox: its compiled whitelists and the file they are written in.</summary>
    internal sealed class CilboxBoxInfo
    {
        public CilboxBoxKind Kind;
        public Type BoxType;

        /// <summary>Absolute path of the box's <c>.cs</c>, or null when it could not be located.</summary>
        public string SourcePath;

        /// <summary>Project-relative path, for pinging the file in the Project window.</summary>
        public string AssetPath;

        public HashSet<string> ExtraTypes = new HashSet<string>();
        public HashSet<string> ExtraFields = new HashSet<string>();
        public Dictionary<Type, HashSet<string>> ExtraMethods = new Dictionary<Type, HashSet<string>>();

        /// <summary>
        /// An instance used only to call the box's own <c>CheckTypeAllowed</c> / <c>CheckMethodAllowed</c>.
        /// Built without running a constructor because <see cref="Cilbox.Cilbox"/> is a MonoBehaviour;
        /// those two methods read nothing but static whitelists, so an uninitialized instance answers
        /// exactly as the running sandbox does.
        /// </summary>
        public Cilbox.Cilbox Probe;

        public string LocalizationKey =>
            Kind == CilboxBoxKind.Avatar ? "sdk.cilbox.box.avatar" :
            Kind == CilboxBoxKind.Prop ? "sdk.cilbox.box.prop" :
            "sdk.cilbox.box.scene";
    }

    /// <summary>
    /// The compiled state of the three Cilbox sandboxes, read straight off the loaded assembly.
    ///
    /// <para>Everything shown in the window comes from here rather than from parsed text, and every
    /// verdict is produced by calling the box's own check methods. That is deliberate: a permissions
    /// UI that reports its own opinion of the rules is worse than none, because the one failure it
    /// hides is "the window said allowed and the sandbox said no".</para>
    /// </summary>
    internal static class BasisCilboxPermissionModel
    {
        public const string CommonSourceFile = "CilboxBasisCommon.cs";

        public const string FieldCommonTypes = "commonWhiteListType";
        public const string FieldCommonFields = "commonWhiteListFields";
        public const string FieldCommonMethods = "commonMethodWhitelist";
        public const string FieldExtraTypes = "extraWhiteListType";
        public const string FieldExtraFields = "extraWhiteListFields";
        public const string FieldExtraMethods = "extraMethodWhitelist";

        private static readonly List<CilboxBoxInfo> _boxes = new List<CilboxBoxInfo>();
        private static bool _loaded;

        public static HashSet<string> CommonTypes { get; private set; } = new HashSet<string>();
        public static HashSet<string> CommonFields { get; private set; } = new HashSet<string>();
        public static Dictionary<Type, HashSet<string>> CommonMethods { get; private set; } = new Dictionary<Type, HashSet<string>>();

        /// <summary>Absolute path of <c>CilboxBasisCommon.cs</c>, or null.</summary>
        public static string CommonSourcePath { get; private set; }

        public static string CommonAssetPath { get; private set; }

        /// <summary>Set when a whitelist field could not be read off the compiled type.</summary>
        public static string LoadError { get; private set; }

        public static IReadOnlyList<CilboxBoxInfo> Boxes
        {
            get
            {
                EnsureLoaded();
                return _boxes;
            }
        }

        public static CilboxBoxInfo Box(CilboxBoxKind kind)
        {
            foreach (CilboxBoxInfo box in Boxes)
            {
                if (box.Kind == kind) return box;
            }
            return null;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            Reload();
        }

        public static void Reload()
        {
            _loaded = true;
            LoadError = null;
            _boxes.Clear();

            CommonTypes = ReadStaticSet(typeof(CilboxBasisCommon), FieldCommonTypes) ?? new HashSet<string>();
            CommonFields = ReadStaticSet(typeof(CilboxBasisCommon), FieldCommonFields) ?? new HashSet<string>();
            CommonMethods = ReadStaticMethodMap(typeof(CilboxBasisCommon), FieldCommonMethods) ?? new Dictionary<Type, HashSet<string>>();

            CommonAssetPath = FindScriptAssetPath(nameof(CilboxBasisCommon));
            CommonSourcePath = ToAbsolute(CommonAssetPath);

            AddBox(CilboxBoxKind.Avatar, typeof(CilboxAvatarBasis), nameof(CilboxAvatarBasis));
            AddBox(CilboxBoxKind.Prop, typeof(CilboxPropBasis), nameof(CilboxPropBasis));
            AddBox(CilboxBoxKind.Scene, typeof(CilboxSceneBasis), nameof(CilboxSceneBasis));
        }

        private static void AddBox(CilboxBoxKind kind, Type type, string typeName)
        {
            var info = new CilboxBoxInfo
            {
                Kind = kind,
                BoxType = type,
                ExtraTypes = ReadStaticSet(type, FieldExtraTypes) ?? new HashSet<string>(),
                ExtraFields = ReadStaticSet(type, FieldExtraFields) ?? new HashSet<string>(),
                ExtraMethods = ReadStaticMethodMap(type, FieldExtraMethods) ?? new Dictionary<Type, HashSet<string>>(),
                AssetPath = FindScriptAssetPath(typeName),
            };
            info.SourcePath = ToAbsolute(info.AssetPath);

            try
            {
                info.Probe = FormatterServices.GetUninitializedObject(type) as Cilbox.Cilbox;
            }
            catch (Exception e)
            {
                info.Probe = null;
                LoadError = e.Message;
            }

            _boxes.Add(info);
        }

        // ------------------------------------------------------------------ reflection

        private static readonly BindingFlags StaticAny =
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy;

        private static HashSet<string> ReadStaticSet(Type owner, string fieldName)
        {
            FieldInfo field = owner.GetField(fieldName, StaticAny);
            if (field == null)
            {
                LoadError = $"{owner.Name}.{fieldName} was not found — the whitelist may have been renamed.";
                return null;
            }
            return field.GetValue(null) as HashSet<string>;
        }

        private static Dictionary<Type, HashSet<string>> ReadStaticMethodMap(Type owner, string fieldName)
        {
            FieldInfo field = owner.GetField(fieldName, StaticAny);
            if (field == null)
            {
                LoadError = $"{owner.Name}.{fieldName} was not found — the whitelist may have been renamed.";
                return null;
            }
            return field.GetValue(null) as Dictionary<Type, HashSet<string>>;
        }

        private static string FindScriptAssetPath(string typeName)
        {
            foreach (string guid in AssetDatabase.FindAssets(typeName + " t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (!Path.GetFileNameWithoutExtension(path).Equals(typeName, StringComparison.Ordinal)) continue;

                // The repo carries dev checkouts of this package under .basisdev; those copies are
                // not the ones Unity compiles, so editing them would change nothing.
                if (path.IndexOf("/.basisdev/", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                return path;
            }
            return null;
        }

        private static string ToAbsolute(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            try
            {
                string full = Path.GetFullPath(assetPath);
                return File.Exists(full) ? full : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ verdicts

        /// <summary>Whether the box lets a type name through, asking the box itself.</summary>
        public static bool IsTypeAllowed(CilboxBoxInfo box, string typeName)
        {
            if (box?.Probe == null || string.IsNullOrEmpty(typeName)) return false;
            try
            {
                return box.Probe.CheckTypeAllowed(typeName);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Whether the box lets a field on a type through.</summary>
        public static bool IsFieldAllowed(CilboxBoxInfo box, string typeName, string fieldName)
        {
            if (box?.Probe == null || string.IsNullOrEmpty(typeName)) return false;
            try
            {
                return box.Probe.CheckFieldAllowed(typeName, fieldName);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The whitelist pattern responsible for a type being allowed, so the window can say
        /// <em>why</em> rather than just yes. Returns null when nothing matched.
        /// </summary>
        public static string MatchingTypePattern(CilboxBoxInfo box, string typeName, out bool fromCommon)
        {
            fromCommon = false;
            if (string.IsNullOrEmpty(typeName)) return null;

            if (CommonTypes.Contains(typeName)) { fromCommon = true; return typeName; }
            if (box != null && box.ExtraTypes.Contains(typeName)) return typeName;

            foreach (string pattern in CommonTypes)
            {
                if (WildcardMatches(pattern, typeName)) { fromCommon = true; return pattern; }
            }
            if (box != null)
            {
                foreach (string pattern in box.ExtraTypes)
                {
                    if (WildcardMatches(pattern, typeName)) return pattern;
                }
            }
            return null;
        }

        /// <summary>The same single-star matching <c>CilboxBasisCommon.MatchesWildcard</c> performs.</summary>
        public static bool WildcardMatches(string pattern, string target)
        {
            if (pattern == null || target == null) return false;
            if (pattern.IndexOf('*') < 0) return false;
            string[] parts = pattern.Split('*');
            return target.StartsWith(parts[0], StringComparison.Ordinal)
                && target.EndsWith(parts[parts.Length - 1], StringComparison.Ordinal);
        }

        /// <summary>
        /// The method whitelist entry governing a type in this box, if any. A type that appears in
        /// no entry has its whole public surface reachable — the gate is default-allow.
        /// </summary>
        public static bool TryGetMethodPins(CilboxBoxInfo box, Type declaringType, out HashSet<string> pinned, out bool fromCommon)
        {
            pinned = null;
            fromCommon = false;
            if (declaringType == null) return false;

            bool found = false;
            var union = new HashSet<string>();

            if (CommonMethods.TryGetValue(declaringType, out HashSet<string> common))
            {
                union.UnionWith(common);
                fromCommon = true;
                found = true;
            }
            if (box != null && box.ExtraMethods.TryGetValue(declaringType, out HashSet<string> extra))
            {
                union.UnionWith(extra);
                found = true;
            }

            if (found) pinned = union;
            return found;
        }

        /// <summary>
        /// Asks the box whether a method may be called, then applies the parameter, generic-argument
        /// and return-type sweep that <c>CilboxUsage.GetNativeMethodFromTypeAndName</c> runs
        /// afterwards. A method passes the name gate and still fails here whenever one of its types
        /// is not whitelisted, which is the single most confusing way for a call to be refused.
        /// </summary>
        public static CilboxVerdict EvaluateMethod(CilboxBoxInfo box, Type declaringType, MethodBase method)
        {
            if (box?.Probe == null || declaringType == null || method == null)
            {
                return new CilboxVerdict(CilboxAccess.Unknown, Loc("sdk.cilbox.reason.unknown"));
            }

            bool nameAllowed;
            try
            {
                nameAllowed = box.Probe.CheckMethodAllowed(out _, declaringType, method.Name, null, null, method.ToString());
            }
            catch (Exception)
            {
                // The Instantiate branch reaches into interpreter state a probe does not have.
                if (declaringType == typeof(UnityEngine.Object) &&
                    (method.Name == "Instantiate" || method.Name == "InstantiateAsync"))
                {
                    return new CilboxVerdict(CilboxAccess.Allowed, Loc("sdk.cilbox.reason.instantiateShim"));
                }
                return new CilboxVerdict(CilboxAccess.Unknown, Loc("sdk.cilbox.reason.unknown"));
            }

            if (!nameAllowed)
            {
                return new CilboxVerdict(CilboxAccess.Blocked, Loc("sdk.cilbox.reason.methodBlocked"));
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (!TypePassesRecursive(box, parameter.ParameterType))
                {
                    return new CilboxVerdict(CilboxAccess.Blocked,
                        LocFormat("sdk.cilbox.reason.parameterType", Pretty(parameter.ParameterType)));
                }
            }

            if (method is MethodInfo info && info.ReturnType != typeof(void))
            {
                if (!TypePassesRecursive(box, info.ReturnType))
                {
                    return new CilboxVerdict(CilboxAccess.Blocked,
                        LocFormat("sdk.cilbox.reason.returnType", Pretty(info.ReturnType)));
                }
            }

            bool pinned = TryGetMethodPins(box, declaringType, out _, out _);
            return pinned
                ? new CilboxVerdict(CilboxAccess.Allowed, Loc("sdk.cilbox.reason.methodPinned"))
                : new CilboxVerdict(CilboxAccess.DefaultAllowed, Loc("sdk.cilbox.reason.methodDefault"));
        }

        /// <summary>
        /// Mirrors <c>CilboxUsage.CheckTypeSecurityRecursive</c>: strip the by-ref marker, the array
        /// suffix and the arity tick, check the bare name, then recurse into generic arguments.
        /// </summary>
        public static bool TypePassesRecursive(CilboxBoxInfo box, Type type)
        {
            if (type == null) return false;

            string name = type.ToString();
            if (name.EndsWith("&", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 1);

            int bracket = name.IndexOf('[');
            if (bracket >= 0) name = name.Substring(0, bracket);

            int tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);

            if (!IsTypeAllowed(box, name)) return false;

            foreach (Type argument in type.GenericTypeArguments)
            {
                if (!TypePassesRecursive(box, argument)) return false;
            }
            return true;
        }

        /// <summary>Verdict for a field or property backing member.</summary>
        public static CilboxVerdict EvaluateField(CilboxBoxInfo box, Type declaringType, FieldInfo field)
        {
            if (box?.Probe == null || declaringType == null || field == null)
            {
                return new CilboxVerdict(CilboxAccess.Unknown, Loc("sdk.cilbox.reason.unknown"));
            }

            // A const is folded into the instruction stream, so no field token is ever emitted and
            // the whitelist never sees it.
            if (field.IsLiteral)
            {
                return new CilboxVerdict(CilboxAccess.Allowed, Loc("sdk.cilbox.reason.constInlined"));
            }

            if (!IsFieldAllowed(box, declaringType.FullName, field.Name))
            {
                return new CilboxVerdict(CilboxAccess.Blocked, Loc("sdk.cilbox.reason.fieldNotListed"));
            }

            if (!TypePassesRecursive(box, field.FieldType))
            {
                return new CilboxVerdict(CilboxAccess.Blocked,
                    LocFormat("sdk.cilbox.reason.fieldType", Pretty(field.FieldType)));
            }

            return new CilboxVerdict(CilboxAccess.Allowed, Loc("sdk.cilbox.reason.fieldListed"));
        }

        /// <summary>
        /// Whether a named member on a named type is reachable, without needing the
        /// <see cref="MethodBase"/>. Used by the API reference, where the point is to say "this
        /// call works in the avatar box but not the prop box" for a call written as text.
        ///
        /// <para>Checking the type alone is not enough: <c>Basis.Shims.*</c> puts every shim into
        /// the prop and scene boxes by wildcard, and those two then pin some of them shut with an
        /// empty method set. A type-only check would advertise scripted player input to prop
        /// authors, where every one of its methods is refused.</para>
        /// </summary>
        public static bool IsMemberAllowed(CilboxBoxInfo box, string typeName, string memberName)
        {
            if (!IsTypeAllowed(box, typeName)) return false;
            if (string.IsNullOrEmpty(memberName)) return true;

            Type type = ResolveType(typeName);
            if (type == null) return true;

            return !TryGetMethodPins(box, type, out HashSet<string> pinned, out _) || pinned.Contains(memberName);
        }

        // ------------------------------------------------------------------ type resolution

        private static Dictionary<string, Type> _typeCache;

        /// <summary>
        /// Finds a type by full name across every loaded assembly. Cached, because the lookup tab
        /// runs it on each keystroke.
        /// </summary>
        public static Type ResolveType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            if (_typeCache == null) _typeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
            if (_typeCache.TryGetValue(fullName, out Type cached)) return cached;

            Type found = Type.GetType(fullName, false);
            if (found == null)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        found = assembly.GetType(fullName, false);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (found != null) break;
                }
            }

            _typeCache[fullName] = found;
            return found;
        }

        /// <summary>Type names the boxes mention that actually resolve, for the lookup suggestions.</summary>
        public static List<string> AllListedTypeNames(CilboxBoxInfo box)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string name in CommonTypes) names.Add(name);
            if (box != null)
            {
                foreach (string name in box.ExtraTypes) names.Add(name);
            }
            return new List<string>(names);
        }

        public static string Pretty(Type type)
        {
            if (type == null) return "?";
            if (!type.IsGenericType) return type.Name;

            string name = type.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);

            Type[] arguments = type.GetGenericArguments();
            var parts = new string[arguments.Length];
            for (int i = 0; i < arguments.Length; i++) parts[i] = Pretty(arguments[i]);
            return name + "<" + string.Join(", ", parts) + ">";
        }

        // ------------------------------------------------------------------ hard denies

        /// <summary>
        /// The denies written directly into <c>CilboxBasisCommon.CheckMethodAllowed</c> rather than
        /// into a list. Shown so the window can explain a refusal no whitelist accounts for; the
        /// verdicts above never consult this table, they call the real method.
        /// </summary>
        public static readonly (string Type, string Members, string ReasonKey)[] HardDenies =
        {
            ("*", "*Invoke*", "sdk.cilbox.deny.invoke"),
            ("System.IntPtr, System.UIntPtr, System.Void*, System.RuntimeFieldHandle, System.RuntimeMethodHandle, System.RuntimeTypeHandle",
                "(the whole type)", "sdk.cilbox.deny.nativePointers"),
            ("Unity.Collections.NativeArray<T>",
                "everything except get_Length, get_IsCreated, ToArray, CopyTo, Equals, GetHashCode, ToString",
                "sdk.cilbox.deny.nativeArray"),
            ("UnityEngine.Application", "OpenURL, Quit, Unload, Load*, ExternalCall, ExternalEval, RequestUserAuthorization, GetBuildTags, SetBuildTags, SetStackTraceLogType, CanStreamedLevelBeLoaded", "sdk.cilbox.deny.application"),
            ("UnityEngine.Object", "Instantiate, InstantiateAsync", "sdk.cilbox.deny.instantiate"),
            ("UnityEngine.GameObject", "AddComponent, SendMessage, SendMessageUpwards, BroadcastMessage", "sdk.cilbox.deny.gameObject"),
            ("UnityEngine.Component", "SendMessage, SendMessageUpwards, BroadcastMessage", "sdk.cilbox.deny.component"),
            ("UnityEngine.Animator", "GetBehaviour, GetBehaviours", "sdk.cilbox.deny.animator"),
        };

        /// <summary>
        /// Types the box swaps for a shim before a script ever sees them, from
        /// <c>GetTypeOverride</c>. A script writing <c>UnityEngine.Debug</c> is really calling
        /// <c>BasisDebugPropsShim</c>, which is worth saying out loud.
        /// </summary>
        public static readonly (string Written, string Actual)[] TypeOverrides =
        {
            ("UnityEngine.Debug", "Basis.Shims.BasisDebugPropsShim"),
            ("UnityEngine.Video.VideoPlayer", "Basis.Shims.VideoPlayerShim"),
            ("Basis.Scripts.BasisSdk.BasisAvatar", "Basis.Shims.BasisAvatarShim (avatar box)"),
            ("Basis.Shims.BasisNetworkShim", "Basis.BasisNetworkShim"),
        };

        // ------------------------------------------------------------------ localization

        private static string Loc(string key) => Basis.Editor.Localization.BasisEditorLocalization.Get(key);

        private static string LocFormat(string key, params object[] args) =>
            Basis.Editor.Localization.BasisEditorLocalization.Get(key, args);
    }
}
#endif
