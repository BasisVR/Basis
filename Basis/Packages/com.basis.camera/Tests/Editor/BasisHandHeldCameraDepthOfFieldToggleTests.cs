using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityCamera = UnityEngine.Camera;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Depth of field is switched on from two surfaces — the toggle on the camera prop itself and
    /// the one in the camera settings panel — and by three writers that touch neither: the blur
    /// style dropdown, the mode presets and a settings load. The live effect is the state; both
    /// switches only show it. These pin that, because the failure is silent: the picture changes
    /// and one of the two switches goes on reading the opposite.
    ///
    /// Awake never runs here — outside play mode Unity does not invoke it for a plain MonoBehaviour —
    /// so the handler arrives with its field initializers applied and no listener wired.
    /// </summary>
    public class BasisHandHeldCameraDepthOfFieldToggleTests
    {
        private const string PrefabPath = "Packages/com.basis.camera/Prefabs/Player Held Camera.prefab";

        private GameObject _go;
        private GameObject _captureGo;
        private GameObject _toggleGo;
        private BasisHandHeldCamera _camera;
        private BasisHandHeldCameraUI _ui;
        private BasisDepthOfFieldInteractionHandler _handler;
        private Toggle _toggle;
        private DepthOfField _dof;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("HandHeldCameraUnderTest");
            _camera = _go.AddComponent<BasisHandHeldCamera>();
            _camera.HHC = _camera;

            _captureGo = new GameObject("CaptureCamera");
            _camera.captureCamera = _captureGo.AddComponent<UnityCamera>();

            _dof = ScriptableObject.CreateInstance<DepthOfField>();
            _camera.MetaData.depthOfField = _dof;

            _ui = new BasisHandHeldCameraUI { HHC = _camera };
            _camera.HandHeld = _ui;

            _toggleGo = new GameObject("DepthOfFieldToggle");
            _toggle = _toggleGo.AddComponent<Toggle>();

            _handler = _go.AddComponent<BasisDepthOfFieldInteractionHandler>();
            _handler.cameraController = _camera;
            _handler.depthOfFieldToggle = _toggle;
            _camera.BasisDOFInteractionHandler = _handler;

            _dof.active = false;
            _toggle.SetIsOnWithoutNotify(false);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_captureGo != null) Object.DestroyImmediate(_captureGo);
            if (_toggleGo != null) Object.DestroyImmediate(_toggleGo);
            if (_dof != null) Object.DestroyImmediate(_dof);
        }

        [Test]
        public void SwitchingItOnFromThePanelMovesTheCamerasOwnToggle()
        {
            _handler.SetDoFState(true);

            Assert.That(_dof.active, Is.True);
            Assert.That(_toggle.isOn, Is.True,
                "The panel writes through the same handler the prop's toggle does, so the two cannot disagree.");
        }

        [Test]
        public void SwitchingItOffFromThePanelMovesTheCamerasOwnToggle()
        {
            _handler.SetDoFState(true);

            _handler.SetDoFState(false);

            Assert.That(_dof.active, Is.False);
            Assert.That(_toggle.isOn, Is.False);
        }

        [Test]
        public void SwitchingItOnPromotesAStoredStyleOfOffToBokeh()
        {
            _dof.mode.value = DepthOfFieldMode.Off;

            _handler.SetDoFState(true);

            Assert.That(_dof.mode.value, Is.EqualTo(DepthOfFieldMode.Bokeh),
                "On with a style of Off renders nothing, which reads as a switch that does not work. " +
                "The panel re-seeds its style dropdown from this.");
        }

        [Test]
        public void TheBlurStyleDropdownPickingOffAlsoClearsTheCamerasToggle()
        {
            _handler.SetDoFState(true);
            Assume.That(_toggle.isOn, Is.True);

            _ui.SetDoFMode(0);

            Assert.That(_dof.active, Is.False);
            Assert.That(_toggle.isOn, Is.False,
                "SetDoFMode owns the on/off as well as the style, and it never reached the prop's toggle.");
        }

        [Test]
        public void TheBlurStyleDropdownPickingAStyleSwitchesTheCamerasToggleBackOn()
        {
            _ui.SetDoFMode(0);
            Assume.That(_toggle.isOn, Is.False);

            _ui.SetDoFMode(1);

            Assert.That(_dof.active, Is.True);
            Assert.That(_toggle.isOn, Is.True);
        }

        [Test]
        public void TheToggleFollowsAWriteThatWentStraightToTheEffect()
        {
            _handler.SetDoFState(true);
            _dof.active = false;

            _handler.SyncToggleFromState();

            Assert.That(_toggle.isOn, Is.False,
                "The mode presets and a settings load write the effect; the toggle has to follow the camera.");
        }

        [Test]
        public void ReSeedingThePropFromStateCarriesTheToggle()
        {
            _dof.active = true;
            _toggle.SetIsOnWithoutNotify(false);

            _ui.SyncPropControlsFromState();

            Assert.That(_toggle.isOn, Is.True,
                "The panel re-seeds the prop's HUD every tick, and this switch is part of that HUD.");
        }

        [Test]
        public void TheEffectIsTheStateNotTheWidget()
        {
            _dof.active = true;
            _toggle.SetIsOnWithoutNotify(false);

            Assert.That(_handler.IsDoFEnabled, Is.True);
        }

        [Test]
        public void AHandlerWithNoToggleStillSwitchesTheEffect()
        {
            _handler.depthOfFieldToggle = null;

            _handler.SetDoFState(true);

            Assert.That(_dof.active, Is.True, "The panel's switch must work on a camera with no HUD toggle wired.");
        }

        [Test]
        public void AHandlerWithNoDepthOfFieldEffectIsInert()
        {
            _camera.MetaData.depthOfField = null;

            Assert.That(() => _handler.SetDoFState(true), Throws.Nothing);
            Assert.That(_handler.IsDoFEnabled, Is.False);
        }

        [Test]
        public void ThePrefabWiresTheCamerasOwnDepthOfFieldToggle()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null, PrefabPath);

            BasisDepthOfFieldInteractionHandler handler = prefab.GetComponentInChildren<BasisDepthOfFieldInteractionHandler>(true);
            Assert.That(handler, Is.Not.Null);
            Assert.That(handler.depthOfFieldToggle, Is.Not.Null,
                "The panel's switch is mirrored onto this one, so an unwired field is a switch that silently disagrees.");
        }
    }
}
