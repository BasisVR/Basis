using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Basis.BasisUI;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class BasisInspectorMember
{
    public string Label;
    public Func<object, object> Getter;
    public bool Hidden;
    public BasisInspectorMember(string label, Func<object, object> getter, bool hidden = false)
    {
        Label = label;
        Getter = getter;
        Hidden = hidden;
    }
}

[AutoStaticsCleanup]
public static partial class BasisInspectorReflection
{
    public const int MaxText = 120;
    const BindingFlags DeclaredInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    static readonly ConcurrentDictionary<Type, BasisInspectorMember[]> cache = new ConcurrentDictionary<Type, BasisInspectorMember[]>();
    [NoAutoStaticsCleanup] static readonly HashSet<string> materialSkipped = new HashSet<string>(StringComparer.Ordinal) { "color", "mainTexture", "mainTextureOffset", "mainTextureScale" };
    [NoAutoStaticsCleanup] static readonly HashSet<string> sideEffectProperties =new HashSet<string>(StringComparer.Ordinal) { "material", "materials", "mesh", "lightmapBakeType", "shadowRadius", "shadowAngle", "areaSize", "bakingOutput", "isPartOfStaticBatch" };
    [NoAutoStaticsCleanup] static readonly HashSet<string> animatorProperties = new HashSet<string>(StringComparer.Ordinal) { "runtimeAnimatorController", "avatar", "applyRootMotion", "updateMode", "cullingMode", "speed", "isHuman", "hasRootMotion", "humanScale", "isInitialized", "layerCount", "parameterCount", "hasBoundPlayables", "keepAnimatorStateOnDisable", "writeDefaultValuesOnDisable", "fireEvents" };
    [NoAutoStaticsCleanup] static readonly string[] transformProperties = { "localPosition", "localEulerAngles", "localScale", "position", "eulerAngles", "lossyScale", "childCount" };
    [NoAutoStaticsCleanup] static readonly string[] rectTransformProperties = { "anchoredPosition", "sizeDelta", "anchorMin", "anchorMax", "pivot" };
    [NoAutoStaticsCleanup] static readonly string[] sensitiveWords = { "password", "passphrase", "passwd", "secret", "token", "cookie", "privatekey", "apikey", "credential", "signature", "nonce" };
    [NoAutoStaticsCleanup] static readonly char[] lineBreaks = { '\r', '\n', '\t' };
    static BasisInspectorMember[] gameObjectMembers;
    public static BasisInspectorMember[] GameObjectMembers => gameObjectMembers ??= BuildGameObjectMembers();
    static string textHidden;
    public static string TextHidden => textHidden ??= BasisLocalization.Get("inspector.hidden");
    static string textUnreadable;
    public static string TextUnreadable => textUnreadable ??= BasisLocalization.Get("inspector.unreadable");
    static string textNone;
    public static string TextNone => textNone ??= BasisLocalization.Get("inspector.none");
    static string textMissing;
    public static string TextMissing => textMissing ??= BasisLocalization.Get("inspector.missing");
    static string textTrue;
    public static string TextTrue => textTrue ??= BasisLocalization.Get("inspector.true");
    static string textFalse;
    public static string TextFalse => textFalse ??= BasisLocalization.Get("inspector.false");
    static string textEnabled;
    public static string TextEnabled => textEnabled ??= BasisLocalization.Get("inspector.enabled");
    static string textDisabled;
    public static string TextDisabled => textDisabled ??= BasisLocalization.Get("inspector.disabled");
    public static void ResetLabels()
    {
        gameObjectMembers = null;
        textHidden = null;
        textUnreadable = null;
        textNone = null;
        textMissing = null;
        textTrue = null;
        textFalse = null;
        textEnabled = null;
        textDisabled = null;
    }
    static BasisInspectorMember[] BuildGameObjectMembers()
    {
        return new[]
        {
            new BasisInspectorMember(Nicify("name"), o => ((GameObject)o).name),
            new BasisInspectorMember(Nicify("activeSelf"), o => ((GameObject)o).activeSelf),
            new BasisInspectorMember(Nicify("activeInHierarchy"), o => ((GameObject)o).activeInHierarchy),
            new BasisInspectorMember(Nicify("tag"), o => ((GameObject)o).tag),
            new BasisInspectorMember(Nicify("layer"), o => LayerText(((GameObject)o).layer)),
            new BasisInspectorMember(Nicify("scene"), o => ((GameObject)o).scene),
            new BasisInspectorMember(BasisLocalization.Get("inspector.path"), o => PathOf(((GameObject)o).transform)),
        };
    }
    public static string LayerText(int layer)
    {
        string name = LayerMask.LayerToName(layer);
        return string.IsNullOrEmpty(name) ? layer.ToString(CultureInfo.InvariantCulture) : $"{layer} ({name})";
    }
    public static string PathOf(Transform target)
    {
        if (target == null) return string.Empty;
        int depth = 0;
        for (Transform walk = target; walk != null; walk = walk.parent) depth++;
        string[] names = new string[depth];
        for (Transform walk = target; walk != null; walk = walk.parent) names[--depth] = walk.name;
        return string.Join("/", names);
    }
    public static bool IsCached(Type type) => type != null && cache.ContainsKey(type);
    public static BasisInspectorMember[] GetMembers(Type type)
    {
        if (type == null) return Array.Empty<BasisInspectorMember>();
        if (cache.TryGetValue(type, out BasisInspectorMember[] members)) return members;
        List<BasisInspectorMember> list = new List<BasisInspectorMember>();
        try
        {
            if (typeof(Transform).IsAssignableFrom(type)) AddNamedProperties(type, typeof(RectTransform).IsAssignableFrom(type) ? Concat(transformProperties, rectTransformProperties) : transformProperties, list);
            else if (typeof(MonoBehaviour).IsAssignableFrom(type) || typeof(ScriptableObject).IsAssignableFrom(type)) AddSerializedFields(type, list);
            else AddNativeProperties(type, list);
            members = list.ToArray();
        }
        catch (Exception)
        {
            members = Array.Empty<BasisInspectorMember>();
        }
        return cache.GetOrAdd(type, members);
    }
    public static Task WarmAsync(Type[] types) => Task.Run(() => Parallel.ForEach(types, type => GetMembers(type)));
    public static Task PrewarmAsync(CancellationToken cancel)
    {
        List<Assembly> assemblies = new List<Assembly>(UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies());
        return Task.Factory.StartNew(() => Prewarm(assemblies, cancel), cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    static void Prewarm(List<Assembly> assemblies, CancellationToken cancel)
    {
        try
        {
            Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Lowest;
        }
        catch (Exception)
        {
        }
        foreach (Assembly assembly in assemblies)
        {
            if (cancel.IsCancellationRequested) return;
            if (assembly.IsDynamic) continue;
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types;
            }
            catch (Exception)
            {
                continue;
            }
            foreach (Type type in types)
            {
                if (cancel.IsCancellationRequested) return;
                if (type == null || type.IsAbstract || type.ContainsGenericParameters || !typeof(Component).IsAssignableFrom(type) || cache.ContainsKey(type)) continue;
                GetMembers(type);
            }
        }
    }
    static string[] Concat(string[] a, string[] b)
    {
        string[] result = new string[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
    static void AddNamedProperties(Type type, string[] names, List<BasisInspectorMember> list)
    {
        foreach (string name in names)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanRead) list.Add(new BasisInspectorMember(Nicify(name), property.GetValue));
        }
    }
    static void AddSerializedFields(Type type, List<BasisInspectorMember> list)
    {
        Stack<Type> chain = new Stack<Type>();
        for (Type walk = type; walk != null && walk != typeof(MonoBehaviour) && walk != typeof(ScriptableObject); walk = walk.BaseType) chain.Push(walk);
        while (chain.Count > 0)
        {
            foreach (FieldInfo field in chain.Pop().GetFields(DeclaredInstance))
            {
                if (!IsInspectorField(field)) continue;
                list.Add(new BasisInspectorMember(Nicify(field.Name), field.GetValue, IsSensitive(field.Name, field.FieldType)));
            }
        }
    }
    public static bool IsInspectorField(FieldInfo field)
    {
        if (field.IsStatic || field.IsInitOnly || field.IsLiteral) return false;
        if (field.IsDefined(typeof(HideInInspector), true) || field.IsDefined(typeof(NonSerializedAttribute), true)) return false;
        if (field.Name.IndexOf('<') >= 0) return false;
        return field.IsPublic || field.IsDefined(typeof(SerializeField), true) || field.IsDefined(typeof(SerializeReference), true);
    }
    static void AddNativeProperties(Type type, List<BasisInspectorMember> list)
    {
        bool animator = typeof(Animator).IsAssignableFrom(type), asset = !typeof(Component).IsAssignableFrom(type), material = typeof(Material).IsAssignableFrom(type);
        Stack<Type> chain = new Stack<Type>();
        for (Type walk = type; walk != null && walk != typeof(Component) && walk != typeof(Behaviour) && walk != typeof(UnityEngine.Object); walk = walk.BaseType) chain.Push(walk);
        while (chain.Count > 0)
        {
            foreach (PropertyInfo property in chain.Pop().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0 || property.GetGetMethod() == null) continue;
                if (property.IsDefined(typeof(ObsoleteAttribute), true) || sideEffectProperties.Contains(property.Name)) continue;
                if (animator && !animatorProperties.Contains(property.Name)) continue;
                if (material && materialSkipped.Contains(property.Name)) continue;
                if (asset && property.PropertyType.IsArray && property.PropertyType.GetElementType().IsValueType) continue;
                list.Add(new BasisInspectorMember(Nicify(property.Name), property.GetValue, IsSensitive(property.Name, property.PropertyType)));
            }
        }
    }
    static readonly Dictionary<Shader, BasisInspectorMember[]> shaderMembers = new Dictionary<Shader, BasisInspectorMember[]>();
    public static BasisInspectorMember[] ShaderMembers(Shader shader)
    {
        if (shader == null) return Array.Empty<BasisInspectorMember>();
        if (shaderMembers.TryGetValue(shader, out BasisInspectorMember[] members)) return members;
        List<BasisInspectorMember> list = new List<BasisInspectorMember>();
        try
        {
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if ((shader.GetPropertyFlags(i) & UnityEngine.Rendering.ShaderPropertyFlags.HideInInspector) != 0) continue;
                int id = shader.GetPropertyNameId(i);
                string label = shader.GetPropertyDescription(i);
                if (string.IsNullOrWhiteSpace(label)) label = Nicify(shader.GetPropertyName(i));
                switch (shader.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color: list.Add(new BasisInspectorMember(label, o => ((Material)o).GetColor(id))); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector: list.Add(new BasisInspectorMember(label, o => ((Material)o).GetVector(id))); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float: case UnityEngine.Rendering.ShaderPropertyType.Range: list.Add(new BasisInspectorMember(label, o => ((Material)o).GetFloat(id))); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int: list.Add(new BasisInspectorMember(label, o => ((Material)o).GetInteger(id))); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture: list.Add(new BasisInspectorMember(label, o => ((Material)o).GetTexture(id))); break;
                }
            }
        }
        catch (Exception)
        {
        }
        members = list.ToArray();
        shaderMembers[shader] = members;
        return members;
    }
    public static bool IsSensitive(string name, Type type)
    {
        if (type != typeof(string) && type != typeof(byte[]) && type != typeof(char[]) && type != typeof(List<byte>)) return false;
        string lower = name.ToLowerInvariant().Replace("_", string.Empty);
        foreach (string word in sensitiveWords) if (lower.Contains(word)) return true;
        return lower == "pass" || lower.EndsWith("pass", StringComparison.Ordinal) || lower == "key" || lower.EndsWith("key", StringComparison.Ordinal);
    }
    public static string Nicify(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int start = 0;
        if (name.StartsWith("m_", StringComparison.Ordinal)) start = 2;
        else if (name.Length > 2 && name[0] == 'k' && name[1] == '_') start = 2;
        while (start < name.Length && name[start] == '_') start++;
        if (start >= name.Length) return name;
        StringBuilder builder = new StringBuilder(name.Length + 8);
        for (int i = start; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '_')
            {
                if (builder.Length > 0 && builder[builder.Length - 1] != ' ') builder.Append(' ');
                continue;
            }
            if (builder.Length > 0 && builder[builder.Length - 1] != ' ')
            {
                char previous = name[i - 1];
                bool upper = char.IsUpper(c), nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (upper && (char.IsLower(previous) || char.IsDigit(previous) || char.IsUpper(previous) && nextLower)) builder.Append(' ');
                else if (char.IsDigit(c) && char.IsLetter(previous)) builder.Append(' ');
            }
            builder.Append(builder.Length == 0 ? char.ToUpperInvariant(c) : c);
        }
        return builder.ToString();
    }
    public static string ComponentState(Component component)
    {
        switch (component)
        {
            case Behaviour behaviour: return behaviour.enabled ? TextEnabled : TextDisabled;
            case Renderer renderer: return renderer.enabled ? TextEnabled : TextDisabled;
            case Collider collider: return collider.enabled ? TextEnabled : TextDisabled;
        }
        return string.Empty;
    }
    public static string Read(BasisInspectorMember member, object target) => TryGet(member, target, out object raw, out string text) ? Format(raw) : text;
    public static bool TryGet(BasisInspectorMember member, object target, out object raw, out string text)
    {
        raw = null;
        if (member.Hidden)
        {
            text = TextHidden;
            return false;
        }
        try
        {
            raw = member.Getter(target);
            text = null;
            return true;
        }
        catch (Exception)
        {
            text = TextUnreadable;
            return false;
        }
    }
    public static bool Same(object previous, object next)
    {
        if (previous == null || next == null) return previous == null && next == null;
        if (previous is UnityEngine.Object a) return next is UnityEngine.Object b && ReferenceEquals(a, b) && a != null;
        if (previous is string || previous.GetType().IsValueType) return previous.Equals(next);
        return false;
    }
    public static string Format(object value)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        switch (value)
        {
            case null: return TextNone;
            case UnityEngine.Object unityObject: return unityObject == null ? TextMissing : $"{unityObject.name} ({unityObject.GetType().Name})";
            case string text: return Clip(text);
            case bool flag: return flag ? TextTrue : TextFalse;
            case float number: return number.ToString("0.###", inv);
            case double number: return number.ToString("0.###", inv);
            case Vector2 v: return $"({v.x.ToString("0.###", inv)}, {v.y.ToString("0.###", inv)})";
            case Vector3 v: return $"({v.x.ToString("0.###", inv)}, {v.y.ToString("0.###", inv)}, {v.z.ToString("0.###", inv)})";
            case Vector4 v: return $"({v.x.ToString("0.###", inv)}, {v.y.ToString("0.###", inv)}, {v.z.ToString("0.###", inv)}, {v.w.ToString("0.###", inv)})";
            case Vector2Int v: return $"({v.x}, {v.y})";
            case Vector3Int v: return $"({v.x}, {v.y}, {v.z})";
            case Quaternion q: return Format(q.eulerAngles);
            case Color c: return $"#{ColorUtility.ToHtmlStringRGBA(c)}";
            case Color32 c: return $"#{ColorUtility.ToHtmlStringRGBA(c)}";
            case Rect r: return $"({r.x.ToString("0.###", inv)}, {r.y.ToString("0.###", inv)}, {r.width.ToString("0.###", inv)}, {r.height.ToString("0.###", inv)})";
            case Bounds b: return $"{Format(b.center)} {Format(b.size)}";
            case LayerMask mask: return mask.value.ToString(inv);
            case Scene scene: return scene.IsValid() ? scene.name : TextNone;
            case Enum e: return e.ToString();
            case IFormattable formattable when value.GetType().IsPrimitive: return formattable.ToString(null, inv);
            case char c: return c.ToString();
            case Array array: return $"{TypeName(array.GetType().GetElementType())}[{array.Length}]";
            case ICollection collection: return $"{TypeName(value.GetType())} [{collection.Count}]";
        }
        return TypeName(value.GetType());
    }
    public static string TypeName(Type type)
    {
        if (type == null) return string.Empty;
        if (!type.IsGenericType) return type.Name;
        string name = type.Name;
        int tick = name.IndexOf('`');
        if (tick > 0) name = name.Substring(0, tick);
        Type[] arguments = type.GetGenericArguments();
        string[] parts = new string[arguments.Length];
        for (int i = 0; i < arguments.Length; i++) parts[i] = TypeName(arguments[i]);
        return $"{name}<{string.Join(", ", parts)}>";
    }
    public static string Clip(string text)
    {
        if (text == null) return string.Empty;
        if (text.Length <= MaxText && text.IndexOfAny(lineBreaks) < 0) return text;
        if (text.IndexOfAny(lineBreaks) >= 0) text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return text.Length > MaxText ? text.Substring(0, MaxText - 3) + "..." : text;
    }
}
