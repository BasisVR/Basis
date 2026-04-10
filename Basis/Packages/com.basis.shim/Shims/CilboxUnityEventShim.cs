using System;
using System.Globalization;
using Cilbox;
using UnityEngine;

namespace Basis.Shims
{
    public enum CilboxUnityEventArgumentKind
    {
        None,
        Bool,
        Int,
        Float,
        String,
        Object
    }

    public sealed class CilboxUnityEventShim : MonoBehaviour
    {
        [SerializeField] private CilboxProxy target;
        [SerializeField] private string className;
        [SerializeField] private string methodName;
        [SerializeField] private string fullSignature;
        [SerializeField] private CilboxUnityEventArgumentKind argumentKind;
        [SerializeField] private string scalarArgument;
        [SerializeField] private UnityEngine.Object objectArgument;

        [NonSerialized] private bool hasCachedMethodIndex;
        [NonSerialized] private uint cachedMethodIndex;

        public void Invoke()
        {
            if (target == null)
            {
                Debug.LogError($"[{nameof(CilboxUnityEventShim)}:{name}] Missing cilbox proxy target for {className}.{methodName}");
                return;
            }

            target.RuntimeProxyLoad();

            CilboxClass proxyClass = target.cls;
            if (proxyClass == null)
            {
                Debug.LogError($"[{nameof(CilboxUnityEventShim)}:{name}] Proxy class was not initialized for {className}.{methodName}");
                return;
            }

            CilboxMethod method = ResolveMethod(proxyClass);
            if (method == null)
            {
                Debug.LogError(
                    $"[{nameof(CilboxUnityEventShim)}:{name}] Could not resolve method {className}.{methodName} ({fullSignature}) on {target.gameObject.name}"
                );
                return;
            }

            object[] parameters = BuildParameters();
            if (parameters.Length == 1 && parameters[0] is CilboxProxy argumentProxy)
            {
                argumentProxy.RuntimeProxyLoad();
            }

            method.Interpret(target, parameters);
        }

        internal void Configure(
            CilboxProxy targetProxy,
            string configuredClassName,
            string configuredMethodName,
            string configuredFullSignature,
            CilboxUnityEventArgumentKind configuredArgumentKind,
            string configuredScalarArgument,
            UnityEngine.Object configuredObjectArgument
        )
        {
            target = targetProxy;
            className = configuredClassName;
            methodName = configuredMethodName;
            fullSignature = configuredFullSignature;
            argumentKind = configuredArgumentKind;
            scalarArgument = configuredScalarArgument;
            objectArgument = configuredObjectArgument;
            hasCachedMethodIndex = false;
            cachedMethodIndex = 0;
        }

        private CilboxMethod ResolveMethod(CilboxClass proxyClass)
        {
            if (hasCachedMethodIndex && cachedMethodIndex < proxyClass.methods.Length)
            {
                return proxyClass.methods[cachedMethodIndex];
            }

            if (!string.IsNullOrEmpty(fullSignature) && proxyClass.methodFullSignatureToIndex.TryGetValue(fullSignature, out uint signatureIndex))
            {
                hasCachedMethodIndex = true;
                cachedMethodIndex = signatureIndex;
                return proxyClass.methods[signatureIndex];
            }

            if (!string.IsNullOrEmpty(fullSignature))
            {
                Debug.LogWarning(
                    $"[{nameof(CilboxUnityEventShim)}:{name}] Full signature lookup failed for {className}.{methodName} ({fullSignature}), falling back to method name."
                );
            }

            if (!string.IsNullOrEmpty(methodName) && proxyClass.methodNameToIndex.TryGetValue(methodName, out uint methodIndex))
            {
                hasCachedMethodIndex = true;
                cachedMethodIndex = methodIndex;
                return proxyClass.methods[methodIndex];
            }

            return null;
        }

        private object[] BuildParameters()
        {
            switch (argumentKind)
            {
                case CilboxUnityEventArgumentKind.None:
                    return Array.Empty<object>();
                case CilboxUnityEventArgumentKind.Bool:
                    return new object[] { ParseBoolArgument() };
                case CilboxUnityEventArgumentKind.Int:
                    return new object[] { ParseIntArgument() };
                case CilboxUnityEventArgumentKind.Float:
                    return new object[] { ParseFloatArgument() };
                case CilboxUnityEventArgumentKind.String:
                    return new object[] { scalarArgument };
                case CilboxUnityEventArgumentKind.Object:
                    return new object[] { objectArgument };
                default:
                    return Array.Empty<object>();
            }
        }

        private bool ParseBoolArgument()
        {
            return string.Equals(scalarArgument, bool.TrueString, StringComparison.OrdinalIgnoreCase);
        }

        private int ParseIntArgument()
        {
            return int.TryParse(scalarArgument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
        }

        private float ParseFloatArgument()
        {
            return float.TryParse(scalarArgument, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float value)
                ? value
                : 0f;
        }
    }
}
