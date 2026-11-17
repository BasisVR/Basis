using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Basis.BasisUI;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Framework.Tests
{
    [NoAutoStaticsCleanup]
    public class BasisOneHandedTests
    {
        private static readonly FieldInfo TrackedRoleField = typeof(BasisInput).GetField("trackedRole", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly PropertyInfo LocalPlayerInstance = typeof(BasisLocalPlayer).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        private readonly List<GameObject> spawned = new List<GameObject>();
        private BasisLocalPlayer savedPlayer;
        private BasisDeviceManagement savedManagement;
        private BasisLocalCharacterDriver driver;
        private string savedMode;
        private class StubController : BasisInputController
        {
            public override void LateDoPollData() { }
            public override void ShowTrackedVisual() { }
            public override void PlayHaptic(float duration = 0.25f, float amplitude = 0.5f, float frequency = 0.5f) { }
            public override void PlaySoundEffect(string SoundEffectName, float Volume) { }
        }
        [SetUp]
        public void SetUp()
        {
            Assert.That(TrackedRoleField, Is.Not.Null, "BasisInput.trackedRole was renamed; this test needs updating.");
            Assert.That(LocalPlayerInstance, Is.Not.Null, "BasisLocalPlayer.Instance was renamed; this test needs updating.");
            savedPlayer = BasisLocalPlayer.Instance;
            savedManagement = BasisDeviceManagement.Instance;
            savedMode = BasisSettingsDefaults.OneHandedMode.RawValue;
            BasisLocalPlayer player = (BasisLocalPlayer)FormatterServices.GetUninitializedObject(typeof(BasisLocalPlayer));
            driver = new BasisLocalCharacterDriver();
            player.LocalCharacterDriver = driver;
            LocalPlayerInstance.SetValue(null, player);
            BasisDeviceManagement management = (BasisDeviceManagement)FormatterServices.GetUninitializedObject(typeof(BasisDeviceManagement));
            management.AllInputDevices = new BasisObservableList<BasisInput>();
            management.CurrentMode = BasisConstants.OpenXRLoader;
            BasisDeviceManagement.Instance = management;
            BasisOneHanded.Invalidate();
        }
        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in spawned)
            {
                Object.DestroyImmediate(gameObject);
            }
            spawned.Clear();
            BasisSettingsDefaults.OneHandedMode.RawValue = savedMode;
            BasisDeviceManagement.Instance = savedManagement;
            LocalPlayerInstance.SetValue(null, savedPlayer);
            BasisOneHanded.Invalidate();
        }
        private T Spawn<T>(BasisBoneTrackedRole role) where T : BasisInput
        {
            GameObject deviceObject = new GameObject($"One handed {role}");
            spawned.Add(deviceObject);
            T device = deviceObject.AddComponent<T>();
            device.hasRoleAssigned = true;
            TrackedRoleField.SetValue(device, role);
            BasisDeviceManagement.Instance.AllInputDevices.Add(device);
            return device;
        }
        private static bool Resolve(string mode, bool isVR, out BasisBoneTrackedRole role) => BasisOneHanded.Resolve(mode, isVR, BasisDeviceManagement.Instance.AllInputDevices, out role);
        [Test]
        public void OffAndDesktopNeverResolve()
        {
            Spawn<StubController>(BasisBoneTrackedRole.LeftHand);
            Assert.That(Resolve(BasisOneHanded.Off, true, out _), Is.False);
            Assert.That(Resolve(BasisOneHanded.Left, false, out _), Is.False);
            Assert.That(Resolve(BasisOneHanded.Auto, false, out _), Is.False);
        }
        [Test]
        public void AChosenHandResolvesWithoutAnyController()
        {
            Assert.That(Resolve(BasisOneHanded.Left, true, out BasisBoneTrackedRole left), Is.True);
            Assert.That(left, Is.EqualTo(BasisBoneTrackedRole.LeftHand));
            Assert.That(Resolve(BasisOneHanded.Right, true, out BasisBoneTrackedRole right), Is.True);
            Assert.That(right, Is.EqualTo(BasisBoneTrackedRole.RightHand));
        }
        [Test]
        public void AutoPicksTheOnlyController()
        {
            Assert.That(Resolve(BasisOneHanded.Auto, true, out _), Is.False);
            Spawn<StubController>(BasisBoneTrackedRole.LeftHand);
            Assert.That(Resolve(BasisOneHanded.Auto, true, out BasisBoneTrackedRole role), Is.True);
            Assert.That(role, Is.EqualTo(BasisBoneTrackedRole.LeftHand));
            Spawn<StubController>(BasisBoneTrackedRole.RightHand);
            Assert.That(Resolve(BasisOneHanded.Auto, true, out _), Is.False);
        }
        [Test]
        public void AutoSkipsControllersThatCannotDriveActions()
        {
            Spawn<StubController>(BasisBoneTrackedRole.RightHand);
            StubController muted = Spawn<StubController>(BasisBoneTrackedRole.LeftHand);
            muted.IgnoredParts = BasisDeviceIgnore.Buttons;
            Spawn<BasisInputXRSimulate>(BasisBoneTrackedRole.LeftHand);
            Assert.That(Resolve(BasisOneHanded.Auto, true, out BasisBoneTrackedRole role), Is.True);
            Assert.That(role, Is.EqualTo(BasisBoneTrackedRole.RightHand));
        }
        [Test]
        public void StickForwardWalksAndSidewaysTurns()
        {
            BasisActionDriver.OneHandedLocomotion(Vector2.up, false, false);
            Assert.That(driver.MovementVector.x, Is.EqualTo(0f));
            Assert.That(driver.MovementVector.y, Is.EqualTo(1f).Within(0.001f));
            Assert.That(driver.Rotation.x, Is.EqualTo(0f));
            BasisActionDriver.OneHandedLocomotion(new Vector2(1f, 0.3f), false, false);
            Assert.That(driver.MovementVector, Is.EqualTo(Vector2.zero));
            Assert.That(driver.Rotation.x, Is.GreaterThan(0.5f));
            BasisActionDriver.OneHandedLocomotion(Vector2.down, false, false);
            Assert.That(driver.MovementVector.y, Is.EqualTo(-1f).Within(0.001f));
        }
        [Test]
        public void MenusAndTextFieldsTakeTheStick()
        {
            BasisActionDriver.OneHandedLocomotion(new Vector2(1f, 0f), true, false);
            Assert.That(driver.Rotation.x, Is.EqualTo(1f).Within(0.001f));
            BasisActionDriver.OneHandedLocomotion(Vector2.up, true, false);
            Assert.That(driver.MovementVector, Is.EqualTo(Vector2.zero));
            BasisActionDriver.OneHandedLocomotion(new Vector2(1f, 0f), false, true);
            Assert.That(driver.Rotation, Is.EqualTo(Vector2.zero));
        }
        [Test]
        public void TheOtherHandStopsDrivingThePlayer()
        {
            BasisSettingsDefaults.OneHandedMode.RawValue = BasisOneHanded.Right;
            BasisOneHanded.Invalidate();
            bool alreadyBound = false;
            IReadOnlyList<BasisActionDriver.ActionId> existing = BasisActionDriver.GetActionsForRole(BasisBoneTrackedRole.LeftHand);
            for (int index = 0; index < existing.Count; index++)
            {
                alreadyBound |= existing[index] == BasisActionDriver.ActionId.SetMovementVectorFromPrimary2DAxis;
            }
            BasisActionDriver.Bind(BasisActionDriver.ActionId.SetMovementVectorFromPrimary2DAxis, BasisBoneTrackedRole.LeftHand);
            try
            {
                BasisInputState current = new BasisInputState { Primary2DAxisRaw = Vector2.up }, last = new BasisInputState();
                BasisActionDriver.UpdatePlayerControl(BasisBoneTrackedRole.LeftHand, ref current, ref last);
                Assert.That(driver.MovementVector, Is.EqualTo(Vector2.zero));
                BasisSettingsDefaults.OneHandedMode.RawValue = BasisOneHanded.Off;
                BasisDeviceManagement.Instance.CurrentMode = BasisConstants.Desktop;
                BasisOneHanded.Invalidate();
                BasisActionDriver.UpdatePlayerControl(BasisBoneTrackedRole.LeftHand, ref current, ref last);
                Assert.That(driver.MovementVector, Is.EqualTo(Vector2.up));
            }
            finally
            {
                if (!alreadyBound)
                {
                    BasisActionDriver.Unbind(BasisActionDriver.ActionId.SetMovementVectorFromPrimary2DAxis, BasisBoneTrackedRole.LeftHand);
                }
            }
        }
    }
}
