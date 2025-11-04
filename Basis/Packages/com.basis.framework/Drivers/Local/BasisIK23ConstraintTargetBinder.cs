using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Cached-reflection helper to set private m_targetN on BasisIK23ConstraintData.
    /// Avoids changing the constraint file and stays allocation-free in hot paths.
    /// </summary>
    internal static class BasisIK23ConstraintTargetBinder
    {
        private static bool s_Initialized;
        private static FieldInfo[] s_TargetFields; // m_target0..m_target22

        public static void InitReflectionCache()
        {
            if (s_Initialized) return;
            var t = typeof(UnityEngine.Animations.Rigging.BasisIK23ConstraintData);
            s_TargetFields = new FieldInfo[BasisIK23ConstraintData.Count];
            for (int i = 0; i < s_TargetFields.Length; i++)
            {
                string name = "m_target" + i;
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (f == null)
                    throw new MissingFieldException($"{t.Name} missing field {name}. Did the constraint definition change?");
                s_TargetFields[i] = f;
            }
            s_Initialized = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTargetTransform(ref BasisIK23ConstraintData data, int slot, Transform tr)
        {
            var fi = s_TargetFields[slot];
            var dataRef = __makeref(data);
            fi.SetValueDirect(dataRef, tr);
        }
    }
}
