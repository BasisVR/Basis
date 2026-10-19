// Editor/Documentation Engine/BasisDocSnippet.cs
// Turns a MemberInfo plus the chain it was reached through into code someone can paste.
//
// The old snippet wrote parameters as /* Vector3 position */, which reads as documentation but
// does not compile, so the reader still had to work out what to pass. Everything here is written
// to compile as-is: every parameter gets a declared local with a real starting value, the access
// chain is captured into a named variable so the null guard means something, and the using
// directives the snippet depends on are listed with it.

using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// Where a member is being reached from, so a snippet can be written against the real chain
/// rather than against the declaring type in the abstract.
/// </summary>
public sealed class BasisDocSnippetContext
{
    /// <summary>The type whose API is on screen — the end of the drill-down chain.</summary>
    public Type HostType;

    /// <summary>Code that evaluates to an instance of <see cref="HostType"/>, e.g. "BasisLocalPlayer.Instance".</summary>
    public string AccessExpr;

    /// <summary>How that expression was found: "Singleton", "Provider.Get", "GetComponent"…</summary>
    public string AccessorHint;

    /// <summary>Whether the chain can evaluate to null at runtime.</summary>
    public bool MayBeNull = true;
}

/// <summary>A rendered snippet: the code, and the namespaces it needs.</summary>
public sealed class BasisDocSnippetResult
{
    public string Code = string.Empty;
    public List<string> Usings = new();
    public bool IsEmpty => string.IsNullOrWhiteSpace(Code);
}

/// <summary>
/// Builds the "How to call" code for a member. Pure string work with no UI and no editor state, so
/// the same builder serves the inspector's plain snippet and the cilboxed-script variant, and can
/// be exercised from tests.
/// </summary>
[NoAutoStaticsCleanup]
public static class BasisDocSnippet
{
    /// <summary>Class name used for the cilboxed wrapper. Only a placeholder — readers rename it.</summary>
    public const string CilboxClassName = "MyScript";

    // ------------------------------------------------------------------ entry points

    /// <summary>The snippet as written inside an ordinary method body.</summary>
    public static BasisDocSnippetResult Build(MemberInfo member, BasisDocSnippetContext context)
    {
        Draft draft = Draft.For(member, context);
        return draft == null ? new BasisDocSnippetResult() : Render(draft, false);
    }

    /// <summary>
    /// The same call as a complete cilboxed script. Cilbox interprets a whole class, so a body
    /// fragment is not enough: the reader needs the attribute, the lifecycle method the call
    /// belongs in, and — for events — the field that keeps the subscription reachable at teardown.
    /// </summary>
    public static BasisDocSnippetResult BuildCilbox(MemberInfo member, BasisDocSnippetContext context)
    {
        Draft draft = Draft.For(member, context);
        return draft == null ? new BasisDocSnippetResult() : Render(draft, true);
    }

    // ------------------------------------------------------------------ shared naming helpers

    /// <summary>A C# type name as it would be written in source, with nested and generic types spelled out.</summary>
    public static string NiceType(Type t)
    {
        if (t == null || t == typeof(void)) return "void";
        if (t.IsByRef) return NiceType(t.GetElementType());
        if (t.IsArray) return NiceType(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";

        Type nullable = Nullable.GetUnderlyingType(t);
        if (nullable != null) return NiceType(nullable) + "?";

        if (t == typeof(int)) return "int";
        if (t == typeof(uint)) return "uint";
        if (t == typeof(long)) return "long";
        if (t == typeof(ulong)) return "ulong";
        if (t == typeof(short)) return "short";
        if (t == typeof(ushort)) return "ushort";
        if (t == typeof(byte)) return "byte";
        if (t == typeof(sbyte)) return "sbyte";
        if (t == typeof(float)) return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(decimal)) return "decimal";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(char)) return "char";
        if (t == typeof(string)) return "string";
        if (t == typeof(object)) return "object";

        string name = t.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0) name = name.Substring(0, tick);

        // A nested type has to be written through its outer type — "Outer.Inner", not "Inner".
        if (t.IsNested && !t.IsGenericParameter && t.DeclaringType != null)
            name = NiceType(t.DeclaringType) + "." + name;

        if (!t.IsGenericType) return name;
        return name + "<" + string.Join(", ", t.GetGenericArguments().Select(NiceType)) + ">";
    }

    /// <summary>The type name to write when calling a static member: nested-aware, namespace omitted.</summary>
    public static string StaticQualifier(Type t) => NiceType(t);

    /// <summary>A camelCase identifier that is safe to declare, derived from a type or member name.</summary>
    public static string SafeVarName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "value";

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        }
        if (sb.Length == 0) return "value";

        string v = char.ToLowerInvariant(sb[0]) + sb.ToString().Substring(1);
        if (char.IsDigit(v[0]) || Keywords.Contains(v)) v = "_" + v;
        return v;
    }

    /// <summary>
    /// A method signature as it appears in source, including generic parameters. Default values are
    /// off for list rows, where they only add width, and on for the detail panel, where the reader
    /// is deciding what to pass.
    /// </summary>
    public static string Signature(MethodInfo m, bool includeDefaults = false)
    {
        if (m == null) return string.Empty;

        string generics = m.IsGenericMethod
            ? "<" + string.Join(", ", m.GetGenericArguments().Select(NiceType)) + ">"
            : string.Empty;

        string parms = string.Join(", ", m.GetParameters().Select(p =>
            ParameterModifier(p) + NiceType(p.ParameterType) + " " + p.Name +
            (includeDefaults && p.HasDefaultValue ? " = " + Literal(p.DefaultValue) : string.Empty)));

        return $"{NiceType(m.ReturnType)} {m.Name}{generics}({parms})";
    }

    /// <summary>A literal that compiles and stands in for a value of this type.</summary>
    public static string PlaceholderFor(Type t)
    {
        if (t == null) return "null";
        if (t.IsByRef) t = t.GetElementType();
        if (t == null) return "null";

        if (Nullable.GetUnderlyingType(t) != null) return "null";

        if (t == typeof(string)) return "\"\"";
        if (t == typeof(bool)) return "false";
        if (t == typeof(char)) return "'\\0'";
        if (t == typeof(float)) return "0f";
        if (t == typeof(double)) return "0d";
        if (t == typeof(decimal)) return "0m";
        if (t == typeof(long)) return "0L";
        if (t == typeof(ulong)) return "0UL";
        if (t == typeof(uint)) return "0u";
        if (t == typeof(object)) return "null";
        if (t.IsPrimitive) return "0";

        if (t.IsEnum)
        {
            string[] names = Enum.GetNames(t);
            return names.Length > 0 ? $"{NiceType(t)}.{names[0]}" : $"default({NiceType(t)})";
        }

        if (UnityLiterals.TryGetValue(t, out string unity)) return unity;

        if (t.IsArray)
        {
            Type element = t.GetElementType();
            return $"new {NiceType(element)}[0]";
        }

        if (typeof(Delegate).IsAssignableFrom(t)) return LambdaFor(t);

        if (t.IsGenericType)
        {
            Type def = t.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(HashSet<>) || def == typeof(Dictionary<,>) || def == typeof(Queue<>) || def == typeof(Stack<>))
                return $"new {NiceType(t)}()";
        }

        // Interfaces, abstracts and Unity objects have to come from somewhere the reader owns.
        if (t.IsInterface || t.IsAbstract) return "null";
        if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return "null";

        if (t.IsValueType) return $"default({NiceType(t)})";
        if (t.GetConstructor(Type.EmptyTypes) != null) return $"new {NiceType(t)}()";
        return "null";
    }

    /// <summary>Formats a constant the way it would be written in code.</summary>
    public static string Literal(object v) => v switch
    {
        null => "null",
        string s => $"\"{s}\"",
        char c => $"'{c}'",
        bool b => b ? "true" : "false",
        float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture) + "f",
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture) + "d",
        Enum e => $"{NiceType(e.GetType())}.{e}",
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)
    };

    // ------------------------------------------------------------------ draft assembly

    /// <summary>
    /// The pieces of a snippet, kept apart until render time because the plain and cilboxed
    /// layouts want them in different places: a cilboxed subscription needs its teardown in
    /// OnDestroy and its handler at class scope, where a body fragment can just run them in order.
    /// </summary>
    private sealed class Draft
    {
        public readonly List<string> Prologue = new();
        public readonly List<string> Setup = new();
        public readonly List<string> Body = new();
        public readonly List<string> Teardown = new();
        public readonly List<string> Members = new();
        public readonly SortedSet<string> Usings = new(NamespaceOrder.Instance);

        /// <summary>Name the chain was captured into, or the static type name.</summary>
        public string Target;

        /// <summary>
        /// The captured chain, kept apart from <see cref="Setup"/> because the two layouts declare
        /// it differently: a body fragment declares a local, while a cilboxed script that has to
        /// unsubscribe in OnDestroy needs a field the second method can still see.
        /// </summary>
        public string ChainType;
        public string ChainExpr;
        public bool ChainGuard;

        /// <summary>Whether the chain has to outlive the method it is captured in.</summary>
        public bool ChainMustPersist;

        public static Draft For(MemberInfo member, BasisDocSnippetContext context)
        {
            if (member == null) return null;

            var d = new Draft();
            Type declaring = member.DeclaringType;
            bool isStatic = IsStatic(member);
            bool wantsTeardown = member is EventInfo;

            var obsolete = member.GetCustomAttribute<ObsoleteAttribute>();
            if (obsolete != null)
            {
                d.Prologue.Add(string.IsNullOrEmpty(obsolete.Message)
                    ? "// Obsolete — expect this to go away."
                    : $"// Obsolete: {obsolete.Message}");
            }

            if (isStatic)
            {
                d.Target = StaticQualifier(declaring);
                d.AddUsing(declaring);
            }
            else
            {
                string chain = string.IsNullOrEmpty(context?.AccessExpr)
                    ? $"GetComponent<{NiceType(declaring)}>()"
                    : context.AccessExpr;
                Type hostType = context?.HostType ?? declaring;

                d.Target = SafeVarName(hostType.Name);
                d.AddUsing(hostType);
                d.AddUsing(declaring);

                // The drill-down chain reaches collection elements through LINQ.
                if (chain.Contains("FirstOrDefault(")) d.Usings.Add("System.Linq");

                // The expression is on the very next line, so the comment only carries how it was found.
                if (!string.IsNullOrEmpty(context?.AccessorHint))
                    d.Prologue.Add($"// Reached through: {context.AccessorHint}");

                d.ChainType = NiceType(hostType);
                d.ChainExpr = chain;
                d.ChainGuard = (context?.MayBeNull ?? true) && !hostType.IsValueType;
                d.ChainMustPersist = wantsTeardown;

                if (declaring != hostType && declaring.IsAssignableFrom(hostType))
                    d.Prologue.Add($"// Declared on {NiceType(declaring)}, which {NiceType(hostType)} inherits.");
            }

            switch (member)
            {
                case FieldInfo f: d.BuildField(f); break;
                case PropertyInfo p: d.BuildProperty(p); break;
                case MethodInfo m: d.BuildMethod(m); break;
                case EventInfo e: d.BuildEvent(e); break;
                default: return null;
            }

            return d.Body.Count == 0 && d.Members.Count == 0 ? null : d;
        }

        // -------------------------------------------------------------- members

        private void BuildField(FieldInfo f)
        {
            AddUsing(f.FieldType);
            string local = UniqueLocal(SafeVarName(f.Name));

            if (f.IsLiteral)
            {
                string value;
                try { value = Literal(f.GetRawConstantValue()); }
                catch (Exception) { value = null; }

                Prologue.Add(value == null
                    ? "// A const — the compiler folds the value into your code."
                    : $"// A const — the compiler folds {value} into your code.");
            }

            Body.Add($"{NiceType(f.FieldType)} {local} = {Target}.{f.Name};");

            if (!f.IsLiteral && !f.IsInitOnly)
                Body.Add($"{Target}.{f.Name} = {PlaceholderFor(f.FieldType)};");
            else if (f.IsInitOnly)
                Prologue.Add("// readonly — assigned by its owner, not by you.");
        }

        private void BuildProperty(PropertyInfo p)
        {
            AddUsing(p.PropertyType);

            MethodInfo getter = p.GetMethod;
            MethodInfo setter = p.SetMethod;
            bool canRead = getter != null && getter.IsPublic;
            bool canWrite = setter != null && setter.IsPublic && !IsInitOnly(setter);

            if (canRead)
            {
                string local = UniqueLocal(SafeVarName(p.Name));
                Body.Add($"{NiceType(p.PropertyType)} {local} = {Target}.{p.Name};");
            }

            if (canWrite)
            {
                if (canRead) Body.Add(string.Empty);
                Body.Add($"{Target}.{p.Name} = {PlaceholderFor(p.PropertyType)};");
            }

            if (!canWrite && setter != null)
                Prologue.Add(IsInitOnly(setter) ? "// init-only — settable in an object initializer only." : "// The setter is not public — read only from here.");
            else if (!canWrite)
                Prologue.Add("// Read only.");
            else if (!canRead)
                Prologue.Add("// Write only.");
        }

        private void BuildMethod(MethodInfo m)
        {
            MethodInfo concrete = m;
            string generics = string.Empty;

            if (m.IsGenericMethodDefinition)
            {
                Type[] parameters = m.GetGenericArguments();
                var chosen = new Type[parameters.Length];
                for (int i = 0; i < parameters.Length; i++) chosen[i] = ChooseTypeArgument(parameters[i]);

                try { concrete = m.MakeGenericMethod(chosen); }
                catch (Exception) { concrete = m; }

                generics = "<" + string.Join(", ", chosen.Select(NiceType)) + ">";
                Prologue.Add($"// Generic: {string.Join(", ", parameters.Select(t => t.Name))} shown here as {string.Join(", ", chosen.Select(NiceType))}.");
                foreach (Type t in chosen) AddUsing(t);
            }

            ParameterInfo[] ps = concrete.GetParameters();
            var args = new List<string>(ps.Length);
            bool hasOut = false;

            foreach (ParameterInfo p in ps)
            {
                Type pt = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                AddUsing(pt);

                string local = UniqueLocal(SafeVarName(p.Name));

                if (p.IsOut)
                {
                    hasOut = true;
                    args.Add($"out {NiceType(pt)} {local}");
                    continue;
                }

                string note = p.HasDefaultValue ? $"   // optional, defaults to {Literal(p.DefaultValue)}" : string.Empty;
                if (IsParams(p)) note = "   // params — pass as many as you like";

                Setup.Add($"{NiceType(pt)} {local} = {PlaceholderFor(pt)};{note}");
                args.Add(p.ParameterType.IsByRef && !p.IsIn ? $"ref {local}" : local);
            }

            string call = $"{Target}.{m.Name}{generics}({string.Join(", ", args)})";
            Type returns = concrete.ReturnType;

            if (returns == typeof(void))
            {
                Body.Add(call + ";");
                return;
            }

            AddUsing(returns);

            if (IsAwaitable(returns))
            {
                Prologue.Add("// Returns a task — await it from an async method.");
                bool hasResult = returns.IsGenericType;
                Body.Add(hasResult
                    ? $"{NiceType(returns.GetGenericArguments()[0])} {UniqueLocal(ResultName(m))} = await {call};"
                    : $"await {call};");
                return;
            }

            if (typeof(IEnumerator).IsAssignableFrom(returns))
            {
                Prologue.Add("// Returns a coroutine — it does nothing until it is started.");
                Body.Add($"StartCoroutine({call});");
                return;
            }

            if (returns == typeof(bool) && hasOut)
            {
                Body.Add($"if ({call})");
                Body.Add("{");
                Body.Add("    // succeeded — the out value is filled in");
                Body.Add("}");
                return;
            }

            Body.Add($"{NiceType(returns)} {UniqueLocal(ResultName(m))} = {call};");
        }

        private void BuildEvent(EventInfo e)
        {
            // The delegate type is never written out — the handler is spelled as a plain method —
            // so its namespace is not part of what this snippet needs.
            Type handlerType = e.EventHandlerType;

            string handler = "Handle" + e.Name;
            MethodInfo invoke = handlerType?.GetMethod("Invoke");
            ParameterInfo[] ps = invoke?.GetParameters() ?? Array.Empty<ParameterInfo>();

            foreach (ParameterInfo p in ps) AddUsing(p.ParameterType);

            string parms = string.Join(", ", ps.Select(p => $"{NiceType(p.ParameterType)} {SafeVarName(p.Name)}"));
            string returns = invoke == null || invoke.ReturnType == typeof(void) ? "void" : NiceType(invoke.ReturnType);
            string body = returns == "void" ? " { }" : $"\n{{\n    return {PlaceholderFor(invoke.ReturnType)};\n}}";

            Body.Add($"{Target}.{e.Name} += {handler};");
            Teardown.Add($"{Target}.{e.Name} -= {handler};");
            Members.Add($"{returns} {handler}({parms}){body}");
        }

        // -------------------------------------------------------------- plumbing

        private readonly HashSet<string> _locals = new(StringComparer.Ordinal);

        private string UniqueLocal(string name)
        {
            if (string.Equals(name, Target, StringComparison.Ordinal)) name += "Value";
            string candidate = name;
            int n = 2;
            while (!_locals.Add(candidate)) candidate = name + n++;
            return candidate;
        }

        public void AddUsing(Type t)
        {
            if (t == null) return;
            if (t.IsByRef || t.IsArray) { AddUsing(t.GetElementType()); return; }
            if (t.IsGenericParameter) return;

            if (t.IsNested && t.DeclaringType != null) { AddUsing(t.DeclaringType); return; }

            // int, bool and friends are written as keywords, so they never justify a using of System.
            if (!string.IsNullOrEmpty(t.Namespace) && !HasKeywordAlias(t)) Usings.Add(t.Namespace);
            if (t.IsGenericType)
            {
                foreach (Type arg in t.GetGenericArguments()) AddUsing(arg);
            }
        }
    }

    // ------------------------------------------------------------------ rendering

    private static BasisDocSnippetResult Render(Draft draft, bool cilbox)
    {
        var result = new BasisDocSnippetResult();
        if (cilbox)
        {
            draft.Usings.Add("UnityEngine");
            draft.Usings.Add("Cilbox");
        }
        result.Usings = draft.Usings.ToList();

        var sb = new StringBuilder();
        foreach (string ns in result.Usings) sb.Append("using ").Append(ns).AppendLine(";");
        if (result.Usings.Count > 0) sb.AppendLine();

        bool asField = cilbox && draft.ChainMustPersist && draft.ChainExpr != null;

        bool hasChain = draft.ChainExpr != null;

        var opening = new List<string>();
        opening.AddRange(draft.Prologue);
        if (hasChain)
        {
            opening.Add(asField
                ? $"{draft.Target} = {draft.ChainExpr};"
                : $"{draft.ChainType} {draft.Target} = {draft.ChainExpr};");
            if (draft.ChainGuard) opening.Add($"if ({draft.Target} == null) return;");
        }
        // A lone prologue comment belongs against the code it introduces, so only break the block
        // once there is real setup above.
        if (hasChain && draft.Setup.Count > 0) opening.Add(string.Empty);
        opening.AddRange(draft.Setup);
        bool openingIsCode = hasChain || draft.Setup.Count > 0;

        if (!cilbox)
        {
            AppendLines(sb, opening, 0);
            if (openingIsCode && draft.Body.Count > 0) sb.AppendLine();
            AppendLines(sb, draft.Body, 0);

            if (draft.Teardown.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("// later, when the script goes away");
                AppendLines(sb, draft.Teardown, 0);
            }

            if (draft.Members.Count > 0)
            {
                sb.AppendLine();
                AppendLines(sb, draft.Members, 0);
            }

            return Finish(result, sb);
        }

        sb.AppendLine("[Cilboxable]");
        sb.AppendLine($"public class {CilboxClassName} : MonoBehaviour");
        sb.AppendLine("{");

        if (asField)
            sb.AppendLine($"    {draft.ChainType} {draft.Target};").AppendLine();

        // Start, not OnEnable: the proxy drops the first OnEnable, so setup put there is lost
        // on the first activation.
        sb.AppendLine("    void Start()");
        sb.AppendLine("    {");
        AppendLines(sb, opening, 8);
        if (openingIsCode && draft.Body.Count > 0) sb.AppendLine();
        AppendLines(sb, draft.Body, 8);
        sb.AppendLine("    }");

        if (draft.Teardown.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    void OnDestroy()");
            sb.AppendLine("    {");
            if (asField) sb.AppendLine($"        if ({draft.Target} == null) return;");
            AppendLines(sb, draft.Teardown, 8);
            sb.AppendLine("    }");
        }

        foreach (string member in draft.Members)
        {
            sb.AppendLine();
            AppendLines(sb, new List<string> { member }, 4);
        }

        sb.AppendLine("}");
        return Finish(result, sb);
    }

    private static BasisDocSnippetResult Finish(BasisDocSnippetResult result, StringBuilder sb)
    {
        result.Code = sb.ToString().TrimEnd() + "\n";
        return result;
    }

    private static void AppendLines(StringBuilder sb, List<string> lines, int indent)
    {
        string pad = new string(' ', indent);
        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line)) { sb.AppendLine(); continue; }

            // A member declaration arrives as a multi-line block; indent every line of it.
            foreach (string part in line.Split('\n'))
                sb.Append(pad).AppendLine(part.TrimEnd('\r'));
        }
    }

    // ------------------------------------------------------------------ small predicates

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "while"
    };

    private static readonly Dictionary<Type, string> UnityLiterals = new()
    {
        { typeof(Vector2), "Vector2.zero" },
        { typeof(Vector3), "Vector3.zero" },
        { typeof(Vector4), "Vector4.zero" },
        { typeof(Vector2Int), "Vector2Int.zero" },
        { typeof(Vector3Int), "Vector3Int.zero" },
        { typeof(Quaternion), "Quaternion.identity" },
        { typeof(Color), "Color.white" },
        { typeof(Color32), "new Color32(255, 255, 255, 255)" },
        { typeof(Matrix4x4), "Matrix4x4.identity" },
        { typeof(LayerMask), "~0" },
        { typeof(Rect), "new Rect(0f, 0f, 1f, 1f)" },
        { typeof(Bounds), "new Bounds(Vector3.zero, Vector3.one)" },
    };

    private static string ParameterModifier(ParameterInfo p)
    {
        if (p.IsOut) return "out ";
        if (p.ParameterType.IsByRef) return p.IsIn ? "in " : "ref ";
        return IsParams(p) ? "params " : string.Empty;
    }

    private static readonly HashSet<Type> KeywordAliases = new()
    {
        typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(short), typeof(ushort),
        typeof(byte), typeof(sbyte), typeof(float), typeof(double), typeof(decimal),
        typeof(bool), typeof(char), typeof(string), typeof(object), typeof(void)
    };

    private static bool HasKeywordAlias(Type t) => KeywordAliases.Contains(t);

    private static bool IsParams(ParameterInfo p) =>
        p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0;

    private static bool IsStatic(MemberInfo mi) => mi switch
    {
        FieldInfo f => f.IsStatic,
        PropertyInfo p => (p.GetMethod ?? p.SetMethod)?.IsStatic ?? false,
        MethodInfo m => m.IsStatic,
        EventInfo e => (e.AddMethod ?? e.RemoveMethod)?.IsStatic ?? false,
        _ => false
    };

    // `init` setters carry a modreq the reflection API exposes only as a required custom modifier.
    private static bool IsInitOnly(MethodInfo setter) =>
        setter != null && setter.ReturnParameter != null &&
        setter.ReturnParameter.GetRequiredCustomModifiers()
              .Any(t => t.Name == "IsExternalInit");

    private static bool IsAwaitable(Type t)
    {
        if (t == null) return false;
        string name = t.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0) name = name.Substring(0, tick);
        return name is "Task" or "ValueTask" or "UniTask" or "Awaitable";
    }

    // A generic parameter has to become something concrete for the snippet to compile. Constraints
    // narrow it far enough to be useful: `where T : Component` is a much better hint than `object`.
    private static Type ChooseTypeArgument(Type parameter)
    {
        foreach (Type constraint in parameter.GetGenericParameterConstraints())
        {
            if (constraint.IsInterface) continue;
            if (constraint.IsGenericParameter) continue;
            return constraint;
        }

        GenericParameterAttributes attributes = parameter.GenericParameterAttributes;
        if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) return typeof(int);

        foreach (Type constraint in parameter.GetGenericParameterConstraints())
        {
            if (!constraint.IsGenericParameter) return constraint;
        }
        return typeof(object);
    }

    private static string LambdaFor(Type delegateType)
    {
        MethodInfo invoke = delegateType.GetMethod("Invoke");
        if (invoke == null) return "null";

        ParameterInfo[] ps = invoke.GetParameters();
        string head = ps.Length switch
        {
            0 => "()",
            1 => SafeVarName(ps[0].Name),
            _ => "(" + string.Join(", ", ps.Select(p => SafeVarName(p.Name))) + ")"
        };
        return invoke.ReturnType == typeof(void) ? head + " => { }" : head + " => default";
    }

    private static string ResultName(MethodInfo m)
    {
        string name = m.Name;
        foreach (string prefix in new[] { "TryGet", "Try", "Get", "Find", "Create", "Build", "Make" })
        {
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                name = name.Substring(prefix.Length);
                break;
            }
        }
        string local = SafeVarName(name);
        return local == "value" ? "result" : local;
    }

    /// <summary>Sorts System first, then alphabetically — the order the editor's own files use.</summary>
    [NoAutoStaticsCleanup]
    private sealed class NamespaceOrder : IComparer<string>
    {
        public static readonly NamespaceOrder Instance = new();

        public int Compare(string a, string b)
        {
            bool sa = a != null && (a == "System" || a.StartsWith("System.", StringComparison.Ordinal));
            bool sb = b != null && (b == "System" || b.StartsWith("System.", StringComparison.Ordinal));
            if (sa != sb) return sa ? -1 : 1;
            return string.CompareOrdinal(a, b);
        }
    }
}
