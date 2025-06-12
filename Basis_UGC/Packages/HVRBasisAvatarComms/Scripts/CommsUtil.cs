using System;
using System.Linq;
using Basis.Scripts.BasisSdk;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HVR.Basis.Comms
{
    internal class CommsUtil
    {
        internal static T GetOrCreateSceneInstance<T>(ref T instance) where T : Component
        {
            if (instance != null) return instance;

            var go = new GameObject($"HVR.{typeof(T).Name}");
            Object.DontDestroyOnLoad(go);
            instance = go.AddComponent<T>();

            return instance;
        }

        internal static BasisAvatar GetAvatar(Component component)
        {
            return component.GetComponentInParent<BasisAvatar>(true);
        }

        internal static FeatureNetworking FeatureNetworkingFromAvatar(BasisAvatar avatar)
        {
            return avatar.GetComponentInChildren<FeatureNetworking>(true);
        }

        /// Semantically used to sanitize a serializable field of objects provided by an End User.<br/>
        /// Given a nullable array of Unity Objects that may contain null-Destroy Objects,
        /// return a non-null array of Unity Objects that does not contain null-Destroy Objects.
        public static T[] SlowSanitizeEndUserProvidedObjectArray<T>(T[] objectsNullable) where T : Object
        {
            if (objectsNullable == null) return Array.Empty<T>();
            
            return objectsNullable.Where(t => t).ToArray();
        }

        /// Semantically used to sanitize a serializable field of structs provided by an End User.<br/>
        /// Returns itself, or an empty array if the parameter is null.
        public static T[] SlowSanitizeEndUserProvidedStructArray<T>(T[] structuresNullable) where T : struct
        {
            if (structuresNullable == null) return Array.Empty<T>();
            
            return structuresNullable;
        }
    }
}