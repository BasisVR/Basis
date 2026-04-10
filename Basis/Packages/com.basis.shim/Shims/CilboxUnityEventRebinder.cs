#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Cilbox;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace Basis.Shims
{
    internal static class CilboxUnityEventRebinder
    {
        [Serializable]
        private sealed class CapturedPersistentCall
        {
            public string HostGameObjectPath;
            public string HostComponentTypeName;
            public int HostComponentOrdinal;
            public string PersistentCallPath;
            public string TargetGameObjectPath;
            public string TargetClassName;
            public int TargetComponentOrdinal;
            public string MethodName;
            public string FullSignature;
            public PersistentListenerMode ListenerMode;
            public CilboxUnityEventArgumentKind ArgumentKind;
            public bool BoolArgument;
            public int IntArgument;
            public float FloatArgument;
            public string StringArgument;
            public string ObjectSceneGameObjectPath;
            public string ObjectSceneComponentTypeName;
            public int ObjectSceneComponentOrdinal = -1;
            public string ObjectGlobalId;
            public string CilboxObjectArgumentGameObjectPath;
            public string CilboxObjectArgumentClassName;
            public int CilboxObjectArgumentComponentOrdinal = -1;
        }

        [Serializable]
        private sealed class CapturedPersistentCallCollection
        {
            public CapturedPersistentCall[] Calls;
        }

        public static List<object> CaptureForRoot(GameObject root)
        {
            List<CapturedPersistentCall> captured = new List<CapturedPersistentCall>();
            if (root == null)
            {
                return Box(captured);
            }

            Component[] components = root.GetComponentsInChildren<Component>(true);
            CaptureForComponents(components, captured);
            return Box(captured);
        }

        public static List<object> CaptureForScene(Scene scene)
        {
            List<CapturedPersistentCall> captured = new List<CapturedPersistentCall>();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return Box(captured);
            }

            List<Component> components = new List<Component>();
            GameObject[] roots = scene.GetRootGameObjects();
            int rootCount = roots.Length;
            for (int i = 0; i < rootCount; i++)
            {
                GameObject root = roots[i];
                if (root == null)
                {
                    continue;
                }

                components.AddRange(root.GetComponentsInChildren<Component>(true));
            }

            CaptureForComponents(components.ToArray(), captured);
            return Box(captured);
        }

        public static string SerializeCapturedCalls(List<object> boxedCalls)
        {
            if (boxedCalls == null || boxedCalls.Count == 0)
            {
                return string.Empty;
            }

            List<CapturedPersistentCall> calls = new List<CapturedPersistentCall>(boxedCalls.Count);
            int length = boxedCalls.Count;
            for (int i = 0; i < length; i++)
            {
                if (boxedCalls[i] is CapturedPersistentCall captured)
                {
                    calls.Add(captured);
                }
            }

            return JsonUtility.ToJson(new CapturedPersistentCallCollection { Calls = calls.ToArray() });
        }

        public static List<object> DeserializeCapturedCalls(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<object>();
            }

            CapturedPersistentCallCollection collection = JsonUtility.FromJson<CapturedPersistentCallCollection>(json);
            if (collection?.Calls == null || collection.Calls.Length == 0)
            {
                return new List<object>();
            }

            List<object> boxed = new List<object>(collection.Calls.Length);
            int length = collection.Calls.Length;
            for (int i = 0; i < length; i++)
            {
                boxed.Add(collection.Calls[i]);
            }

            return boxed;
        }

        public static void RemoveShimsFromRoot(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            CilboxUnityEventShim[] shims = root.GetComponentsInChildren<CilboxUnityEventShim>(true);
            int length = shims.Length;
            for (int i = 0; i < length; i++)
            {
                CilboxUnityEventShim shim = shims[i];
                if (shim != null)
                {
                    UnityEngine.Object.DestroyImmediate(shim);
                }
            }
        }

        public static void RemoveShimsFromScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            int length = roots.Length;
            for (int i = 0; i < length; i++)
            {
                RemoveShimsFromRoot(roots[i]);
            }
        }

        public static void ApplyCapturedCalls(Scene scene, List<object> boxedCalls)
        {
            if (!scene.IsValid() || !scene.isLoaded || boxedCalls == null || boxedCalls.Count == 0)
            {
                return;
            }

            int length = boxedCalls.Count;
            for (int i = 0; i < length; i++)
            {
                if (boxedCalls[i] is CapturedPersistentCall captured)
                {
                    ApplyCapturedCall(scene, captured);
                }
            }
        }

        private static List<object> Box(List<CapturedPersistentCall> captured)
        {
            List<object> boxed = new List<object>(captured.Count);
            int length = captured.Count;
            for (int i = 0; i < length; i++)
            {
                boxed.Add(captured[i]);
            }

            return boxed;
        }

        private static void CaptureForComponents(Component[] components, List<CapturedPersistentCall> captured)
        {
            int length = components.Length;
            for (int i = 0; i < length; i++)
            {
                Component component = components[i];
                if (component == null || component is CilboxUnityEventShim)
                {
                    continue;
                }

                if (component is MonoBehaviour monoBehaviour && CilboxUtil.HasCilboxableAttribute(monoBehaviour.GetType()))
                {
                    continue;
                }

                CaptureForComponent(component, captured);
            }
        }

        private static void CaptureForComponent(Component component, List<CapturedPersistentCall> captured)
        {
            SerializedObject serializedObject = new SerializedObject(component);
            SerializedProperty iterator = serializedObject.GetIterator();
            HashSet<string> seenCallArrays = new HashSet<string>(StringComparer.Ordinal);
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = true;
                if (iterator.propertyType != SerializedPropertyType.Generic)
                {
                    continue;
                }

                SerializedProperty callsArray = iterator.FindPropertyRelative("m_PersistentCalls.m_Calls");
                if (callsArray == null || !callsArray.isArray || !seenCallArrays.Add(callsArray.propertyPath))
                {
                    continue;
                }

                int callCount = callsArray.arraySize;
                for (int callIndex = 0; callIndex < callCount; callIndex++)
                {
                    SerializedProperty callProperty = callsArray.GetArrayElementAtIndex(callIndex);
                    if (callProperty != null && TryCaptureCall(component, callProperty, out CapturedPersistentCall persistentCall))
                    {
                        captured.Add(persistentCall);
                    }
                }
            }
        }

        private static bool TryCaptureCall(Component hostComponent, SerializedProperty callProperty, out CapturedPersistentCall captured)
        {
            captured = null;

            SerializedProperty targetProperty = callProperty.FindPropertyRelative("m_Target");
            if (targetProperty?.objectReferenceValue is not MonoBehaviour targetMonoBehaviour)
            {
                return false;
            }

            if (!CilboxUtil.HasCilboxableAttribute(targetMonoBehaviour.GetType()))
            {
                return false;
            }

            SerializedProperty modeProperty = callProperty.FindPropertyRelative("m_Mode");
            if (modeProperty == null)
            {
                return false;
            }

            PersistentListenerMode listenerMode = (PersistentListenerMode)modeProperty.intValue;
            if (listenerMode == PersistentListenerMode.EventDefined)
            {
                Debug.LogWarning(
                    $"[{nameof(CilboxUnityEventRebinder)}] Skipping dynamic UnityEvent listener {targetMonoBehaviour.GetType().FullName}.{callProperty.FindPropertyRelative("m_MethodName")?.stringValue} on {hostComponent.name}"
                );
                return false;
            }

            int hostComponentOrdinal = GetComponentOrdinal(hostComponent);
            int targetComponentOrdinal = GetComponentOrdinal(targetMonoBehaviour);
            if (hostComponentOrdinal < 0 || targetComponentOrdinal < 0)
            {
                return false;
            }

            string methodName = callProperty.FindPropertyRelative("m_MethodName")?.stringValue;
            string objectArgumentAssemblyTypeName = callProperty.FindPropertyRelative("m_Arguments.m_ObjectArgumentAssemblyTypeName")?.stringValue;
            MethodInfo methodInfo = ResolvePersistentCallMethod(targetMonoBehaviour.GetType(), methodName, listenerMode, objectArgumentAssemblyTypeName);

            captured = new CapturedPersistentCall
            {
                HostGameObjectPath = GetGameObjectPath(hostComponent.gameObject),
                HostComponentTypeName = hostComponent.GetType().FullName,
                HostComponentOrdinal = hostComponentOrdinal,
                PersistentCallPath = callProperty.propertyPath,
                TargetGameObjectPath = GetGameObjectPath(targetMonoBehaviour.gameObject),
                TargetClassName = targetMonoBehaviour.GetType().FullName,
                TargetComponentOrdinal = targetComponentOrdinal,
                MethodName = methodName,
                FullSignature = methodInfo?.ToString(),
                ListenerMode = listenerMode
            };

            PopulateArgumentData(callProperty, captured);
            Debug.Log(
                $"[{nameof(CilboxUnityEventRebinder)}] Captured {captured.HostComponentTypeName}.{captured.PersistentCallPath} -> {captured.TargetClassName}.{captured.MethodName} on {captured.TargetGameObjectPath}"
            );
            return true;
        }

        private static void PopulateArgumentData(SerializedProperty callProperty, CapturedPersistentCall captured)
        {
            switch (captured.ListenerMode)
            {
                case PersistentListenerMode.Void:
                    captured.ArgumentKind = CilboxUnityEventArgumentKind.None;
                    return;
                case PersistentListenerMode.Bool:
                    captured.ArgumentKind = CilboxUnityEventArgumentKind.Bool;
                    captured.BoolArgument = callProperty.FindPropertyRelative("m_Arguments.m_BoolArgument")?.boolValue ?? false;
                    return;
                case PersistentListenerMode.Int:
                    captured.ArgumentKind = CilboxUnityEventArgumentKind.Int;
                    captured.IntArgument = callProperty.FindPropertyRelative("m_Arguments.m_IntArgument")?.intValue ?? 0;
                    return;
                case PersistentListenerMode.Float:
                    captured.ArgumentKind = CilboxUnityEventArgumentKind.Float;
                    captured.FloatArgument = callProperty.FindPropertyRelative("m_Arguments.m_FloatArgument")?.floatValue ?? 0f;
                    return;
                case PersistentListenerMode.String:
                    captured.ArgumentKind = CilboxUnityEventArgumentKind.String;
                    captured.StringArgument = callProperty.FindPropertyRelative("m_Arguments.m_StringArgument")?.stringValue;
                    return;
                case PersistentListenerMode.Object:
                    captured.ArgumentKind = CilboxUnityEventArgumentKind.Object;
                    CaptureObjectArgument(callProperty.FindPropertyRelative("m_Arguments.m_ObjectArgument")?.objectReferenceValue, captured);
                    return;
                default:
                    captured.ArgumentKind = CilboxUnityEventArgumentKind.None;
                    return;
            }
        }

        private static void CaptureObjectArgument(UnityEngine.Object objectArgument, CapturedPersistentCall captured)
        {
            if (objectArgument == null)
            {
                return;
            }

            if (objectArgument is MonoBehaviour objectMonoBehaviour && CilboxUtil.HasCilboxableAttribute(objectMonoBehaviour.GetType()))
            {
                int objectComponentOrdinal = GetComponentOrdinal(objectMonoBehaviour);
                if (objectComponentOrdinal >= 0)
                {
                    captured.CilboxObjectArgumentGameObjectPath = GetGameObjectPath(objectMonoBehaviour.gameObject);
                    captured.CilboxObjectArgumentClassName = objectMonoBehaviour.GetType().FullName;
                    captured.CilboxObjectArgumentComponentOrdinal = objectComponentOrdinal;
                }

                return;
            }

            if (objectArgument is Component component)
            {
                captured.ObjectSceneGameObjectPath = GetGameObjectPath(component.gameObject);
                captured.ObjectSceneComponentTypeName = component.GetType().FullName;
                captured.ObjectSceneComponentOrdinal = GetComponentOrdinal(component);
                return;
            }

            if (objectArgument is GameObject gameObject)
            {
                captured.ObjectSceneGameObjectPath = GetGameObjectPath(gameObject);
                return;
            }

            GlobalObjectId globalId = GlobalObjectId.GetGlobalObjectIdSlow(objectArgument);
            captured.ObjectGlobalId = globalId.ToString();
        }

        private static void ApplyCapturedCall(Scene scene, CapturedPersistentCall captured)
        {
            Component hostComponent = ResolveComponent(scene, captured.HostGameObjectPath, captured.HostComponentTypeName, captured.HostComponentOrdinal);
            if (hostComponent == null)
            {
                Debug.LogWarning(
                    $"[{nameof(CilboxUnityEventRebinder)}] Could not resolve host component {captured.HostComponentTypeName} at {captured.HostGameObjectPath}"
                );
                return;
            }

            CilboxProxy targetProxy = FindMatchingProxy(scene, captured.TargetGameObjectPath, captured.TargetClassName, captured.TargetComponentOrdinal);
            if (targetProxy == null)
            {
                Debug.LogError(
                    $"[{nameof(CilboxUnityEventRebinder)}] Failed to find CilboxProxy for {captured.TargetClassName}.{captured.MethodName} at {captured.TargetGameObjectPath}"
                );
                return;
            }

            UnityEngine.Object objectArgument = ResolveObjectArgument(scene, captured);
            if (
                captured.ArgumentKind == CilboxUnityEventArgumentKind.Object
                && (
                    !string.IsNullOrEmpty(captured.ObjectSceneGameObjectPath)
                    || !string.IsNullOrEmpty(captured.ObjectGlobalId)
                    || !string.IsNullOrEmpty(captured.CilboxObjectArgumentGameObjectPath)
                )
                && objectArgument == null
            )
            {
                Debug.LogError(
                    $"[{nameof(CilboxUnityEventRebinder)}] Failed to resolve object argument for {captured.TargetClassName}.{captured.MethodName}"
                );
                return;
            }

            CilboxUnityEventShim shim = hostComponent.gameObject.AddComponent<CilboxUnityEventShim>();
            shim.Configure(
                targetProxy,
                captured.TargetClassName,
                captured.MethodName,
                captured.FullSignature,
                captured.ArgumentKind,
                BuildScalarArgument(captured),
                objectArgument
            );
            EditorUtility.SetDirty(shim);

            SerializedObject serializedObject = new SerializedObject(hostComponent);
            SerializedProperty callProperty = serializedObject.FindProperty(captured.PersistentCallPath);
            if (callProperty == null)
            {
                Debug.LogWarning(
                    $"[{nameof(CilboxUnityEventRebinder)}] Could not reopen persistent call {captured.PersistentCallPath} on {hostComponent.name} after creating a shim."
                );
                return;
            }

            callProperty.FindPropertyRelative("m_Target").objectReferenceValue = shim;
            callProperty.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue = GetUnityTypeName(typeof(CilboxUnityEventShim));
            callProperty.FindPropertyRelative("m_MethodName").stringValue = nameof(CilboxUnityEventShim.Invoke);
            callProperty.FindPropertyRelative("m_Mode").intValue = (int)PersistentListenerMode.Void;

            SerializedProperty argumentsProperty = callProperty.FindPropertyRelative("m_Arguments");
            argumentsProperty.FindPropertyRelative("m_ObjectArgument").objectReferenceValue = null;
            argumentsProperty.FindPropertyRelative("m_ObjectArgumentAssemblyTypeName").stringValue = GetUnityTypeName(typeof(UnityEngine.Object));
            argumentsProperty.FindPropertyRelative("m_IntArgument").intValue = 0;
            argumentsProperty.FindPropertyRelative("m_FloatArgument").floatValue = 0f;
            argumentsProperty.FindPropertyRelative("m_StringArgument").stringValue = string.Empty;
            argumentsProperty.FindPropertyRelative("m_BoolArgument").boolValue = false;

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(hostComponent);
            Debug.Log(
                $"[{nameof(CilboxUnityEventRebinder)}] Rebound {captured.HostComponentTypeName}.{captured.PersistentCallPath} to {nameof(CilboxUnityEventShim)} on {hostComponent.gameObject.name} for {captured.TargetClassName}.{captured.MethodName}"
            );
        }

        private static UnityEngine.Object ResolveObjectArgument(Scene scene, CapturedPersistentCall captured)
        {
            if (!string.IsNullOrEmpty(captured.CilboxObjectArgumentGameObjectPath))
            {
                return FindMatchingProxy(
                    scene,
                    captured.CilboxObjectArgumentGameObjectPath,
                    captured.CilboxObjectArgumentClassName,
                    captured.CilboxObjectArgumentComponentOrdinal
                );
            }

            if (!string.IsNullOrEmpty(captured.ObjectSceneComponentTypeName))
            {
                return ResolveComponent(
                    scene,
                    captured.ObjectSceneGameObjectPath,
                    captured.ObjectSceneComponentTypeName,
                    captured.ObjectSceneComponentOrdinal
                );
            }

            if (!string.IsNullOrEmpty(captured.ObjectSceneGameObjectPath))
            {
                return ResolveGameObject(scene, captured.ObjectSceneGameObjectPath);
            }

            if (!string.IsNullOrEmpty(captured.ObjectGlobalId) && GlobalObjectId.TryParse(captured.ObjectGlobalId, out GlobalObjectId globalId))
            {
                return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
            }

            return null;
        }

        private static string BuildScalarArgument(CapturedPersistentCall captured)
        {
            switch (captured.ArgumentKind)
            {
                case CilboxUnityEventArgumentKind.Bool:
                    return captured.BoolArgument ? bool.TrueString : bool.FalseString;
                case CilboxUnityEventArgumentKind.Int:
                    return captured.IntArgument.ToString(CultureInfo.InvariantCulture);
                case CilboxUnityEventArgumentKind.Float:
                    return captured.FloatArgument.ToString("R", CultureInfo.InvariantCulture);
                case CilboxUnityEventArgumentKind.String:
                    return captured.StringArgument;
                default:
                    return null;
            }
        }

        private static MethodInfo ResolvePersistentCallMethod(
            Type targetType,
            string methodName,
            PersistentListenerMode listenerMode,
            string objectArgumentAssemblyTypeName
        )
        {
            if (targetType == null || string.IsNullOrEmpty(methodName))
            {
                return null;
            }

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type parameterType = listenerMode switch
            {
                PersistentListenerMode.Void => null,
                PersistentListenerMode.Bool => typeof(bool),
                PersistentListenerMode.Int => typeof(int),
                PersistentListenerMode.Float => typeof(float),
                PersistentListenerMode.String => typeof(string),
                PersistentListenerMode.Object => ResolveType(objectArgumentAssemblyTypeName) ?? typeof(UnityEngine.Object),
                _ => null
            };

            if (listenerMode == PersistentListenerMode.Void)
            {
                return targetType.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
            }

            if (parameterType == null)
            {
                return null;
            }

            MethodInfo exact = targetType.GetMethod(methodName, flags, null, new[] { parameterType }, null);
            if (exact != null)
            {
                return exact;
            }

            MethodInfo[] methods = targetType.GetMethods(flags);
            int length = methods.Length;
            for (int i = 0; i < length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != methodName)
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1)
                {
                    continue;
                }

                if (parameters[0].ParameterType.IsAssignableFrom(parameterType))
                {
                    return method;
                }
            }

            return null;
        }

        private static Type ResolveType(string unityTypeName)
        {
            if (string.IsNullOrWhiteSpace(unityTypeName))
            {
                return null;
            }

            Type resolved = Type.GetType(unityTypeName, false);
            if (resolved != null)
            {
                return resolved;
            }

            string typeName = unityTypeName;
            string assemblyName = null;
            int commaIndex = unityTypeName.IndexOf(',');
            if (commaIndex >= 0)
            {
                typeName = unityTypeName.Substring(0, commaIndex).Trim();
                assemblyName = unityTypeName[(commaIndex + 1)..].Trim();
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            int length = assemblies.Length;
            for (int i = 0; i < length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assemblyName != null && !string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal))
                {
                    continue;
                }

                resolved = assembly.GetType(typeName, false);
                if (resolved != null)
                {
                    return resolved;
                }
            }

            return null;
        }

        private static int GetComponentOrdinal(Component component)
        {
            Component[] components = component.gameObject.GetComponents(component.GetType());
            int length = components.Length;
            for (int i = 0; i < length; i++)
            {
                if (components[i] == component)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string GetGameObjectPath(GameObject gameObject)
        {
            string path = gameObject.name;
            Transform current = gameObject.transform.parent;
            while (current != null)
            {
                path = $"{current.name}/{path}";
                current = current.parent;
            }

            return path;
        }

        private static GameObject ResolveGameObject(Scene scene, string path)
        {
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(path))
            {
                return null;
            }

            string[] parts = path.Split('/');
            GameObject current = null;
            GameObject[] roots = scene.GetRootGameObjects();
            int rootCount = roots.Length;
            for (int i = 0; i < rootCount; i++)
            {
                GameObject root = roots[i];
                if (root != null && root.name == parts[0])
                {
                    current = root;
                    break;
                }
            }

            if (current == null)
            {
                return null;
            }

            for (int partIndex = 1; partIndex < parts.Length; partIndex++)
            {
                Transform child = current.transform.Find(parts[partIndex]);
                if (child == null)
                {
                    return null;
                }

                current = child.gameObject;
            }

            return current;
        }

        private static Component ResolveComponent(Scene scene, string gameObjectPath, string componentTypeName, int componentOrdinal)
        {
            GameObject gameObject = ResolveGameObject(scene, gameObjectPath);
            if (gameObject == null || string.IsNullOrEmpty(componentTypeName) || componentOrdinal < 0)
            {
                return null;
            }

            Type componentType = ResolveType(componentTypeName);
            if (componentType == null)
            {
                return null;
            }

            Component[] components = gameObject.GetComponents(componentType);
            if (componentOrdinal >= components.Length)
            {
                return null;
            }

            return components[componentOrdinal];
        }

        private static CilboxProxy FindMatchingProxy(Scene scene, string gameObjectPath, string className, int componentOrdinal)
        {
            GameObject gameObject = ResolveGameObject(scene, gameObjectPath);
            if (gameObject == null || string.IsNullOrEmpty(className) || componentOrdinal < 0)
            {
                return null;
            }

            CilboxProxy[] proxies = gameObject.GetComponents<CilboxProxy>();
            int ordinal = 0;
            int length = proxies.Length;
            for (int i = 0; i < length; i++)
            {
                CilboxProxy proxy = proxies[i];
                if (proxy == null || !string.Equals(proxy.className, className, StringComparison.Ordinal))
                {
                    continue;
                }

                if (ordinal == componentOrdinal)
                {
                    return proxy;
                }

                ordinal++;
            }

            return null;
        }

        private static string GetUnityTypeName(Type type)
        {
            return $"{type.FullName}, {type.Assembly.GetName().Name}";
        }
    }
}
#endif
