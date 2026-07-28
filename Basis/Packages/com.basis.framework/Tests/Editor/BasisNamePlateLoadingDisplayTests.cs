using Basis.Scripts.Device_Management;
using Basis.Scripts.UI.NamePlate;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// End-to-end regression tests for the nameplate avatar-loading display: a real
    /// BasisRemoteNamePlate component with a real TMP label and SpriteRenderer bar,
    /// driven through the real <see cref="BasisRemoteNamePlate.ProgressReport"/> path —
    /// including the BasisDeviceManagement main-thread queue the reports marshal
    /// through (drained manually here since no event driver runs in edit mode).
    /// Pins show-on-first-report, bar sizing every report, quantized label rewrites,
    /// hide-on-completion, the culled state tracking without object writes, and the
    /// (pre-existing) inactive-plate guard.
    /// </summary>
    public class BasisNamePlateLoadingDisplayTests
    {
        private GameObject _root;
        private BasisRemoteNamePlate _plate;
        private Texture2D _texture;
        private Sprite _sprite;

        [SetUp]
        public void SetUp()
        {
            DrainMainThreadQueue();

            _root = new GameObject("PlateUnderTest");
            _plate = _root.AddComponent<BasisRemoteNamePlate>();

            GameObject textGo = new GameObject("LoadingText");
            textGo.transform.SetParent(_root.transform, false);
            _plate.LoadingText = textGo.AddComponent<TextMeshPro>();

            GameObject barGo = new GameObject("LoadingBar");
            barGo.transform.SetParent(_root.transform, false);
            SpriteRenderer bar = barGo.AddComponent<SpriteRenderer>();
            _texture = new Texture2D(4, 4);
            _sprite = Sprite.Create(_texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            bar.sprite = _sprite;
            bar.drawMode = SpriteDrawMode.Sliced;
            _plate.LoadingBar = bar;

            // Mirror the prefab's resting state: overlays hidden until a report shows them.
            textGo.SetActive(false);
            barGo.SetActive(false);
        }

        [TearDown]
        public void TearDown()
        {
            DrainMainThreadQueue();
            if (_root != null) Object.DestroyImmediate(_root);
            if (_sprite != null) Object.DestroyImmediate(_sprite);
            if (_texture != null) Object.DestroyImmediate(_texture);
        }

        private static void DrainMainThreadQueue()
        {
            while (BasisDeviceManagement.mainThreadActions.TryDequeue(out System.Action action))
            {
                action?.Invoke();
            }
        }

        private void Report(float progress, string info)
        {
            _plate.ProgressReport("test", progress, info);
            DrainMainThreadQueue();
        }

        [Test]
        public void MidLoadReport_ShowsBarAndText()
        {
            Report(50f, "Downloading 50%");

            Assert.IsTrue(_plate.HasProgressBarVisible);
            Assert.IsTrue(_plate.LoadingText.gameObject.activeSelf);
            Assert.IsTrue(_plate.LoadingBar.gameObject.activeSelf);
            Assert.AreEqual("Downloading 50%", _plate.LoadingText.text);
            Assert.AreEqual(25f, _plate.LoadingBar.size.x, 1e-3f);
        }

        [Test]
        public void BarTracksEveryReport_TextRewritesPerBucket()
        {
            Report(41f, "Downloading 41%");
            Assert.AreEqual("Downloading 41%", _plate.LoadingText.text);
            Assert.AreEqual(20.5f, _plate.LoadingBar.size.x, 1e-3f);

            // Same 5% bucket: the bar moves, the label deliberately does not re-tessellate.
            Report(44f, "Downloading 44%");
            Assert.AreEqual("Downloading 41%", _plate.LoadingText.text);
            Assert.AreEqual(22f, _plate.LoadingBar.size.x, 1e-3f);

            // New bucket: label catches up.
            Report(46f, "Downloading 46%");
            Assert.AreEqual("Downloading 46%", _plate.LoadingText.text);
        }

        [Test]
        public void CompletionReport_HidesBoth()
        {
            Report(50f, "Downloading 50%");
            Report(100f, "Avatar ready");

            Assert.IsFalse(_plate.HasProgressBarVisible);
            Assert.IsFalse(_plate.LoadingText.gameObject.activeSelf);
            Assert.IsFalse(_plate.LoadingBar.gameObject.activeSelf);
        }

        [Test]
        public void InstantLoad_OnlyCompletionReport_StaysHidden()
        {
            Report(100f, "Avatar ready");

            Assert.IsFalse(_plate.HasProgressBarVisible);
            Assert.IsFalse(_plate.LoadingText.gameObject.activeSelf);
            Assert.IsFalse(_plate.LoadingBar.gameObject.activeSelf);
        }

        [Test]
        public void NewLoadAfterCompletion_ShowsAgainWithFreshText()
        {
            Report(60f, "Downloading 60%");
            Report(100f, "Avatar ready");
            Report(10f, "Downloading 10%");

            Assert.IsTrue(_plate.LoadingText.gameObject.activeSelf);
            Assert.IsTrue(_plate.LoadingBar.gameObject.activeSelf);
            Assert.AreEqual("Downloading 10%", _plate.LoadingText.text);
            Assert.AreEqual(5f, _plate.LoadingBar.size.x, 1e-3f);
        }

        [Test]
        public void CulledPlate_TracksStateWithoutTouchingObjects_ThenShowsOnReadmission()
        {
            Report(30f, "Downloading 30%");
            Assert.IsTrue(_plate.LoadingBar.gameObject.activeSelf);

            _plate.SetLoadingOverlayCulled(true);
            Assert.IsFalse(_plate.LoadingText.gameObject.activeSelf);
            Assert.IsFalse(_plate.LoadingBar.gameObject.activeSelf);

            // Culled: state advances, objects stay untouched.
            Report(70f, "Downloading 70%");
            Assert.IsTrue(_plate.HasProgressBarVisible);
            Assert.IsFalse(_plate.LoadingBar.gameObject.activeSelf);
            Assert.AreEqual("Downloading 30%", _plate.LoadingText.text);

            // Readmitted: objects return and the next report refreshes the stale label.
            _plate.SetLoadingOverlayCulled(false);
            Assert.IsTrue(_plate.LoadingText.gameObject.activeSelf);
            Assert.IsTrue(_plate.LoadingBar.gameObject.activeSelf);
            Report(72f, "Downloading 72%");
            Assert.AreEqual("Downloading 72%", _plate.LoadingText.text);
            Assert.AreEqual(36f, _plate.LoadingBar.size.x, 1e-3f);
        }

        [Test]
        public void InactivePlate_SkipsReports()
        {
            // Pre-existing guard: reports for a deactivated plate are dropped, not queued.
            _root.SetActive(false);
            Report(50f, "Downloading 50%");

            Assert.IsFalse(_plate.HasProgressBarVisible);
            Assert.IsFalse(_plate.LoadingText.gameObject.activeSelf);
        }
    }
}
