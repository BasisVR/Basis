using System;
using System.Collections.Generic;
using System.Reflection;

namespace Basis.Network.Core
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class BasisNetworkPackageAttribute : Attribute
    {
        public readonly Type Entry;
        public BasisNetworkPackageAttribute(Type entry)
        {
            Entry = entry;
        }
    }
    public interface IBasisNetworkPackage
    {
        void Initialize();
    }
    public static class BasisNetworkPackages
    {
        private static readonly HashSet<Type> started = new HashSet<Type>();
        private static readonly List<Type> loaded = new List<Type>();
        private static readonly List<string> failures = new List<string>();
        private static readonly object gate = new object();
        public static IReadOnlyList<Type> Loaded
        {
            get { lock (gate) return loaded.ToArray(); }
        }
        public static IReadOnlyList<string> Failures
        {
            get { lock (gate) return failures.ToArray(); }
        }
        public static int Initialize(params Assembly[] assemblies)
        {
            int count = 0;
            if (assemblies == null) return count;
            foreach (Assembly assembly in assemblies)
            {
                if (assembly == null) continue;
                object[] attributes;
                try
                {
                    attributes = assembly.GetCustomAttributes(typeof(BasisNetworkPackageAttribute), false);
                }
                catch (Exception ex)
                {
                    Fail($"Could not read the network packages in {assembly.GetName().Name}: {ex.GetBaseException().Message}");
                    continue;
                }
                foreach (object attribute in attributes)
                {
                    if (Start(((BasisNetworkPackageAttribute)attribute).Entry)) count++;
                }
            }
            return count;
        }
        private static bool Start(Type entry)
        {
            if (entry == null) return false;
            lock (gate)
            {
                if (!started.Add(entry)) return false;
            }
            try
            {
                if (!(Activator.CreateInstance(entry) is IBasisNetworkPackage package))
                {
                    Fail($"Network package entry {entry.FullName} does not implement {nameof(IBasisNetworkPackage)}");
                    return false;
                }
                package.Initialize();
                lock (gate) loaded.Add(entry);
                BNL.Log($"Network package {entry.FullName} initialized");
                return true;
            }
            catch (Exception ex)
            {
                Fail($"Network package {entry.FullName} failed to initialize: {ex.GetBaseException().Message}");
                return false;
            }
        }
        private static void Fail(string message)
        {
            lock (gate) failures.Add(message);
            BNL.LogError(message);
        }
    }
}
