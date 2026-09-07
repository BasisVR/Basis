using Basis.Scripts.Drivers;
using NUnit.Framework;
using System;
using System.Collections;
using System.Reflection;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// The shared hand-pose cells are Persistent native memory owned by a static cache, so they
    /// are freed only when the cache is told to. A script edit while playing reloads the domain
    /// without ever leaving play mode, which leaked one grid per avatar worn that session:
    /// "Found 1 leak(s) from callstack: ... BasisHandPoseGrid:TryBake".
    /// </summary>
    public class BasisAvatarModelCacheTeardownTests
    {
        [Test]
        public void Clear_FreesCellsPublishedToTheCache()
        {
            using var rig = BasisHumanoidRigFixture.Build("teardown");
            using var grid = new BasisHandPoseGrid();
            Assert.IsTrue(BasisHandPoseGrid.TryAcquire(rig.Animator, BasisHandPoseGrid.DefaultIncrement, grid));
            Assert.IsFalse(grid.OwnsCells, "an acquired grid is a view; the cache entry owns the cells");
            AtomicSafetyHandle handle = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(grid.Cells);
            Assert.IsTrue(AtomicSafetyHandle.IsHandleValid(handle));

            BasisAvatarModelCache.Clear();

            Assert.IsFalse(AtomicSafetyHandle.IsHandleValid(handle), "Clear left the published cells allocated");
        }

        [Test]
        public void Clear_FreesCellsRetiredByEviction()
        {
            using var rig = BasisHumanoidRigFixture.Build("evicted");
            using var grid = new BasisHandPoseGrid();
            Assert.IsTrue(BasisHandPoseGrid.TryAcquire(rig.Animator, BasisHandPoseGrid.DefaultIncrement, grid));
            AtomicSafetyHandle handle = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(grid.Cells);

            BasisAvatarModelCache.Remove(BasisAvatarModelCache.GetKey(rig.Animator));
            Assert.IsTrue(AtomicSafetyHandle.IsHandleValid(handle), "eviction retires rather than frees; views may still be sampling");

            BasisAvatarModelCache.Clear();

            Assert.IsFalse(AtomicSafetyHandle.IsHandleValid(handle), "Clear left the retired cells allocated");
        }

        [Test]
        public void Clear_RunsBeforeAssemblyReloadAndOnQuit()
        {
            Assert.IsTrue(IsClearSubscribed(typeof(AssemblyReloadEvents), nameof(AssemblyReloadEvents.beforeAssemblyReload)),
                "a domain reload during play mode never enters edit mode, so only this hook can free the cells first");
            Assert.IsTrue(IsClearSubscribed(typeof(Application), nameof(Application.quitting)),
                "a player build has no edit mode to return to");
        }

        const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>
        /// A field-like event keeps its handlers in a delegate field of the same name; Unity's
        /// editor events wrap them in an internal performance-tracking list that only exposes an
        /// enumerator. Both shapes are searched so the test survives either.
        /// </summary>
        static bool IsClearSubscribed(Type owner, string eventName)
        {
            bool sawStore = false;
            foreach (FieldInfo field in owner.GetFields(Any))
            {
                if (field.IsStatic == false || field.Name.IndexOf(eventName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sawStore = true;
                object store = field.GetValue(null);
                if (store == null) continue;
                if (store is Delegate handlers && ContainsClear(handlers)) return true;
                MethodInfo getEnumerator = store.GetType().GetMethod("GetEnumerator", Any, null, Type.EmptyTypes, null);
                if (getEnumerator == null || !(getEnumerator.Invoke(store, null) is IEnumerator entries)) continue;
                try
                {
                    while (entries.MoveNext())
                    {
                        if (entries.Current is Delegate entry && ContainsClear(entry)) return true;
                    }
                }
                finally
                {
                    (entries as IDisposable)?.Dispose();
                }
            }
            Assert.IsTrue(sawStore, $"{owner.Name}.{eventName} has no backing field of that name; Unity changed the event shape");
            return false;
        }

        static bool ContainsClear(Delegate handlers)
        {
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                if (handler.Method.DeclaringType == typeof(BasisAvatarModelCache) && handler.Method.Name == nameof(BasisAvatarModelCache.Clear)) return true;
            }
            return false;
        }
    }
}
