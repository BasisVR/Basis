using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Replicates avatar components through the Generic (glTF) fallback. glTF carries only
/// meshes/materials/skin, so everything that makes an avatar behave — jiggle rigs, face
/// tracking, Vixxy controls, colliders — is captured at build time as a reflection walk over
/// Unity-serialized fields and rebuilt onto the imported hierarchy at load.
///
/// References are the whole point: fields pointing at Transforms/GameObjects/Components
/// inside the avatar are stored as node paths (+ a same-type component index) and re-resolved
/// on the imported hierarchy; ScriptableObject references are inlined by value; asset
/// references that can't travel (meshes, textures, materials — the GLB already carries their
/// equivalents) are dropped with a log rather than failing the component.
///
/// Safety model — the payload is remote content, so every client-side protection applies
/// BEFORE anything is constructed, not just on the clones afterwards:
/// - Apply requires the same <see cref="ContentPoliceSelector"/> the removal walk uses and
///   checks each type with the identical predicate (ApprovedTypeNames.Contains(FullName));
///   disallowed types are never AddComponent'ed. No selector = nothing replicated.
/// - Apply also enforces the capture-side skip list, so approval alone can't reconstruct a type
///   capture would never emit (Transform/RectTransform, the renderers, BasisAvatar).
/// - UnityEvent fields are never serialized or applied, mirroring the persistent-listener
///   scrub (a remote payload must not be able to author persistent calls).
/// - Inline ScriptableObjects are only created when the receiving field declares a specific
///   ScriptableObject subclass the payload type is assignable to — generic Object fields
///   can't be used to instantiate arbitrary client types — and their count is budgeted.
/// - Payload size, component count and list lengths are hard-capped.
/// - Reconstruction happens on the hidden (inactive) template BEFORE instancing, so nothing
///   replicated runs there, and every clone still passes the full ContentPolice walk.
/// Capture and apply share the same field-walk rules, which is what keeps them symmetric.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisGenericComponentReplicator
{
    public const int MaxDepth = 8;
    public const int MaxJsonBytes = 2 * 1024 * 1024;
    public const int MaxComponents = 256;
    public const int MaxScriptableObjects = 64;
    public const int MaxListElements = 4096;

    private sealed class ApplyContext
    {
        public Transform Root;
        public int ScriptableObjectBudget = MaxScriptableObjects;
    }

    // Handled elsewhere in the generic pipeline (hierarchy, rig, renderers, wiring) — never
    // replicated here.
    private static readonly HashSet<string> SkippedTypeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "UnityEngine.Transform",
        "UnityEngine.RectTransform",
        "UnityEngine.Animator",
        "UnityEngine.SkinnedMeshRenderer",
        "UnityEngine.MeshRenderer",
        "UnityEngine.MeshFilter",
        "Basis.Scripts.BasisSdk.BasisAvatar",
    };

    // ─────────────────────────── capture ───────────────────────────

    /// <summary>Serializes every replicable component under <paramref name="root"/> to JSON.</summary>
    public static string Capture(Transform root)
    {
        JArray components = new JArray();
        Component[] all = root.GetComponentsInChildren<Component>(true);
        for (int Index = 0; Index < all.Length; Index++)
        {
            Component component = all[Index];
            if (component == null)
            {
                continue;
            }
            Type type = component.GetType();
            if (SkippedTypeNames.Contains(type.FullName))
            {
                continue;
            }
            try
            {
                JObject entry = new JObject
                {
                    ["type"] = type.AssemblyQualifiedName,
                    ["path"] = BasisGenericAvatarData.GetPathRelativeTo(root, component.transform),
                    ["index"] = ComponentIndexOnNode(component),
                };
                if (component is Behaviour behaviour)
                {
                    entry["enabled"] = behaviour.enabled;
                }
                else if (component is Collider collider)
                {
                    entry["enabled"] = collider.enabled;
                }
                entry["fields"] = CaptureFields(component, type, root, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
                components.Add(entry);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Component replication: capturing {type.Name} on '{component.name}' failed: {ex.Message}");
            }
        }
        return components.Count == 0 ? null : components.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static JObject CaptureFields(object instance, Type type, Transform root, int depth, HashSet<object> visited)
    {
        JObject fields = new JObject();
        foreach (FieldInfo field in SerializableFields(type))
        {
            // Per-field isolation: an unreadable or misbehaving field (creators ship
            // unassigned references all the time) must cost that field, not the component.
            try
            {
                object value = field.GetValue(instance);
                JToken encoded = EncodeValue(value, field.FieldType, root, depth, visited);
                if (encoded != null)
                {
                    fields[field.Name] = encoded;
                }
            }
            catch (Exception ex)
            {
                BasisDebug.Log($"Component replication: skipping field {type.Name}.{field.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return fields;
    }

    private static JToken EncodeValue(object value, Type declaredType, Transform root, int depth, HashSet<object> visited)
    {
        if (depth > MaxDepth)
        {
            return null;
        }
        if (value == null)
        {
            return JValue.CreateNull();
        }
        // Unity fake-null: an unassigned/destroyed UnityEngine.Object is a live C# instance
        // that throws (UnassignedReferenceException/NRE) the moment its members are touched.
        // Unity's own boolean operator is the truth test — encode it as plain null.
        if (value is UnityEngine.Object unityValue && !unityValue)
        {
            return JValue.CreateNull();
        }
        Type type = value.GetType();

        if (type.IsPrimitive || value is string || value is decimal)
        {
            return new JValue(value);
        }
        if (type.IsEnum)
        {
            return new JValue(Convert.ToInt64(value));
        }
        if (value is LayerMask layerMask)
        {
            return new JValue(layerMask.value);
        }
        if (value is AnimationCurve curve)
        {
            JArray keys = new JArray();
            foreach (Keyframe key in curve.keys)
            {
                keys.Add(new JObject
                {
                    ["t"] = key.time, ["v"] = key.value,
                    ["i"] = key.inTangent, ["o"] = key.outTangent,
                    ["iw"] = key.inWeight, ["ow"] = key.outWeight, ["wm"] = (int)key.weightedMode,
                });
            }
            return new JObject { ["$curve"] = keys, ["pre"] = (int)curve.preWrapMode, ["post"] = (int)curve.postWrapMode };
        }
        if (value is UnityEngine.Object unityObject)
        {
            return EncodeUnityObject(unityObject, root, depth, visited);
        }
        if (value is IList list)
        {
            JArray array = new JArray();
            foreach (object element in list)
            {
                JToken encoded = EncodeValue(element, element?.GetType() ?? typeof(object), root, depth + 1, visited);
                array.Add(encoded ?? JValue.CreateNull());
            }
            return array;
        }
        if (type.IsValueType || type.GetCustomAttribute<SerializableAttribute>() != null)
        {
            if (!type.IsValueType)
            {
                if (visited.Contains(value))
                {
                    return null;
                }
                visited.Add(value);
            }
            return CaptureFields(value, type, root, depth + 1, visited);
        }
        return null;
    }

    private static JToken EncodeUnityObject(UnityEngine.Object unityObject, Transform root, int depth, HashSet<object> visited)
    {
        if (!unityObject)
        {
            return JValue.CreateNull();
        }
        if (unityObject is GameObject gameObject)
        {
            string path = BasisGenericAvatarData.GetPathRelativeTo(root, gameObject.transform);
            return path == null ? null : new JObject { ["$go"] = path };
        }
        if (unityObject is Transform transform)
        {
            string path = BasisGenericAvatarData.GetPathRelativeTo(root, transform);
            return path == null ? null : new JObject { ["$node"] = path };
        }
        if (unityObject is Component component)
        {
            string path = BasisGenericAvatarData.GetPathRelativeTo(root, component.transform);
            if (path == null)
            {
                return null;
            }
            return new JObject
            {
                ["$comp"] = path,
                ["$type"] = component.GetType().AssemblyQualifiedName,
                ["$index"] = ComponentIndexOnNode(component),
            };
        }
        if (unityObject is ScriptableObject scriptableObject)
        {
            if (visited.Contains(scriptableObject))
            {
                return null;
            }
            visited.Add(scriptableObject);
            return new JObject
            {
                ["$so"] = scriptableObject.GetType().AssemblyQualifiedName,
                ["fields"] = CaptureFields(scriptableObject, scriptableObject.GetType(), root, depth + 1, visited),
            };
        }
        // Meshes, materials, textures, clips: the GLB carries what it can of these; a
        // component field pointing at one cannot travel.
        BasisDebug.Log($"Component replication: dropping unsupported asset reference ({unityObject.GetType().Name} '{unityObject.name}').");
        return null;
    }

    // ─────────────────────────── apply ───────────────────────────

    /// <summary>
    /// Rebuilds the captured components onto an imported hierarchy: first pass adds every
    /// component (so component-to-component references resolve regardless of order), second
    /// pass fills fields. <paramref name="policeCheck"/> is the same selector the ContentPolice
    /// removal walk uses and is mandatory — without it nothing is replicated (fail closed),
    /// with it every type passes the walk's exact approval predicate before it is constructed.
    /// </summary>
    public static void Apply(string componentsJson, Transform root, ContentPoliceSelector policeCheck)
    {
        if (string.IsNullOrEmpty(componentsJson) || root == null)
        {
            return;
        }
        if (policeCheck == null)
        {
            BasisDebug.LogError("Component replication: no ContentPolice selector available — replicating nothing (fail closed).");
            return;
        }
        if (componentsJson.Length > MaxJsonBytes)
        {
            BasisDebug.LogError($"Component replication: payload {componentsJson.Length:N0} chars exceeds cap {MaxJsonBytes:N0} — replicating nothing.");
            return;
        }
        JArray components;
        try
        {
            components = JArray.Parse(componentsJson);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Component replication: payload parse failed: {ex.Message}");
            return;
        }
        if (components.Count > MaxComponents)
        {
            BasisDebug.LogError($"Component replication: {components.Count} components exceeds cap {MaxComponents} — replicating nothing.");
            return;
        }

        ApplyContext context = new ApplyContext { Root = root };
        List<(JObject entry, Component instance)> created = new List<(JObject, Component)>();
        foreach (JToken token in components)
        {
            if (token is not JObject entry)
            {
                continue;
            }
            string typeName = entry.Value<string>("type");
            Type type = ResolveType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                BasisDebug.Log($"Component replication: type unavailable on this client, skipping: {typeName}");
                continue;
            }
            // Capture never emits these, so a payload naming one is crafted. Checked before the
            // approved list because most of them are legitimately approved for avatars, and
            // AddComponent on a Transform-derived type rewrites the node's existing Transform.
            if (SkippedTypeNames.Contains(type.FullName))
            {
                BasisDebug.LogErrorUnreported($"Component replication: refusing non-replicable type {type.FullName}.");
                continue;
            }
            // The removal walk's exact predicate, applied before construction instead of
            // after — a disallowed type never exists, not even on the hidden template.
            if (!policeCheck.ApprovedTypeNames.Contains(type.FullName))
            {
                BasisDebug.LogErrorUnreported($"Component {type.FullName} is not approved and will not be replicated. Request the {Application.productName} team to add it to the approved list, or add it yourself!", BasisDebug.LogTag.System);
                continue;
            }
            Transform node = BasisGenericAvatarData.ResolveByPath(root, entry.Value<string>("path"));
            if (node == null)
            {
                BasisDebug.LogError($"Component replication: node '{entry.Value<string>("path")}' not found for {type.Name}.");
                continue;
            }
            Component instance;
            try
            {
                instance = node.gameObject.AddComponent(type);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Component replication: AddComponent {type.Name} failed: {ex.Message}");
                continue;
            }
            if (instance == null)
            {
                continue;
            }
            created.Add((entry, instance));
        }

        foreach ((JObject entry, Component instance) in created)
        {
            try
            {
                if (entry["fields"] is JObject fields)
                {
                    ApplyFields(instance, instance.GetType(), fields, context);
                }
                JToken enabledToken = entry["enabled"];
                if (enabledToken != null)
                {
                    if (instance is Behaviour behaviour)
                    {
                        behaviour.enabled = enabledToken.Value<bool>();
                    }
                    else if (instance is Collider collider)
                    {
                        collider.enabled = enabledToken.Value<bool>();
                    }
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Component replication: populating {instance.GetType().Name} failed: {ex.Message}");
            }
        }
    }

    private static void ApplyFields(object instance, Type type, JObject fields, ApplyContext context)
    {
        foreach (FieldInfo field in SerializableFields(type))
        {
            JToken token = fields[field.Name];
            if (token == null)
            {
                continue;
            }
            bool assigned = TryDecodeValue(token, field.FieldType, context, () => field.GetValue(instance), out object decoded);
            if (assigned)
            {
                try
                {
                    field.SetValue(instance, decoded);
                }
                catch (Exception ex)
                {
                    BasisDebug.Log($"Component replication: could not set {type.Name}.{field.Name}: {ex.Message}");
                }
            }
        }
    }

    private static bool TryDecodeValue(JToken token, Type targetType, ApplyContext context, Func<object> currentValue, out object decoded)
    {
        decoded = null;
        if (token.Type == JTokenType.Null)
        {
            return !targetType.IsValueType;
        }

        if (targetType.IsPrimitive || targetType == typeof(string) || targetType == typeof(decimal))
        {
            decoded = ((JValue)token).ToObject(targetType);
            return true;
        }
        if (targetType.IsEnum)
        {
            decoded = Enum.ToObject(targetType, token.Value<long>());
            return true;
        }
        if (targetType == typeof(LayerMask))
        {
            decoded = (LayerMask)token.Value<int>();
            return true;
        }
        if (targetType == typeof(AnimationCurve))
        {
            if (token is not JObject curveObject || curveObject["$curve"] is not JArray keyArray)
            {
                return false;
            }
            Keyframe[] keys = new Keyframe[keyArray.Count];
            for (int Index = 0; Index < keyArray.Count; Index++)
            {
                JObject key = (JObject)keyArray[Index];
                keys[Index] = new Keyframe(key.Value<float>("t"), key.Value<float>("v"), key.Value<float>("i"), key.Value<float>("o"), key.Value<float>("iw"), key.Value<float>("ow"))
                {
                    weightedMode = (WeightedMode)key.Value<int>("wm"),
                };
            }
            decoded = new AnimationCurve(keys)
            {
                preWrapMode = (WrapMode)curveObject.Value<int>("pre"),
                postWrapMode = (WrapMode)curveObject.Value<int>("post"),
            };
            return true;
        }
        if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
        {
            return TryDecodeUnityObject(token, targetType, context, out decoded);
        }
        if (targetType.IsArray)
        {
            if (token is not JArray arrayToken || arrayToken.Count > MaxListElements)
            {
                return false;
            }
            Type elementType = targetType.GetElementType();
            Array array = Array.CreateInstance(elementType, arrayToken.Count);
            for (int Index = 0; Index < arrayToken.Count; Index++)
            {
                if (TryDecodeValue(arrayToken[Index], elementType, context, () => null, out object element))
                {
                    array.SetValue(element, Index);
                }
            }
            decoded = array;
            return true;
        }
        if (typeof(IList).IsAssignableFrom(targetType) && targetType.IsGenericType)
        {
            if (token is not JArray listToken || listToken.Count > MaxListElements)
            {
                return false;
            }
            Type elementType = targetType.GetGenericArguments()[0];
            IList list = (IList)Activator.CreateInstance(targetType);
            foreach (JToken elementToken in listToken)
            {
                if (TryDecodeValue(elementToken, elementType, context, () => null, out object element))
                {
                    list.Add(element);
                }
                else
                {
                    list.Add(elementType.IsValueType ? Activator.CreateInstance(elementType) : null);
                }
            }
            decoded = list;
            return true;
        }
        if (token is JObject nested && (targetType.IsValueType || targetType.GetCustomAttribute<SerializableAttribute>() != null))
        {
            object target = currentValue?.Invoke();
            if (target == null || target.GetType() != targetType)
            {
                try
                {
                    target = Activator.CreateInstance(targetType);
                }
                catch
                {
                    return false;
                }
            }
            ApplyFields(target, targetType, nested, context);
            decoded = target;
            return true;
        }
        return false;
    }

    private static bool TryDecodeUnityObject(JToken token, Type targetType, ApplyContext context, out object decoded)
    {
        decoded = null;
        if (token is not JObject reference)
        {
            return false;
        }
        string nodePath = reference.Value<string>("$node");
        if (nodePath != null)
        {
            Transform node = BasisGenericAvatarData.ResolveByPath(context.Root, nodePath);
            decoded = node;
            return node != null;
        }
        string gameObjectPath = reference.Value<string>("$go");
        if (gameObjectPath != null)
        {
            Transform node = BasisGenericAvatarData.ResolveByPath(context.Root, gameObjectPath);
            decoded = node != null ? node.gameObject : null;
            return node != null;
        }
        string componentPath = reference.Value<string>("$comp");
        if (componentPath != null)
        {
            Transform node = BasisGenericAvatarData.ResolveByPath(context.Root, componentPath);
            Type componentType = ResolveType(reference.Value<string>("$type"));
            if (node == null || componentType == null)
            {
                return false;
            }
            Component[] siblings = node.GetComponents(componentType);
            int index = reference.Value<int?>("$index") ?? 0;
            decoded = index >= 0 && index < siblings.Length ? siblings[index] : (siblings.Length > 0 ? siblings[0] : null);
            return decoded != null;
        }
        string scriptableType = reference.Value<string>("$so");
        if (scriptableType != null)
        {
            // Inline ScriptableObjects only materialize into fields that declare a specific
            // SO subclass the payload type actually fits — a generic Object/ScriptableObject
            // field cannot be used to instantiate arbitrary client types (CreateInstance runs
            // the type's OnEnable). Budgeted so a hostile payload can't spam instances.
            Type type = ResolveType(scriptableType);
            if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
            {
                return false;
            }
            if (targetType == typeof(ScriptableObject) || targetType == typeof(UnityEngine.Object) || !targetType.IsAssignableFrom(type))
            {
                BasisDebug.Log($"Component replication: refusing inline ScriptableObject {type.FullName} for field type {targetType.Name}.");
                return false;
            }
            if (context.ScriptableObjectBudget <= 0)
            {
                BasisDebug.LogError("Component replication: inline ScriptableObject budget exhausted.");
                return false;
            }
            context.ScriptableObjectBudget--;
            ScriptableObject instance = ScriptableObject.CreateInstance(type);
            if (reference["fields"] is JObject fields)
            {
                ApplyFields(instance, type, fields, context);
            }
            decoded = instance;
            return true;
        }
        return false;
    }

    // ─────────────────────────── shared ───────────────────────────

    private static IEnumerable<FieldInfo> SerializableFields(Type type)
    {
        Type current = type;
        while (current != null && current != typeof(object) && current != typeof(Component) && current != typeof(MonoBehaviour) && current != typeof(ScriptableObject))
        {
            FieldInfo[] declared = current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (int Index = 0; Index < declared.Length; Index++)
            {
                FieldInfo field = declared[Index];
                if (field.IsDefined(typeof(NonSerializedAttribute), false) || field.IsInitOnly)
                {
                    continue;
                }
                if (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null)
                {
                    continue;
                }
                // Never carried, in either direction: persistent UnityEvent calls are exactly
                // what ContentPolice's listener scrub removes from loaded content — a remote
                // payload must not be able to author them.
                if (typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(field.FieldType))
                {
                    continue;
                }
                yield return field;
            }
            current = current.BaseType;
        }
    }

    private static int ComponentIndexOnNode(Component component)
    {
        Component[] siblings = component.GetComponents(component.GetType());
        for (int Index = 0; Index < siblings.Length; Index++)
        {
            if (siblings[Index] == component)
            {
                return Index;
            }
        }
        return 0;
    }

    private static Type ResolveType(string assemblyQualifiedName)
    {
        if (string.IsNullOrEmpty(assemblyQualifiedName))
        {
            return null;
        }
        Type type = Type.GetType(assemblyQualifiedName, false);
        if (type != null)
        {
            return type;
        }
        // Version drift between the building SDK and this client: fall back to scanning by
        // full name.
        int comma = assemblyQualifiedName.IndexOf(',');
        string fullName = comma > 0 ? assemblyQualifiedName.Substring(0, comma) : assemblyQualifiedName;
        foreach (Assembly assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
        {
            type = assembly.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }
        }
        return null;
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object a, object b) => ReferenceEquals(a, b);
        public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
}
