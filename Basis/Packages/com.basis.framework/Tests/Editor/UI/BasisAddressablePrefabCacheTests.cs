using System.Collections.Generic;
using System.Text.RegularExpressions;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisAddressablePrefabCacheTests
    {
        private const string KnownPath = "test://known";
        private const string SecondPath = "test://second";
        private const string MissingPath = "test://missing";

        private readonly List<GameObject> _cleanup = new List<GameObject>();
        private GameObject _template;
        private GameObject _secondTemplate;
        private int _loads;

        [SetUp]
        public void SetUp()
        {
            _loads = 0;
            _template = CreateTemplate("Template");
            _secondTemplate = CreateTemplate("SecondTemplate");
            BasisAddressablePrefabCache.ResetForTests(path =>
            {
                _loads++;
                if (path == KnownPath) return _template;
                if (path == SecondPath) return _secondTemplate;
                return null;
            });
        }

        [TearDown]
        public void TearDown()
        {
            BasisAddressablePrefabCache.ResetForTests(null);
            foreach (GameObject go in _cleanup)
            {
                if (go)
                {
                    Object.DestroyImmediate(go);
                }
            }
            _cleanup.Clear();
            if (_template)
            {
                Object.DestroyImmediate(_template);
            }
            if (_secondTemplate)
            {
                Object.DestroyImmediate(_secondTemplate);
            }
        }

        private GameObject CreateTemplate(string name)
        {
            GameObject template = new GameObject(name, typeof(RectTransform), typeof(PanelElementDescriptor));
            GameObject child = new GameObject("Child", typeof(RectTransform));
            child.transform.SetParent(template.transform, false);
            return template;
        }

        private RectTransform CreateParent()
        {
            GameObject parent = new GameObject("Parent", typeof(RectTransform));
            _cleanup.Add(parent);
            return (RectTransform)parent.transform;
        }

        [Test]
        public void GetPrefab_LoadsOncePerPath()
        {
            BasisAddressablePrefabCache.GetPrefab(KnownPath);
            BasisAddressablePrefabCache.GetPrefab(KnownPath);
            BasisAddressablePrefabCache.GetPrefab(SecondPath);
            Assert.That(_loads, Is.EqualTo(2));
        }

        [Test]
        public void Instantiate_ProducesDistinctInstances()
        {
            RectTransform parent = CreateParent();
            GameObject first = BasisAddressablePrefabCache.Instantiate(KnownPath, parent);
            GameObject second = BasisAddressablePrefabCache.Instantiate(KnownPath, parent);
            _cleanup.Add(first);
            _cleanup.Add(second);
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(_loads, Is.EqualTo(1));
        }

        [Test]
        public void Instantiate_MatchesDirectInstantiateControl()
        {
            RectTransform parent = CreateParent();
            GameObject control = Object.Instantiate(_template, parent, false);
            GameObject cached = BasisAddressablePrefabCache.Instantiate(KnownPath, parent);
            _cleanup.Add(control);
            _cleanup.Add(cached);

            Assert.That(cached.name, Is.EqualTo(control.name));
            Assert.That(cached.transform.parent, Is.SameAs(parent));
            Assert.That(cached.transform.localPosition, Is.EqualTo(control.transform.localPosition));
            Assert.That(cached.transform.localRotation, Is.EqualTo(control.transform.localRotation));
            Assert.That(cached.transform.localScale, Is.EqualTo(control.transform.localScale));
            Assert.That(cached.activeSelf, Is.EqualTo(control.activeSelf));
            Assert.That(cached.transform.childCount, Is.EqualTo(control.transform.childCount));

            Component[] controlComponents = control.GetComponents<Component>();
            Component[] cachedComponents = cached.GetComponents<Component>();
            Assert.That(cachedComponents.Length, Is.EqualTo(controlComponents.Length));
            for (int i = 0; i < controlComponents.Length; i++)
            {
                Assert.That(cachedComponents[i].GetType(), Is.EqualTo(controlComponents[i].GetType()));
            }
        }

        [Test]
        public void Instantiate_WithNullOrMissingPath_ReturnsNull()
        {
            Assert.That(BasisAddressablePrefabCache.Instantiate(null, null), Is.Null);
            LogAssert.Expect(LogType.Error, new Regex("Failed to load Addressable"));
            Assert.That(BasisAddressablePrefabCache.Instantiate(MissingPath, null), Is.Null);
        }

        [Test]
        public void CreateNew_RunsTheCreateEvent()
        {
            RectTransform parent = CreateParent();
            PanelElementDescriptor element = AddressableUIInstanceBase.CreateNew<PanelElementDescriptor>(KnownPath, parent);
            _cleanup.Add(element.gameObject);
            Assert.That(element, Is.Not.Null);
            Assert.That(element.HasRunCreateEvent, Is.True);
        }

        [Test]
        public void CreateNew_WithMissingPath_ReturnsNull()
        {
            RectTransform parent = CreateParent();
            LogAssert.Expect(LogType.Error, new Regex("Failed to load Addressable"));
            PanelElementDescriptor element = AddressableUIInstanceBase.CreateNew<PanelElementDescriptor>(MissingPath, parent);
            Assert.That(element, Is.Null);
        }

        [Test]
        public void ReleaseInstance_ActuallyDestroysTheInstance()
        {
            RectTransform parent = CreateParent();
            PanelElementDescriptor element = AddressableUIInstanceBase.CreateNew<PanelElementDescriptor>(KnownPath, parent);
            GameObject go = element.gameObject;
            int released = 0;
            element.OnInstanceReleased += () => released++;
            element.ReleaseInstance();
            Assert.That(go == null, Is.True, "released element was not destroyed; a plain Instantiate clone must be destroyed directly, Addressables.ReleaseInstance would silently leak it");
            Assert.That(released, Is.EqualTo(1));
        }

        [Test]
        public void Clear_ForcesReload()
        {
            BasisAddressablePrefabCache.GetPrefab(KnownPath);
            BasisAddressablePrefabCache.Clear();
            BasisAddressablePrefabCache.GetPrefab(KnownPath);
            Assert.That(_loads, Is.EqualTo(2));
        }

        [Test]
        public void DestroyedCacheEntry_Reloads()
        {
            BasisAddressablePrefabCache.GetPrefab(KnownPath);
            Object.DestroyImmediate(_template);
            _template = CreateTemplate("Template");
            GameObject reloaded = BasisAddressablePrefabCache.GetPrefab(KnownPath);
            Assert.That(reloaded, Is.SameAs(_template));
            Assert.That(_loads, Is.EqualTo(2));
        }

        [Test]
        public void DestroyInstance_InEditMode_DestroysImmediately()
        {
            GameObject go = new GameObject("Doomed");
            BasisAddressablePrefabCache.DestroyInstance(go);
            Assert.That(go == null, Is.True);
        }
    }
}
