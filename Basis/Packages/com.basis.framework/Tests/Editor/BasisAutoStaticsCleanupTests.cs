using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine.Assemblies;

namespace Basis.Framework.Tests
{
    public class BasisAutoStaticsCleanupTests
    {
        const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        static bool Resettable(Type type, FieldInfo field)
        {
            if (field.IsInitOnly || field.IsLiteral || field.Name.StartsWith("__autoCleanup") || field.IsDefined(typeof(NoAutoStaticsCleanupAttribute), false)) return false;
            string name = field.Name.StartsWith("<") ? field.Name.Substring(1, field.Name.IndexOf('>') - 1) : field.Name;
            MemberInfo owner = (MemberInfo)type.GetProperty(name, Statics) ?? type.GetEvent(name, Statics);
            return owner == null || !owner.IsDefined(typeof(NoAutoStaticsCleanupAttribute), false);
        }

        [Test]
        public void EveryAnnotatedTypeGotItsGeneratedReset()
        {
            List<string> missing = new List<string>();
            int annotatedTypes = 0;
            foreach (Assembly assembly in CurrentAssemblies.GetLoadedAssemblies())
            {
                string name = assembly.GetName().Name;
                if (!name.StartsWith("Basis") && !name.StartsWith("HVR.")) continue;
                Type[] types;
                try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (Type type in types)
                {
                    if (!type.IsDefined(typeof(AutoStaticsCleanupAttribute), false)) continue;
                    annotatedTypes++;
                    if (!type.GetFields(Statics).Any(f => Resettable(type, f))) continue;
                    if (type.GetMethods(Statics).All(m => !m.Name.StartsWith("__AutoStaticsCleanup_"))) missing.Add(name + ": " + type.FullName);
                }
            }
            Assert.That(annotatedTypes, Is.GreaterThan(0));
            Assert.That(missing, Is.Empty, "Unity's AutoStaticsCleanup generator produced no reset for these types. Find warning CS8785 in that assembly's compile output and mark the field it names [NoAutoStaticsCleanup].");
        }
    }
}
