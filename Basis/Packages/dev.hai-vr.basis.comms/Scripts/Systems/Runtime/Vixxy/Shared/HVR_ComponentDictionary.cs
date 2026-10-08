using System;
using System.Collections.Generic;
using UnityEngine;

namespace HVR.Vixxy
{
    public static class HVR_ComponentDictionary
    {
        private static readonly Dictionary<string, Type> ComponentDictionary = new();

        public static bool TryGetComponentType(string fullClassName, out Type foundType)
        {
            if (!ComponentDictionary.TryGetValue(fullClassName, out foundType))
            {
                foundType = FindComponentType(fullClassName);
                ComponentDictionary[fullClassName] = foundType;
            }
            return foundType != null;
        }

        private static Type FindComponentType(string fullClassName)
        {
            var assemblies = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
            foreach (var assembly in assemblies)
            {
                Type type;
                try
                {
                    type = assembly.GetType(fullClassName, false);
                }
                catch (ArgumentException)
                {
                    return null;
                }
                if (type != null && typeof(Component).IsAssignableFrom(type))
                {
                    return type;
                }
            }
            return null;
        }
    }
}
