using NUnit.Framework;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Tests for the mirror camera registries: <see cref="BasisMirrorViewerRegistry"/> (cameras
    /// that should drive per-view mirror reflections) and <see cref="BasisMirrorReflectionCamera"/>
    /// (marker identifying reflection sources so they never drive mirrors — recursion prevention).
    /// The marker's Awake/OnDestroy are invoked via reflection because edit mode does not run
    /// MonoBehaviour lifecycle callbacks.
    /// </summary>
    public class BasisMirrorRegistryTests
    {
        private readonly List<GameObject> spawned = new List<GameObject>();
        private readonly List<UnityEngine.Camera> registered = new List<UnityEngine.Camera>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < registered.Count; i++)
            {
                BasisMirrorViewerRegistry.Unregister(registered[i]);
            }
            registered.Clear();

            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] != null)
                {
                    Object.DestroyImmediate(spawned[i]);
                }
            }
            spawned.Clear();
        }

        private UnityEngine.Camera CreateCamera(string name)
        {
            GameObject go = new GameObject(name, typeof(UnityEngine.Camera));
            spawned.Add(go);
            return go.GetComponent<UnityEngine.Camera>();
        }

        private UnityEngine.Camera RegisterNewViewer(string name)
        {
            UnityEngine.Camera camera = CreateCamera(name);
            BasisMirrorViewerRegistry.Register(camera);
            registered.Add(camera);
            return camera;
        }

        private static List<UnityEngine.Camera> Collect()
        {
            List<UnityEngine.Camera> result = new List<UnityEngine.Camera>();
            BasisMirrorViewerRegistry.CollectInto(result);
            return result;
        }

        [Test]
        public void Viewer_RegisteredActiveEnabledCamera_IsCollected()
        {
            UnityEngine.Camera camera = RegisterNewViewer("viewer");
            Assert.That(Collect(), Has.Member(camera));
        }

        [Test]
        public void Viewer_DuplicateRegister_IsCollectedOnce()
        {
            UnityEngine.Camera camera = RegisterNewViewer("viewer");
            BasisMirrorViewerRegistry.Register(camera);
            Assert.That(Collect().FindAll(c => c == camera), Has.Count.EqualTo(1));
        }

        [Test]
        public void Viewer_NullRegisterAndUnregister_DoNotThrow()
        {
            Assert.DoesNotThrow(() => BasisMirrorViewerRegistry.Register(null));
            Assert.DoesNotThrow(() => BasisMirrorViewerRegistry.Unregister(null));
        }

        [Test]
        public void Viewer_DisabledCamera_IsNotCollected()
        {
            UnityEngine.Camera camera = RegisterNewViewer("viewer");
            camera.enabled = false;
            Assert.That(Collect(), Has.No.Member(camera));
        }

        [Test]
        public void Viewer_InactiveGameObject_IsNotCollected()
        {
            UnityEngine.Camera camera = RegisterNewViewer("viewer");
            camera.gameObject.SetActive(false);
            Assert.That(Collect(), Has.No.Member(camera));
        }

        [Test]
        public void Viewer_DestroyedCamera_IsPrunedWithoutThrowing()
        {
            UnityEngine.Camera camera = RegisterNewViewer("viewer");
            Object.DestroyImmediate(camera.gameObject);

            List<UnityEngine.Camera> collected = null;
            Assert.DoesNotThrow(() => collected = Collect());
            Assert.That(collected.TrueForAll(c => c != null), Is.True,
                "destroyed cameras must be pruned, never handed to callers.");
        }

        [Test]
        public void Viewer_Unregistered_IsNotCollected()
        {
            UnityEngine.Camera camera = RegisterNewViewer("viewer");
            BasisMirrorViewerRegistry.Unregister(camera);
            Assert.That(Collect(), Has.No.Member(camera));
        }

        [Test]
        public void Reflection_NullCamera_IsNotReflectionCamera()
        {
            Assert.That(BasisMirrorReflectionCamera.IsReflectionCamera(null), Is.False);
        }

        [Test]
        public void Reflection_UnmarkedCamera_IsNotReflectionCamera()
        {
            UnityEngine.Camera camera = CreateCamera("plain");
            Assert.That(BasisMirrorReflectionCamera.IsReflectionCamera(camera), Is.False);
        }

        [Test]
        public void Reflection_MarkedCamera_RegistersAndUnregistersThroughLifecycle()
        {
            UnityEngine.Camera camera = CreateCamera("marked");
            BasisMirrorReflectionCamera marker = camera.gameObject.AddComponent<BasisMirrorReflectionCamera>();

            InvokeLifecycle(marker, "Awake");
            Assert.That(BasisMirrorReflectionCamera.IsReflectionCamera(camera), Is.True,
                "a camera carrying the marker must be identified as a reflection source.");

            InvokeLifecycle(marker, "OnDestroy");
            Assert.That(BasisMirrorReflectionCamera.IsReflectionCamera(camera), Is.False,
                "a destroyed marker must release its camera from the reflection set.");
        }

        private static void InvokeLifecycle(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"expected private lifecycle method {methodName} to exist.");
            method.Invoke(target, null);
        }
    }
}
