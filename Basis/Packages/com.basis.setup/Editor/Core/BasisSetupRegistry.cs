using System;
using System.Collections.Generic;
using System.Linq;

namespace Basis.Setup
{
    public static class BasisSetupRegistry
    {
        private static List<IBasisSetupModule> _modules;

        public static IReadOnlyList<IBasisSetupModule> Modules
        {
            get
            {
                if (_modules == null)
                {
                    Rebuild();
                }

                return _modules;
            }
        }

        public static void Rebuild()
        {
            var found = new List<IBasisSetupModule>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];
                    if (type == null || type.IsAbstract || type.IsInterface)
                    {
                        continue;
                    }

                    if (!typeof(IBasisSetupModule).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (type.GetConstructor(Type.EmptyTypes) == null)
                    {
                        continue;
                    }

                    try
                    {
                        found.Add((IBasisSetupModule)Activator.CreateInstance(type));
                    }
                    catch
                    {
                    }
                }
            }

            _modules = found
                .OrderBy(m => m.Order)
                .ThenBy(m => m.Category, StringComparer.Ordinal)
                .ThenBy(m => m.DisplayName, StringComparer.Ordinal)
                .ToList();
        }
    }
}
